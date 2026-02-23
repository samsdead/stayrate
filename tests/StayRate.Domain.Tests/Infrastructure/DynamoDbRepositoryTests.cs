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

    private static QuoteSnapshot CreateSnapshot() =>
        new(
            "prop-1",
            new DateOnly(2026, 3, 10),
            new DateOnly(2026, 3, 12),
            2,
            [new QuoteSnapshotLineItem("nightly", "Nightly rate", new Money(100m, "GBP"))],
            new Money(200m, "GBP"));
}
