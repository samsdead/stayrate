using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using StayRate.Application.Abstractions;
using StayRate.Domain.Bookings;
using StayRate.Infrastructure.Persistence.DynamoDb;

namespace StayRate.Infrastructure.Idempotency.DynamoDb;

public sealed class DynamoDbIdempotencyStore(IAmazonDynamoDB dynamoDb, DynamoDbTableNames tableNames) : IIdempotencyStore
{
    private const int TtlDays = 7;

    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
    {
        var response = await dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = tableNames.IdempotencyTableName,
            Key = new() { ["IdempotencyKey"] = new() { S = key } },
            ConsistentRead = true
        }, ct);

        if (response.Item is null || response.Item.Count == 0)
            return null;

        var item = response.Item;
        return new IdempotencyRecord(
            item["IdempotencyKey"].S,
            item["RequestHash"].S,
            Guid.Parse(item["ResultBookingId"].S),
            DateTimeOffset.Parse(item["CreatedAt"].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task TryAddAsync(IdempotencyRecord record, CancellationToken ct)
    {
        try
        {
            await dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = tableNames.IdempotencyTableName,
                Item = SerializeRecord(record),
                ConditionExpression = "attribute_not_exists(IdempotencyKey)"
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new InvalidOperationException("The idempotency record already exists.");
        }
    }

    public async Task<bool> TryAddBookingAsync(
        IdempotencyRecord record, Booking booking, string legacyKey, CancellationToken ct)
    {
        var transaction = new TransactWriteItemsRequest
        {
            ClientRequestToken = booking.Id.ToString("D"),
            TransactItems =
            [
                new() { Put = new()
                {
                    TableName = tableNames.BookingsTableName,
                    Item = DynamoDbBookingRepository.SerializeBooking(booking),
                    ConditionExpression = "attribute_not_exists(BookingId)"
                } },
                new() { Put = new()
                {
                    TableName = tableNames.IdempotencyTableName,
                    Item = SerializeRecord(record),
                    ConditionExpression = "attribute_not_exists(IdempotencyKey)"
                } },
                new() { ConditionCheck = new()
                {
                    TableName = tableNames.IdempotencyTableName,
                    Key = new() { ["IdempotencyKey"] = new() { S = legacyKey } },
                    ConditionExpression = "attribute_not_exists(IdempotencyKey)"
                } }
            ]
        };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await dynamoDb.TransactWriteItemsAsync(transaction, ct);
                return true;
            }
            catch (TransactionCanceledException ex) when (
                ex.CancellationReasons?.Skip(1).Any(reason => reason.Code == "ConditionalCheckFailed") == true)
            {
                return false;
            }
            catch (TransactionCanceledException ex) when (
                attempt < 3 &&
                ex.CancellationReasons?.Any(reason => reason.Code == "TransactionConflict") == true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 25) * (1 << attempt)), ct);
            }
        }
    }

    private static Dictionary<string, AttributeValue> SerializeRecord(IdempotencyRecord record) => new()
    {
        ["IdempotencyKey"] = new() { S = record.Key },
        ["RequestHash"] = new() { S = record.RequestHash },
        ["ResultBookingId"] = new() { S = record.ResultBookingId.ToString("D") },
        ["CreatedAt"] = new() { S = record.CreatedAt.ToString("O") },
        ["ExpiresAtEpoch"] = new() { N = record.CreatedAt.AddDays(TtlDays).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) }
    };
}
