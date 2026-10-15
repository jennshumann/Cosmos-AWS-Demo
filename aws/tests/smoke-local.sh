#!/usr/bin/env bash
#
# End-to-end local smoke test: DynamoDB Local + seed-dynamodb.sh + the API running on Kestrel.
# Verifies GET /products returns the 5 seed products, plus a point read, a search and POST /orders.
# (The stream -> inventory path is covered by the xUnit tests, which drive the stream handler.)

set -euo pipefail

AWS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${API_PORT:-5080}"
BASE="http://localhost:$PORT"

export DYNAMODB_ENDPOINT="${DYNAMODB_ENDPOINT:-http://localhost:8000}"
export PRODUCTS_TABLE_NAME="${PRODUCTS_TABLE_NAME:-Products}"
export ORDERS_TABLE_NAME="${ORDERS_TABLE_NAME:-Orders}"
export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-local}"
export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-local}"
export AWS_REGION="${AWS_REGION:-us-east-1}"
export AWS_DEFAULT_REGION="$AWS_REGION"

fail() { echo "FAIL: $*" >&2; exit 1; }

"$AWS_DIR/scripts/local-dynamodb.sh"
"$AWS_DIR/migration/seed-dynamodb.sh" -t "$PRODUCTS_TABLE_NAME" -e "$DYNAMODB_ENDPOINT"

echo "Starting API on $BASE..."
dotnet run --project "$AWS_DIR/src/CosmosAwsDemo.Lambda" -- --urls "$BASE" > /tmp/cosmos-aws-demo-api.log 2>&1 &
API_PID=$!
trap 'kill $API_PID 2>/dev/null || true' EXIT
for _ in $(seq 1 60); do curl -sf "$BASE/products" >/dev/null 2>&1 && break; sleep 1; done

products="$(curl -sf "$BASE/products")" || fail "GET /products (see /tmp/cosmos-aws-demo-api.log)"
echo "GET /products -> $products"
[[ "$(jq 'length' <<<"$products")" == 5 ]] || fail "expected 5 products"
[[ "$(jq -r '[.[].id] | sort | join(",")' <<<"$products")" == "COG-001,COG-002,COG-003,COG-004,COG-005" ]] \
    || fail "unexpected product ids"

hoodie="$(curl -sf "$BASE/products/Apparel/COG-001")" || fail "GET /products/Apparel/COG-001"
echo "GET /products/Apparel/COG-001 -> $hoodie"
[[ "$(jq -r '.description' <<<"$hoodie")" == "Cognition hoodie" ]] || fail "wrong product"

status="$(curl -s -o /dev/null -w '%{http_code}' "$BASE/products/Drinkware/COG-001")"
[[ "$status" == 404 ]] || fail "expected 404 for wrong category, got $status"

search="$(curl -sf "$BASE/search/SOCKS")" || fail "GET /search/SOCKS"
echo "GET /search/SOCKS -> $search"
[[ "$(jq -r '[.[].id] | join(",")' <<<"$search")" == "COG-005" ]] || fail "search did not match case-insensitively"

status="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/orders" -H 'content-type: application/json' \
    -d '{"id":"smoke-order-1","customerNumber":"CUST-42","lineItems":[{"productId":"COG-001","category":"Apparel","quantity":2}]}')"
[[ "$status" == 204 ]] || fail "POST /orders returned $status"
aws dynamodb get-item --endpoint-url "$DYNAMODB_ENDPOINT" --table-name "$ORDERS_TABLE_NAME" \
    --key '{"id":{"S":"smoke-order-1"}}' --output json | jq -e '.Item.customerNumber.S == "CUST-42"' >/dev/null \
    || fail "order not stored"
echo "POST /orders -> 204, order stored"

echo "PASS: local smoke test"
