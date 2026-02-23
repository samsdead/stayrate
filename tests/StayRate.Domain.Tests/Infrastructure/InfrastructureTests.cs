using StayRate.Application.Abstractions;
using StayRate.Domain;
using StayRate.Domain.Bookings;
using StayRate.Infrastructure.Hashing;
using StayRate.Infrastructure.Idempotency;
using StayRate.Infrastructure.Persistence;
using StayRate.Infrastructure.Time;

namespace StayRate.Domain.Tests.Infrastructure;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task InMemoryBookingRepository_AddAndGet_ReturnsStoredBooking()
    {
        var repository = new InMemoryBookingRepository();
        var booking = Booking.CreateFromQuoteSnapshot(CreateSnapshot(), "hash-1", DateTimeOffset.UtcNow);

        await repository.AddAsync(booking, CancellationToken.None);
        var loaded = await repository.GetByIdAsync(booking.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(booking.Id, loaded!.Id);
    }

    [Fact]
    public async Task InMemoryBookingRepository_AddDuplicate_ThrowsInvalidOperationException()
    {
        var repository = new InMemoryBookingRepository();
        var booking = Booking.CreateFromQuoteSnapshot(CreateSnapshot(), "hash-2", DateTimeOffset.UtcNow);

        await repository.AddAsync(booking, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(booking, CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryIdempotencyStore_AddAndGet_ReturnsStoredRecord()
    {
        var store = new InMemoryIdempotencyStore();
        var record = new IdempotencyRecord("k1", "h1", Guid.NewGuid(), DateTimeOffset.UtcNow);

        await store.TryAddAsync(record, CancellationToken.None);
        var loaded = await store.GetAsync("k1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("h1", loaded!.RequestHash);
    }

    [Fact]
    public async Task InMemoryIdempotencyStore_AddDuplicate_ThrowsInvalidOperationException()
    {
        var store = new InMemoryIdempotencyStore();
        var record = new IdempotencyRecord("k2", "h2", Guid.NewGuid(), DateTimeOffset.UtcNow);

        await store.TryAddAsync(record, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryAddAsync(record, CancellationToken.None));
    }

    [Fact]
    public void CanonicalJsonRequestHasher_SameInput_ProducesSameHash()
    {
        var hasher = new CanonicalJsonRequestHasher();
        var snapshot = CreateSnapshot();

        var first = hasher.Hash(snapshot);
        var second = hasher.Hash(snapshot);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void CanonicalJsonRequestHasher_DifferentInput_ProducesDifferentHash()
    {
        var hasher = new CanonicalJsonRequestHasher();

        var first = hasher.Hash(CreateSnapshot());
        var second = hasher.Hash(CreateSnapshot(total: 250m));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SystemClock_UtcNow_IsNearCurrentUtcTime()
    {
        var clock = new SystemClock();
        var before = DateTimeOffset.UtcNow;
        var now = clock.UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(now, before.AddSeconds(-1), after.AddSeconds(1));
    }

    private static QuoteSnapshot CreateSnapshot(decimal total = 200m) =>
        new(
            "prop-1",
            new DateOnly(2026, 3, 10),
            new DateOnly(2026, 3, 12),
            2,
            new List<QuoteSnapshotLineItem>
            {
                new("nightly", "Nightly rate", new Money(100m, "GBP"))
            },
            new Money(total, "GBP"));
}
