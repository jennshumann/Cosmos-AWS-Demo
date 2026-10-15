#!/usr/bin/env python3
"""Export documents from a Cosmos DB (SQL API) container and import them into a DynamoDB table.

Source is either a live container (azure-cosmos SDK, paged read_all_items) or a JSON export file
(e.g. from the Cosmos DB Data Migration Tool / Azure Data Factory). Each document is:

  * stripped of Cosmos system properties (_rid, _self, _etag, _attachments, _ts, _lsn),
  * converted to DynamoDB types (floats become Decimal, as boto3 requires),
  * optionally given lower-cased copies of string attributes (--lowercase description -> descriptionLower),
  * checked for the target table's key attributes and for duplicate keys,

and written with boto3's batch_writer (25-item BatchWriteItem calls with automatic UnprocessedItems retry).

Examples
  # Products: Cosmos emulator -> DynamoDB Local
  python cosmos_to_dynamodb.py --cosmos-endpoint http://localhost:8081 --cosmos-key "$KEY" \\
      --database cmh-cosmos-demo-db --container products --table Products \\
      --dynamodb-endpoint http://localhost:8000 --lowercase description

  # Orders: Azure account (endpoint/key looked up with `az`) -> deployed table
  python cosmos_to_dynamodb.py --cosmos-account cmh-cosmos-demo-acct --resource-group COSMOS-DEMO \\
      --database cmh-cosmos-demo-db --container orders --table <OrdersTableName>
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from decimal import Decimal
from typing import Any, Iterable, Iterator

COSMOS_SYSTEM_PROPERTIES = {"_rid", "_self", "_etag", "_attachments", "_ts", "_lsn"}


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    src = p.add_argument_group("source (live Cosmos DB container or --input-file)")
    src.add_argument("--input-file", help="JSON array (or JSON Lines) export of the container instead of a live read")
    src.add_argument("--cosmos-endpoint", default=os.environ.get("COSMOS_ENDPOINT"))
    src.add_argument("--cosmos-key", default=os.environ.get("COSMOS_KEY"))
    src.add_argument("--cosmos-account", default=os.environ.get("COSMOS_ACCOUNT_NAME"),
                     help="look up endpoint and key with the Azure CLI when --cosmos-endpoint/--cosmos-key are unset")
    src.add_argument("--resource-group", default=os.environ.get("COSMOS_RESOURCE_GROUP"))
    src.add_argument("--database", default=os.environ.get("COSMOS_DATABASE_NAME"))
    src.add_argument("--container", default=os.environ.get("COSMOS_CONTAINER_NAME"))
    src.add_argument("--page-size", type=int, default=100)

    dst = p.add_argument_group("target")
    dst.add_argument("--table", required=True, help="DynamoDB table name")
    dst.add_argument("--dynamodb-endpoint", default=os.environ.get("DYNAMODB_ENDPOINT"),
                     help="e.g. http://localhost:8000 for DynamoDB Local")
    dst.add_argument("--region", default=os.environ.get("AWS_REGION") or os.environ.get("AWS_DEFAULT_REGION"))

    xf = p.add_argument_group("transform")
    xf.add_argument("--lowercase", action="append", default=[], metavar="ATTR",
                    help="also write <ATTR>Lower = lower-cased ATTR (repeatable); use 'description' for Products")
    xf.add_argument("--keep-system-properties", action="store_true", help="keep _rid/_etag/_ts/...")

    p.add_argument("--dry-run", action="store_true", help="transform and validate, but don't write to DynamoDB")
    p.add_argument("--export-file", help="also write the transformed documents to this JSON file")
    args = p.parse_args(argv)

    if not args.input_file and not (args.database and args.container):
        p.error("--database and --container are required unless --input-file is given")
    return args


def az_lookup(account: str, resource_group: str | None) -> tuple[str, str]:
    def az(*cmd: str) -> str:
        return subprocess.run(["az", *cmd, "-o", "tsv"], check=True, capture_output=True, text=True).stdout.strip()

    if not resource_group:
        resource_group = az("cosmosdb", "list", "--query", f"[?name=='{account}'].resourceGroup | [0]")
        if not resource_group:
            sys.exit(f"error: Cosmos DB account {account} not found in the current subscription")
    endpoint = az("cosmosdb", "show", "-n", account, "-g", resource_group, "--query", "documentEndpoint")
    key = az("cosmosdb", "keys", "list", "-n", account, "-g", resource_group, "--type", "keys",
             "--query", "primaryReadonlyMasterKey")
    return endpoint, key


def read_cosmos(args: argparse.Namespace) -> Iterator[dict[str, Any]]:
    from azure.cosmos import CosmosClient

    endpoint, key = args.cosmos_endpoint, args.cosmos_key
    if not (endpoint and key):
        if not args.cosmos_account:
            sys.exit("error: set --cosmos-endpoint and --cosmos-key, or --cosmos-account to look them up with az")
        endpoint, key = az_lookup(args.cosmos_account, args.resource_group)

    # Azure documentEndpoint values end with "/"; without it some endpoints (e.g. the vNext emulator) reject "//dbs".
    client = CosmosClient(endpoint.rstrip("/") + "/", credential=key)
    container = client.get_database_client(args.database).get_container_client(args.container)
    # read_all_items pages through the container's feed (continuation tokens handled by the SDK).
    yield from container.read_all_items(max_item_count=args.page_size)


def read_file(path: str) -> Iterator[dict[str, Any]]:
    with open(path, encoding="utf-8") as f:
        text = f.read().strip()
    if text.startswith("["):
        yield from json.loads(text)
    else:
        for line in text.splitlines():
            if line.strip():
                yield json.loads(line)


def transform(doc: dict[str, Any], lowercase: Iterable[str], keep_system: bool) -> dict[str, Any]:
    if not keep_system:
        doc = {k: v for k, v in doc.items() if k not in COSMOS_SYSTEM_PROPERTIES}
    for attr in lowercase:
        if isinstance(doc.get(attr), str):
            doc[f"{attr}Lower"] = doc[attr].lower()
    # Round-trip through JSON so every float (at any depth) becomes Decimal for boto3.
    return json.loads(json.dumps(doc), parse_float=Decimal)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)

    import boto3

    dynamodb = boto3.resource("dynamodb", endpoint_url=args.dynamodb_endpoint, region_name=args.region)
    table = dynamodb.Table(args.table)
    description = table.meta.client.describe_table(TableName=args.table)["Table"]
    key_names = [k["AttributeName"] for k in description["KeySchema"]]
    if description.get("StreamSpecification", {}).get("StreamEnabled"):
        print(f"warning: {args.table} has a stream enabled. Every imported item becomes a stream record; for the "
              "Orders table, disable the stream consumer first (cdk deploy -c ordersStreamEnabled=false) or "
              "imported orders will decrement inventory again.", file=sys.stderr)

    source = read_file(args.input_file) if args.input_file else read_cosmos(args)
    print(f"Importing {'file ' + args.input_file if args.input_file else args.database + '/' + args.container} "
          f"-> {args.table} (key: {', '.join(key_names)}){' [dry run]' if args.dry_run else ''}")

    seen: set[tuple[Any, ...]] = set()
    exported: list[dict[str, Any]] = []
    written = skipped = 0

    def items() -> Iterator[dict[str, Any]]:
        nonlocal skipped
        for raw in source:
            item = transform(raw, args.lowercase, args.keep_system_properties)
            missing = [k for k in key_names if item.get(k) in (None, "")]
            if missing:
                print(f"skip (missing key {', '.join(missing)}): id={item.get('id')!r}", file=sys.stderr)
                skipped += 1
                continue
            key = tuple(item[k] for k in key_names)
            if key in seen:
                # e.g. Orders: Cosmos only guarantees id uniqueness per partition (customerNumber), DynamoDB keys on id.
                sys.exit(f"error: duplicate DynamoDB key {dict(zip(key_names, key))}; "
                         "Cosmos ids are only unique per logical partition - choose a different table key")
            seen.add(key)
            if args.export_file:
                exported.append(item)
            yield item

    if args.dry_run:
        written = sum(1 for _ in items())
    else:
        with table.batch_writer(overwrite_by_pkeys=key_names) as batch:
            for item in items():
                batch.put_item(Item=item)
                written += 1

    if args.export_file:
        with open(args.export_file, "w", encoding="utf-8") as f:
            json.dump(exported, f, indent=2, default=lambda d: int(d) if d == d.to_integral_value() else float(d))

    print(f"{'Validated' if args.dry_run else 'Wrote'} {written} item(s), skipped {skipped}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
