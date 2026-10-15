#!/usr/bin/env bash
#
# Starts DynamoDB Local in Docker (if nothing is listening on the endpoint yet) and creates the Products and
# Orders tables with the same keys and stream settings as the CDK stack. Safe to re-run.

set -euo pipefail

ENDPOINT="${DYNAMODB_ENDPOINT:-http://localhost:8000}"
PRODUCTS_TABLE="${PRODUCTS_TABLE_NAME:-Products}"
ORDERS_TABLE="${ORDERS_TABLE_NAME:-Orders}"
CONTAINER_NAME="${DYNAMODB_LOCAL_CONTAINER:-cosmos-aws-demo-dynamodb}"
PORT="${ENDPOINT##*:}"

export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-local}"
export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-local}"
export AWS_DEFAULT_REGION="${AWS_REGION:-${AWS_DEFAULT_REGION:-us-east-1}}"
ddb() { aws dynamodb --endpoint-url "$ENDPOINT" "$@"; }

if ! ddb list-tables >/dev/null 2>&1; then
    echo "Starting DynamoDB Local container $CONTAINER_NAME on port $PORT..."
    docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
    docker run -d --name "$CONTAINER_NAME" -p "$PORT:8000" amazon/dynamodb-local:latest \
        -jar DynamoDBLocal.jar -inMemory -sharedDb >/dev/null
    for _ in $(seq 1 30); do ddb list-tables >/dev/null 2>&1 && break; sleep 1; done
    ddb list-tables >/dev/null || { echo "DynamoDB Local did not start" >&2; exit 1; }
fi

table_exists() { ddb describe-table --table-name "$1" >/dev/null 2>&1; }

if table_exists "$PRODUCTS_TABLE"; then
    echo "Table $PRODUCTS_TABLE already exists"
else
    ddb create-table --table-name "$PRODUCTS_TABLE" --billing-mode PAY_PER_REQUEST \
        --attribute-definitions AttributeName=category,AttributeType=S AttributeName=id,AttributeType=S \
        --key-schema AttributeName=category,KeyType=HASH AttributeName=id,KeyType=RANGE >/dev/null
    echo "Created $PRODUCTS_TABLE (PK category, SK id)"
fi

if table_exists "$ORDERS_TABLE"; then
    echo "Table $ORDERS_TABLE already exists"
else
    ddb create-table --table-name "$ORDERS_TABLE" --billing-mode PAY_PER_REQUEST \
        --attribute-definitions AttributeName=id,AttributeType=S \
        --key-schema AttributeName=id,KeyType=HASH \
        --stream-specification StreamEnabled=true,StreamViewType=NEW_IMAGE >/dev/null
    echo "Created $ORDERS_TABLE (PK id, stream NEW_IMAGE)"
fi
