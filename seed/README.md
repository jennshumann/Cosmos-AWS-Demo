# Products seed data

[`products.json`](products.json) holds sample catalog records for the Products container. Each record matches [`Product.cs`](../CosmosFunctionsDemo/Models/Product.cs) (`id`, `description`, `category`, `unitPrice`, `availableInventory`).

| id      | description            | category    | unitPrice | availableInventory |
|---------|------------------------|-------------|-----------|--------------------|
| COG-001 | Cognition hoodie       | Apparel     | 65.00     | 120                |
| COG-002 | Cognition t-shirt      | Apparel     | 28.00     | 340                |
| COG-003 | Cognition water bottle | Drinkware   | 32.00     | 85                 |
| COG-004 | Cognition sticker pack | Accessories | 6.00      | 1500               |
| COG-005 | Cognition crew socks   | Apparel     | 14.00     | 210                |

## Running the seed script

Run [`seed.sh`](seed.sh) after the infrastructure has been deployed (see [Deploying](../README.md#deploying)). It needs `az` (logged in with `az login` and pointed at the right subscription), `curl`, `jq`, and `openssl`.

```bash
./seed/seed.sh -a cmh-cosmos-demo-acct -d cmh-cosmos-demo-db -c products
```

Or use environment variables:

```bash
export COSMOS_ACCOUNT_NAME=cmh-cosmos-demo-acct
export COSMOS_DATABASE_NAME=cmh-cosmos-demo-db
export COSMOS_CONTAINER_NAME=products
export COSMOS_RESOURCE_GROUP=COSMOS-DEMO   # optional, looked up from the account if not set
./seed/seed.sh
```

The values above are the defaults from [`pipeline.yaml`](../pipelines/pipeline.yaml). Change them if you deployed with different names.

The script uses the Azure CLI to get the account endpoint, the primary key, and the container's partition key path (`az cosmosdb sql container show`). It then upserts each record through the Cosmos DB REST API, because the Azure CLI has no command for writing documents. Upserting means you can run the script again at any time. Each run resets the inventory numbers to the values in `products.json`.

To run against the Cosmos DB emulator or skip the CLI lookups, set `COSMOS_ENDPOINT` and `COSMOS_KEY` (and `COSMOS_PARTITION_KEY_PATH` if it isn't `/category`).

## Order line item categories must match

The Products container is partitioned on `/category` (the `cosmosContainerPartitionKey` parameter in [`cosmosfunctionsdemo.bicep`](../infrastructure/cosmosfunctionsdemo.bicep)). When the change feed processes an order, [`InventoryUpdateService.cs`](../CosmosFunctionsDemo/Services/InventoryUpdateService.cs) reads each product by `lineItem.ProductId` and uses `lineItem.Category` as the partition key. This means the `category` on every order line item must exactly match the product's category: `Apparel`, `Drinkware`, or `Accessories` (case-sensitive). If the category is wrong, the read returns 404 and the inventory isn't updated.

Example order:

```json
{
  "id": "order-1001",
  "customerNumber": "CUST-42",
  "lineItems": [
    { "productId": "COG-001", "category": "Apparel", "quantity": 2 },
    { "productId": "COG-003", "category": "Drinkware", "quantity": 1 }
  ]
}
```
