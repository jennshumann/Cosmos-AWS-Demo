#!/usr/bin/env bash
#
# Upserts the product records in products.json into the Cosmos DB Products container.
#
# The Azure CLI has no data-plane command for writing documents, so `az` is used to look up
# the account endpoint, primary key, and container partition key path, and each document is
# then upserted through the Cosmos DB SQL REST API (master-key auth, x-ms-documentdb-is-upsert).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_VERSION="2018-12-31"

usage() {
    cat <<USAGE
Usage: $(basename "$0") [-a account] [-d database] [-c container] [-g resource-group] [-f file]

Options (each falls back to the environment variable shown):
  -a  Cosmos DB account name    COSMOS_ACCOUNT_NAME
  -d  Database name             COSMOS_DATABASE_NAME
  -c  Container name            COSMOS_CONTAINER_NAME
  -g  Resource group            COSMOS_RESOURCE_GROUP   (optional; looked up from the account if unset)
  -f  Seed file                 SEED_FILE               (default: $SCRIPT_DIR/products.json)
  -h  Show this help

Advanced (skip the Azure CLI lookups, e.g. for the Cosmos DB emulator):
  COSMOS_ENDPOINT, COSMOS_KEY      Account endpoint and key to use directly
  COSMOS_PARTITION_KEY_PATH        Partition key path (default: /category)
USAGE
}

die() { echo "error: $*" >&2; exit 1; }

ACCOUNT="${COSMOS_ACCOUNT_NAME:-}"
DATABASE="${COSMOS_DATABASE_NAME:-}"
CONTAINER="${COSMOS_CONTAINER_NAME:-}"
RESOURCE_GROUP="${COSMOS_RESOURCE_GROUP:-}"
SEED_FILE="${SEED_FILE:-$SCRIPT_DIR/products.json}"
ENDPOINT="${COSMOS_ENDPOINT:-}"
KEY="${COSMOS_KEY:-}"
PK_PATH="${COSMOS_PARTITION_KEY_PATH:-}"

while getopts ":a:d:c:g:f:h" opt; do
    case "$opt" in
        a) ACCOUNT="$OPTARG" ;;
        d) DATABASE="$OPTARG" ;;
        c) CONTAINER="$OPTARG" ;;
        g) RESOURCE_GROUP="$OPTARG" ;;
        f) SEED_FILE="$OPTARG" ;;
        h) usage; exit 0 ;;
        :) die "option -$OPTARG requires a value" ;;
        *) usage >&2; exit 1 ;;
    esac
done

for cmd in curl jq openssl base64 od; do
    command -v "$cmd" >/dev/null 2>&1 || die "'$cmd' is required but not installed"
done

[[ -n "$DATABASE" ]] || die "database name is required (-d or COSMOS_DATABASE_NAME)"
[[ -n "$CONTAINER" ]] || die "container name is required (-c or COSMOS_CONTAINER_NAME)"
[[ -f "$SEED_FILE" ]] || die "seed file not found: $SEED_FILE"
jq -e 'type == "array"' "$SEED_FILE" >/dev/null || die "seed file must contain a JSON array: $SEED_FILE"

if [[ -z "$ENDPOINT" || -z "$KEY" ]]; then
    [[ -n "$ACCOUNT" ]] || die "account name is required (-a or COSMOS_ACCOUNT_NAME)"
    command -v az >/dev/null 2>&1 || die "Azure CLI ('az') is required; run 'az login' first"

    if [[ -z "$RESOURCE_GROUP" ]]; then
        RESOURCE_GROUP="$(az cosmosdb list --query "[?name=='$ACCOUNT'].resourceGroup | [0]" -o tsv)"
        [[ -n "$RESOURCE_GROUP" ]] || die "Cosmos DB account '$ACCOUNT' not found in the current subscription"
    fi

    ENDPOINT="$(az cosmosdb show -n "$ACCOUNT" -g "$RESOURCE_GROUP" --query documentEndpoint -o tsv)"
    KEY="$(az cosmosdb keys list -n "$ACCOUNT" -g "$RESOURCE_GROUP" --type keys --query primaryMasterKey -o tsv)"

    if [[ -z "$PK_PATH" ]]; then
        PK_PATH="$(az cosmosdb sql container show -a "$ACCOUNT" -g "$RESOURCE_GROUP" -d "$DATABASE" -n "$CONTAINER" \
            --query 'resource.partitionKey.paths[0]' -o tsv)"
    fi
fi

PK_PATH="${PK_PATH:-/category}"
PK_FIELD="${PK_PATH#/}"
[[ -n "$PK_FIELD" && "$PK_FIELD" != */* ]] || die "unsupported partition key path '$PK_PATH' (only top-level paths are supported)"
ENDPOINT="${ENDPOINT%/}"

KEY_HEX="$(printf '%s' "$KEY" | base64 -d | od -An -v -tx1 | tr -d ' \n')"
RESOURCE_LINK="dbs/$DATABASE/colls/$CONTAINER"

auth_header() {
    local verb="$1" resource_type="$2" resource_link="$3" date="$4" sig
    sig="$(printf '%s\n%s\n%s\n%s\n\n' "$verb" "$resource_type" "$resource_link" "${date,,}" \
        | openssl dgst -sha256 -mac HMAC -macopt "hexkey:$KEY_HEX" -binary | base64 | tr -d '\n')"
    jq -rn --arg s "type=master&ver=1.0&sig=$sig" '$s | @uri'
}

echo "Seeding $ENDPOINT/$RESOURCE_LINK (partition key $PK_PATH) from $SEED_FILE"

RESPONSE_FILE="$(mktemp)"
trap 'rm -f "$RESPONSE_FILE"' EXIT

failures=0
count="$(jq length "$SEED_FILE")"
for ((i = 0; i < count; i++)); do
    doc="$(jq -c ".[$i]" "$SEED_FILE")"
    id="$(jq -r '.id // empty' <<<"$doc")"
    pk_header="$(jq -c --arg f "$PK_FIELD" 'if has($f) then [.[$f]] else empty end' <<<"$doc")"

    if [[ -z "$id" || -z "$pk_header" ]]; then
        echo "  [skip] record $i is missing 'id' or '$PK_FIELD'" >&2
        failures=$((failures + 1))
        continue
    fi

    date="$(LC_ALL=C TZ=GMT date '+%a, %d %b %Y %H:%M:%S GMT')"
    status="$(curl -sS -o "$RESPONSE_FILE" -w '%{http_code}' -X POST "$ENDPOINT/$RESOURCE_LINK/docs" \
        -H "Authorization: $(auth_header post docs "$RESOURCE_LINK" "$date")" \
        -H "x-ms-date: $date" \
        -H "x-ms-version: $API_VERSION" \
        -H "x-ms-documentdb-is-upsert: True" \
        -H "x-ms-documentdb-partitionkey: $pk_header" \
        -H "Content-Type: application/json" \
        --data-binary "$doc")" || status="000"

    case "$status" in
        201) echo "  [created]  $id ($pk_header)" ;;
        200) echo "  [replaced] $id ($pk_header)" ;;
        *)
            echo "  [failed]   $id: HTTP $status $(cat "$RESPONSE_FILE" 2>/dev/null)" >&2
            failures=$((failures + 1))
            ;;
    esac
done

if ((failures > 0)); then
    die "$failures of $count record(s) failed to upsert"
fi
echo "Upserted $count record(s)."
