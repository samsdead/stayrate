using StayRate.Application.Abstractions;
using StayRate.Application.Bookings.ConfirmBooking;
using StayRate.Domain;
using StayRate.Domain.Bookings;
using StayRate.Infrastructure.Idempotency;
using StayRate.Infrastructure.Persistence;

namespace StayRate.Domain.Tests.Bookings;

public sealed class ConfirmBookingHandlerTests
{
    [Fact]
    public async Task HandleAsync_WithoutIdempotencyKey_ThrowsArgumentException()
    {
        var handler = CreateHandler();
        var command = new ConfirmBookingCommand("", CreateSnapshot("prop-1"));

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_NewKey_CreatesConfirmedBookingAndStoresIdempotency()
    {
        var repository = new InMemoryBookingRepository();
        var store = new InMemoryIdempotencyStore();
        var handler = CreateHandler(repository, store);

        var result = await handler.HandleAsync(
            new ConfirmBookingCommand("idem-1", CreateSnapshot("prop-1")),
            CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.NotNull(result.ConfirmedAt);

        var savedBooking = await repository.GetByIdAsync(result.BookingId, CancellationToken.None);
        Assert.NotNull(savedBooking);

        var record = await store.GetAsync("idem-1", CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(result.BookingId, record!.ResultBookingId);
    }

    [Fact]
    public async Task HandleAsync_SameKeySamePayload_ReturnsExistingBooking()
    {
        var repository = new InMemoryBookingRepository();
        var store = new InMemoryIdempotencyStore();
        var handler = CreateHandler(repository, store);

        var first = await handler.HandleAsync(
            new ConfirmBookingCommand("idem-2", CreateSnapshot("prop-2")),
            CancellationToken.None);

        var second = await handler.HandleAsync(
            new ConfirmBookingCommand("idem-2", CreateSnapshot("prop-2")),
            CancellationToken.None);

        Assert.Equal(first.BookingId, second.BookingId);
        Assert.Equal(first.QuoteSnapshotHash, second.QuoteSnapshotHash);
    }

    [Fact]
    public async Task HandleAsync_SameKeyDifferentPayload_ThrowsConflict()
    {
        var repository = new InMemoryBookingRepository();
        var store = new InMemoryIdempotencyStore();
        var handler = CreateHandler(repository, store);

        await handler.HandleAsync(
            new ConfirmBookingCommand("idem-3", CreateSnapshot("prop-3")),
            CancellationToken.None);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            handler.HandleAsync(
                new ConfirmBookingCommand("idem-3", CreateSnapshot("prop-different")),
                CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenIdempotencyRecordPointsToMissingBooking_ThrowsInvalidOperationException()
    {
        var repository = new InMemoryBookingRepository();
        var store = new MissingBookingStore();
        var handler = CreateHandler(repository, store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(
                new ConfirmBookingCommand("idem-4", CreateSnapshot("prop-4")),
                CancellationToken.None));
    }

    private static ConfirmBookingHandler CreateHandler(
        IBookingRepository? repository = null,
        IIdempotencyStore? store = null)
    {
        return new ConfirmBookingHandler(
            repository ?? new InMemoryBookingRepository(),
            store ?? new InMemoryIdempotencyStore(),
            new TestRequestHasher(),
            new FrozenClock(new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero)));
    }

    private static QuoteSnapshot CreateSnapshot(string propertyId) =>
        new(
            propertyId,
            new DateOnly(2026, 3, 10),
            new DateOnly(2026, 3, 12),
            2,
            new List<QuoteSnapshotLineItem>
            {
                new("nightly", "Nightly rate", new Money(100m, "GBP"))
            },
            new Money(200m, "GBP"));

    private sealed class FrozenClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class TestRequestHasher : IRequestHasher
    {
        public string Hash(object value)
        {
            var snapshot = (QuoteSnapshot)value;
            return $"{snapshot.PropertyId}-{snapshot.CheckIn:yyyyMMdd}-{snapshot.CheckOut:yyyyMMdd}-{snapshot.Guests}-{snapshot.Total.Amount}";
        }
    }

    private sealed class MissingBookingStore : IIdempotencyStore
    {
        public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
        {
            return Task.FromResult<IdempotencyRecord?>(
                new IdempotencyRecord(key, "prop-4-20260310-20260312-2-200", Guid.NewGuid(), DateTimeOffset.UtcNow));
        }

        public Task TryAddAsync(IdempotencyRecord record, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}
