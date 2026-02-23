using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using StayRate.Application.Abstractions;
using StayRate.Domain.Bookings;

namespace StayRate.Infrastructure.Persistence.DynamoDb;

public sealed class DynamoDbBookingRepository : IBookingRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DynamoDbBookingRepository(IAmazonDynamoDB dynamoDb, DynamoDbTableNames tableNames)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableNames.BookingsTableName;
    }

    public async Task<Booking?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["BookingId"] = new() { S = id.ToString("D") }
            },
            ConsistentRead = true
        }, ct);

        if (response.Item.Count == 0)
            return null;

        var item = response.Item;
        var snapshot = JsonSerializer.Deserialize<QuoteSnapshot>(item["QuoteSnapshot"].S, JsonOptions)
            ?? throw new InvalidOperationException("Stored booking snapshot could not be deserialized.");

        return Booking.Rehydrate(
            Guid.Parse(item["BookingId"].S),
            Enum.Parse<BookingStatus>(item["Status"].S, ignoreCase: false),
            snapshot,
            item["QuoteSnapshotHash"].S,
            DateTimeOffset.Parse(item["CreatedAt"].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ParseNullable(item, "ConfirmedAt"),
            ParseNullable(item, "CancelledAt"));
    }

    public async Task AddAsync(Booking booking, CancellationToken ct)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["BookingId"] = new() { S = booking.Id.ToString("D") },
            ["Status"] = new() { S = booking.Status.ToString() },
            ["QuoteSnapshot"] = new() { S = JsonSerializer.Serialize(booking.QuoteSnapshot, JsonOptions) },
            ["QuoteSnapshotHash"] = new() { S = booking.QuoteSnapshotHash },
            ["CreatedAt"] = new() { S = booking.CreatedAt.ToString("O") },
            ["ConfirmedAt"] = new() { S = booking.ConfirmedAt?.ToString("O") ?? string.Empty },
            ["CancelledAt"] = new() { S = booking.CancelledAt?.ToString("O") ?? string.Empty }
        };

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(BookingId)"
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new InvalidOperationException($"Booking with id '{booking.Id}' already exists.");
        }
    }

    private static DateTimeOffset? ParseNullable(IReadOnlyDictionary<string, AttributeValue> item, string attributeName)
    {
        if (!item.TryGetValue(attributeName, out var value) || string.IsNullOrWhiteSpace(value.S))
            return null;

        return DateTimeOffset.Parse(value.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
