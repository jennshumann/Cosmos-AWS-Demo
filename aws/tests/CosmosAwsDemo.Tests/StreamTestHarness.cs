using Amazon.DynamoDBStreams;
using Amazon.DynamoDBStreams.Model;
using Amazon.Lambda.DynamoDBEvents;
using StreamAttributeValue = Amazon.DynamoDBStreams.Model.AttributeValue;

namespace CosmosAwsDemo.Tests;

/// <summary>
/// Stands in for the Lambda event source mapping: reads records from the Orders table's DynamoDB stream
/// and converts them into the DynamoDBEvent shape Lambda delivers to OrdersStreamFunction.
/// </summary>
public static class StreamTestHarness
{
    public static async Task<List<DynamoDBEvent.DynamodbStreamRecord>> WaitForRecordsAsync(
        IAmazonDynamoDBStreams streams,
        string streamArn,
        Func<DynamoDBEvent.DynamodbStreamRecord, bool> predicate,
        int expectedCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        List<DynamoDBEvent.DynamodbStreamRecord> matches;
        do
        {
            matches = (await ReadAllAsync(streams, streamArn)).Where(predicate).ToList();
            if (matches.Count >= expectedCount)
            {
                return matches;
            }

            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Expected {expectedCount} matching stream records, found {matches.Count}.");
    }

    public static string? OrderId(DynamoDBEvent.DynamodbStreamRecord record) =>
        record.Dynamodb?.Keys is { } keys && keys.TryGetValue("id", out var id) ? id.S : null;

    public static DynamoDBEvent ToEvent(IEnumerable<DynamoDBEvent.DynamodbStreamRecord> records) =>
        new() { Records = records.ToList() };

    private static async Task<List<DynamoDBEvent.DynamodbStreamRecord>> ReadAllAsync(IAmazonDynamoDBStreams streams, string streamArn)
    {
        var records = new List<DynamoDBEvent.DynamodbStreamRecord>();
        var description = await streams.DescribeStreamAsync(new DescribeStreamRequest { StreamArn = streamArn });

        foreach (var shard in description.StreamDescription.Shards ?? new List<Shard>())
        {
            var iterator = (await streams.GetShardIteratorAsync(new GetShardIteratorRequest
            {
                StreamArn = streamArn,
                ShardId = shard.ShardId,
                ShardIteratorType = ShardIteratorType.TRIM_HORIZON,
            })).ShardIterator;

            while (iterator is not null)
            {
                var page = await streams.GetRecordsAsync(new GetRecordsRequest { ShardIterator = iterator });
                if (page.Records is not { Count: > 0 })
                {
                    break;
                }

                records.AddRange(page.Records.Select(r => ToLambdaRecord(r, streamArn)));
                iterator = page.NextShardIterator;
            }
        }

        return records;
    }

    private static DynamoDBEvent.DynamodbStreamRecord ToLambdaRecord(Amazon.DynamoDBStreams.Model.Record record, string streamArn) => new()
    {
        EventID = record.EventID,
        EventName = record.EventName.Value,
        EventSource = "aws:dynamodb",
        EventSourceArn = streamArn,
        AwsRegion = record.AwsRegion,
        Dynamodb = new DynamoDBEvent.StreamRecord
        {
            Keys = Convert(record.Dynamodb.Keys),
            NewImage = Convert(record.Dynamodb.NewImage),
            SequenceNumber = record.Dynamodb.SequenceNumber,
            StreamViewType = record.Dynamodb.StreamViewType?.Value,
        },
    };

    private static Dictionary<string, DynamoDBEvent.AttributeValue>? Convert(Dictionary<string, StreamAttributeValue>? image) =>
        image?.ToDictionary(kv => kv.Key, kv => Convert(kv.Value));

    private static DynamoDBEvent.AttributeValue Convert(StreamAttributeValue value) => new()
    {
        S = value.S,
        N = value.N,
        BOOL = value.BOOL,
        NULL = value.NULL,
        L = value.L?.Select(Convert).ToList(),
        M = Convert(value.M),
    };
}
