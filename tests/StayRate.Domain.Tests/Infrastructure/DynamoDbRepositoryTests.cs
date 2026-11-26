using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Moq;
using StayRate.Application.Abstractions;
using StayRate.Domain;
using StayRate.Domain.Bookings;
using StayRate.Infrastructure.Idempotency.DynamoDb;
using StayRate.Infrastructure.Persistence.DynamoDb;

namespace StayRate.Domain.Tests.Infrastructure;

public sealed class DynamoDbRepositoryTests
{
    [Fact]
    public async Task DynamoDbIdempotencyStore_GetAsync_WhenItemMissing_ReturnsNull()
    {
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo
            .Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { Item = null });

        var store = new DynamoDbIdempotencyStore(dynamo.Object, new DynamoDbTableNames("bookings", "idempotency"));

        var result = await store.GetAsync("k1", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DynamoDbIdempotencyStore_TryAddAsync_WritesTtlAttribute()
    {
        PutItemRequest? capturedRequest = null;
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo
            .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutItemRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new PutItemResponse());

        var store = new DynamoDbIdempotencyStore(dynamo.Object, new DynamoDbTableNames("bookings", "idempotency"));
        var createdAt = new DateTimeOffset(2026, 2, 23, 18, 0, 0, TimeSpan.Zero);

        await store.TryAddAsync(new IdempotencyRecord("k2", "h2", Guid.NewGuid(), createdAt), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Item.ContainsKey("ExpiresAtEpoch"));
        Assert.True(long.TryParse(capturedRequest.Item["ExpiresAtEpoch"].N, out var ttl));
        Assert.True(ttl > createdAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task DynamoDbBookingRepository_GetByIdAsync_WhenItemMissing_ReturnsNull()
    {
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo
            .Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { Item = null });

        var repository = new DynamoDbBookingRepository(dynamo.Object, new DynamoDbTableNames("bookings", "idempotency"));

        var result = await repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DynamoDbBookingRepository_AddAsync_UsesConditionalWrite()
    {
        PutItemRequest? capturedRequest = null;
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo
            .Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutItemRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new PutItemResponse());

        var repository = new DynamoDbBookingRepository(dynamo.Object, new DynamoDbTableNames("bookings", "idempotency"));
        var booking = Booking.CreateFromQuoteSnapshot(CreateSnapshot(), "hash-1", DateTimeOffset.UtcNow);

        await repository.AddAsync(booking, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("attribute_not_exists(BookingId)", capturedRequest!.ConditionExpression);
    }

    [Fact]
    public async Task DynamoDbIdempotencyStore_AddBooking_UsesOneTransaction()
    {
        TransactWriteItemsRequest? captured = null;
        var dynamo = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        dynamo.Setup(client => client.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new TransactWriteItemsResponse());
        var store = new DynamoDbIdempotencyStore(dynamo.Object, new DynamoDbTableNames("bookings", "idempotency"));
        var booking = Booking.CreateFromQuoteSnapshot(CreateSnapshot(), "quote-hash", DateTimeOffset.UtcNow);
        booking.Confirm(booking.CreatedAt);
        var record = new IdempotencyRecord("v2:scoped", "request-hash", booking.Id, booking.CreatedAt);

        Assert.True(await store.TryAddBookingAsync(record, booking, "legacy", CancellationToken.None));
        Assert.NotNull(captured);
        Assert.Collection(captured.TransactItems,
            item =>
            {
                Assert.Equal("bookings", item.Put.TableName);
                Assert.Equal("attribute_not_exists(BookingId)", item.Put.ConditionExpression);
            },
            item =>
            {
                Assert.Equal("idempotency", item.Put.TableName);
                Assert.Equal(booking.Id.ToString("D"), item.Put.Item["ResultBookingId"].S);
                Assert.Equal("attribute_not_exists(IdempotencyKey)", item.Put.ConditionExpression);
            },
            item =>
            {
                Assert.Equal("idempotency", item.ConditionCheck.TableName);
                Assert.Equal("legacy", item.ConditionCheck.Key["IdempotencyKey"].S);
                Assert.Equal("attribute_not_exists(IdempotencyKey)", item.ConditionCheck.ConditionExpression);
            });
        dynamo.VerifyAll();
    }

    [Fact]
    public void HistoricQuoteSnapshot_RetainsStoredMoney()
    {
        var snapshot = CreateSnapshot();
        var oldFormat = JsonSerializer.Serialize(snapshot);

        var restored = JsonSerializer.Deserialize<QuoteSnapshot>(oldFormat);

        Assert.NotNull(restored);
        Assert.Equal(snapshot.Total, restored.Total);
        Assert.Equal(snapshot.LineItems, restored.LineItems);
    }

    private static QuoteSnapshot CreateSnapshot() =>
        new(
            "prop-1",
            new DateOnly(2026, 3, 10),
            new DateOnly(2026, 3, 12),
            2,
            [new QuoteSnapshotLineItem("nightly", "Nightly rate", new Money(100m, "GBP"))],
            new Money(200m, "GBP"));
}
