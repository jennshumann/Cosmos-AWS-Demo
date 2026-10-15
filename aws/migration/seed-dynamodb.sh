#!/usr/bin/env bash
#
# Loads seed/products.json into the DynamoDB Products table with `aws dynamodb batch-write-item`.
# Each JSON record is converted to DynamoDB attribute-value format (S/N/BOOL/NULL/L/M), and a lower-cased
# copy of `description` is written as `descriptionLower` so GET /search/{description} can match
# case-insensitively, like Cosmos SQL CONTAINS(c.description, @d, true).
# Puts overwrite existing items, so re-running the script resets inventory to the seed values.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BATCH_SIZE=25   # batch-write-item limit
MAX_RETRIES=8

usage() {
    cat <<USAGE
Usage: $(basename "$0") [-t table] [-s stack] [-f file] [-e endpoint-url] [-r region]

Options (each falls back to the environment variable shown):
  -t  Products table name     PRODUCTS_TABLE_NAME
  -s  CloudFormation stack    STACK_NAME   (looks up the ProductsTableName output; default CosmosAwsDemoStack)
  -f  Seed file               SEED_FILE    (default: <repo>/seed/products.json)
  -e  Endpoint URL            DYNAMODB_ENDPOINT   (e.g. http://localhost:8000 for DynamoDB Local)
  -r  Region                  AWS_REGION / AWS_DEFAULT_REGION
  -h  Show this help
USAGE
}

die() { echo "error: $*" >&2; exit 1; }

TABLE="${PRODUCTS_TABLE_NAME:-}"
STACK="${STACK_NAME:-CosmosAwsDemoStack}"
SEED_FILE="${SEED_FILE:-$SCRIPT_DIR/../../seed/products.json}"
ENDPOINT="${DYNAMODB_ENDPOINT:-}"
REGION="${AWS_REGION:-${AWS_DEFAULT_REGION:-}}"

while getopts ":t:s:f:e:r:h" opt; do
    case "$opt" in
        t) TABLE="$OPTARG" ;;
        s) STACK="$OPTARG" ;;
        f) SEED_FILE="$OPTARG" ;;
        e) ENDPOINT="$OPTARG" ;;
        r) REGION="$OPTARG" ;;
        h) usage; exit 0 ;;
        :) die "option -$OPTARG needs a value" ;;
        *) usage >&2; exit 2 ;;
    esac
done

command -v aws >/dev/null || die "aws CLI not found"
command -v jq >/dev/null || die "jq not found"
[[ -f "$SEED_FILE" ]] || die "seed file not found: $SEED_FILE"

AWS_ARGS=()
[[ -n "$REGION" ]] && AWS_ARGS+=(--region "$REGION")

if [[ -z "$TABLE" ]]; then
    [[ -z "$ENDPOINT" ]] || die "set -t/PRODUCTS_TABLE_NAME when using a local endpoint"
    echo "Looking up ProductsTableName output of stack $STACK..."
    TABLE="$(aws "${AWS_ARGS[@]}" cloudformation describe-stacks --stack-name "$STACK" \
        --query "Stacks[0].Outputs[?OutputKey=='ProductsTableName'].OutputValue" --output text)"
    [[ -n "$TABLE" && "$TABLE" != "None" ]] || die "stack $STACK has no ProductsTableName output"
fi

[[ -n "$ENDPOINT" ]] && AWS_ARGS+=(--endpoint-url "$ENDPOINT")

# JSON value -> DynamoDB AttributeValue
JQ_PROGRAM='
def av:
  if type == "string" then {S: .}
  elif type == "number" then {N: tostring}
  elif type == "boolean" then {BOOL: .}
  elif type == "null" then {NULL: true}
  elif type == "array" then {L: map(av)}
  else {M: with_entries(.value |= av)}
  end;

if type != "array" then error("seed file must contain a JSON array") else . end
| map(
    if (.category | type) != "string" or (.id | type) != "string"
    then error("every record needs string \"category\" and \"id\" (the table key): \(tojson)")
    else . end
    | if (.description | type) == "string" then . + {descriptionLower: (.description | ascii_downcase)} else . end
    | {PutRequest: {Item: with_entries(.value |= av)}}
  )
| [range(0; length; $size) as $i | .[$i:$i + $size]]
| .[]
| {($table): .}
'

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

total="$(jq 'length' "$SEED_FILE")"
echo "Seeding $total record(s) from $SEED_FILE into $TABLE${ENDPOINT:+ at $ENDPOINT}"

batch=0
while IFS= read -r request; do
    batch=$((batch + 1))
    request_file="$TMP_DIR/batch-$batch.json"
    printf '%s' "$request" > "$request_file"

    for attempt in $(seq 0 "$MAX_RETRIES"); do
        unprocessed="$(aws "${AWS_ARGS[@]}" dynamodb batch-write-item \
            --request-items "file://$request_file" --output json | jq -c '.UnprocessedItems // {}')"
        [[ "$unprocessed" == "{}" ]] && break
        (( attempt == MAX_RETRIES )) && die "batch $batch still has unprocessed items after $MAX_RETRIES retries"
        sleep "$(awk "BEGIN { print 0.1 * 2 ^ $attempt }")"
        printf '%s' "$unprocessed" > "$request_file"
    done
done < <(jq -c --arg table "$TABLE" --argjson size "$BATCH_SIZE" "$JQ_PROGRAM" "$SEED_FILE")

echo "Done: wrote $total item(s) in $batch batch(es)."
