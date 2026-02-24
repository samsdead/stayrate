using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using StayRate.Application.Abstractions;
using StayRate.Infrastructure.Persistence.DynamoDb;

namespace StayRate.Infrastructure.Idempotency.DynamoDb;

public sealed class DynamoDbIdempotencyStore : IIdempotencyStore
{
    private const int TtlDays = 7;
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DynamoDbIdempotencyStore(IAmazonDynamoDB dynamoDb, DynamoDbTableNames tableNames)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableNames.IdempotencyTableName;
    }

    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["IdempotencyKey"] = new() { S = key }
            },
            ConsistentRead = true
        }, ct);

        if (response.Item is null || response.Item.Count == 0)
            return null;

        var item = response.Item;
        return new IdempotencyRecord(
            item["IdempotencyKey"].S,
            item["RequestHash"].S,
            Guid.Parse(item["ResultBookingId"].S),
            DateTimeOffset.Parse(item["CreatedAt"].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        );
    }

    public async Task TryAddAsync(IdempotencyRecord record, CancellationToken ct)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["IdempotencyKey"] = new() { S = record.Key },
            ["RequestHash"] = new() { S = record.RequestHash },
            ["ResultBookingId"] = new() { S = record.ResultBookingId.ToString("D") },
            ["CreatedAt"] = new() { S = record.CreatedAt.ToString("O") },
            ["ExpiresAtEpoch"] = new() { N = record.CreatedAt.AddDays(TtlDays).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) }
        };

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(IdempotencyKey)"
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new InvalidOperationException($"Idempotency key '{record.Key}' already exists.");
        }
    }
}
