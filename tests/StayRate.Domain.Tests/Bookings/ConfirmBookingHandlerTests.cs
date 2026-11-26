using StayRate.Infrastructure.Hashing;
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
        var command = new ConfirmBookingCommand("", CreateSnapshot("prop-1"), "caller-1");

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_NewKey_CreatesConfirmedBookingAndStoresIdempotency()
    {
        var repository = new InMemoryBookingRepository();
        var store = new InMemoryIdempotencyStore(repository);
        var handler = CreateHandler(repository, store);

        var result = await handler.HandleAsync(
            new ConfirmBookingCommand("idem-1", CreateSnapshot("prop-1"), "caller-1"),
            CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.NotNull(result.ConfirmedAt);

        var savedBooking = await repository.GetByIdAsync(result.BookingId, CancellationToken.None);
        Assert.NotNull(savedBooking);

        var key = "v2:" + new CanonicalJsonRequestHasher().Hash(new { Caller = "caller-1", IdempotencyKey = "idem-1" });
        var record = await store.GetAsync(key, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(result.BookingId, record!.ResultBookingId);
    }

    [Fact]
    public async Task HandleAsync_SameKeySamePayload_ReturnsExistingBooking()
    {
        var repository = new InMemoryBookingRepository();
        var store = new InMemoryIdempotencyStore(repository);
        var handler = CreateHandler(repository, store);

        var first = await handler.HandleAsync(
            new ConfirmBookingCommand("idem-2", CreateSnapshot("prop-2"), "caller-1"),
            CancellationToken.None);

        var second = await handler.HandleAsync(
            new ConfirmBookingCommand("idem-2", CreateSnapshot("prop-2"), "caller-1"),
            CancellationToken.None);

        Assert.Equal(first.BookingId, second.BookingId);
        Assert.Equal(first.QuoteSnapshotHash, second.QuoteSnapshotHash);
    }

    [Fact]
    public async Task HandleAsync_SameKeyDifferentPayload_ThrowsConflict()
    {
        var repository = new InMemoryBookingRepository();
        var store = new InMemoryIdempotencyStore(repository);
        var handler = CreateHandler(repository, store);

        await handler.HandleAsync(
            new ConfirmBookingCommand("idem-3", CreateSnapshot("prop-3"), "caller-1"),
            CancellationToken.None);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            handler.HandleAsync(
                new ConfirmBookingCommand("idem-3", CreateSnapshot("prop-different"), "caller-1"),
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
                new ConfirmBookingCommand("idem-4", CreateSnapshot("prop-4"), "caller-1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_ConcurrentReplaysReturnOneBooking()
    {
        var handler = CreateHandler();
        var command = new ConfirmBookingCommand("concurrent", CreateSnapshot("prop-1"), "caller-1");

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => handler.HandleAsync(command, CancellationToken.None))));

        Assert.Single(results.Select(result => result.BookingId).Distinct());
    }

    [Fact]
    public async Task HandleAsync_OwnerlessLegacyKeyPreventsAnotherBooking()
    {
        var repository = new InMemoryBookingRepository();
        var store = new InMemoryIdempotencyStore(repository);
        var snapshot = CreateSnapshot("prop-1");
        var existing = Booking.CreateFromQuoteSnapshot(snapshot, "legacy-hash", DateTimeOffset.UtcNow);
        await repository.AddAsync(existing, CancellationToken.None);
        await store.TryAddAsync(new IdempotencyRecord("legacy", "legacy-hash", existing.Id, existing.CreatedAt), CancellationToken.None);
        var handler = CreateHandler(repository, store);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            handler.HandleAsync(new ConfirmBookingCommand("legacy", snapshot, "caller-1"), CancellationToken.None));
        Assert.Same(existing, await repository.GetByIdAsync(existing.Id, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_SameKeyFromDifferentCallersIsIndependent()
    {
        var handler = CreateHandler();
        var snapshot = CreateSnapshot("prop-1");

        var first = await handler.HandleAsync(
            new ConfirmBookingCommand("shared", snapshot, "caller-1"), CancellationToken.None);
        var second = await handler.HandleAsync(
            new ConfirmBookingCommand("shared", snapshot, "caller-2"), CancellationToken.None);

        Assert.NotEqual(first.BookingId, second.BookingId);
    }

    private static ConfirmBookingHandler CreateHandler(
        InMemoryBookingRepository? repository = null,
        IIdempotencyStore? store = null)
    {
        repository ??= new InMemoryBookingRepository();
        return new ConfirmBookingHandler(
            repository,
            store ?? new InMemoryIdempotencyStore(repository),
            new CanonicalJsonRequestHasher(),
            new FrozenClock(new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero)),
            new TestBookingQuoteCatalog());
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

    private sealed class TestBookingQuoteCatalog : IBookingQuoteCatalog
    {
        public QuoteSnapshot FindQuote(string propertyId, DateOnly checkIn, DateOnly checkOut, int guests) =>
            CreateSnapshot(propertyId);
    }

    private sealed class MissingBookingStore : IIdempotencyStore
    {
        public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
        {
            return Task.FromResult<IdempotencyRecord?>(
                new IdempotencyRecord(key, new CanonicalJsonRequestHasher().Hash(CreateSnapshot("prop-4")), Guid.NewGuid(), DateTimeOffset.UtcNow));
        }

        public Task TryAddAsync(IdempotencyRecord record, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> TryAddBookingAsync(
            IdempotencyRecord record, Booking booking, string legacyKey, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
