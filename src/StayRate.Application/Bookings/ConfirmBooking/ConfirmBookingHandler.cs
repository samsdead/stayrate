using StayRate.Application.Abstractions;
using StayRate.Domain.Bookings;
using StayRate.Domain.Common;

namespace StayRate.Application.Bookings.ConfirmBooking;

public sealed class ConfirmBookingHandler(
    IBookingRepository bookings,
    IIdempotencyStore idempotency,
    IRequestHasher hasher,
    IClock clock,
    IBookingQuoteCatalog catalog)
{
    public async Task<ConfirmBookingResult> HandleAsync(ConfirmBookingCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.IdempotencyKey) || string.IsNullOrWhiteSpace(cmd.Caller))
            throw new ArgumentException("A caller and idempotency key are required.");

        var snapshot = cmd.Snapshot;
        var requestHash = hasher.Hash(snapshot);
        var scopedKey = "v2:" + hasher.Hash(new { cmd.Caller, cmd.IdempotencyKey });
        var existing = await idempotency.GetAsync(scopedKey, ct);
        if (existing is not null)
            return await ReplayAsync(existing, requestHash, ct);

        if (await idempotency.GetAsync(cmd.IdempotencyKey, ct) is not null)
            throw new IdempotencyConflictException();

        var quote = catalog.FindQuote(snapshot.PropertyId, snapshot.CheckIn, snapshot.CheckOut, snapshot.Guests);
        if (quote is null || !quote.Total.Equals(snapshot.Total) || !quote.LineItems.SequenceEqual(snapshot.LineItems))
            throw new DomainRuleViolation("Booking details must match a current quote for this property.");

        var now = clock.UtcNow;
        var booking = Booking.CreateFromQuoteSnapshot(quote, hasher.Hash(quote), now);
        booking.Confirm(now);

        var record = new IdempotencyRecord(scopedKey, requestHash, booking.Id, now);
        if (await idempotency.TryAddBookingAsync(record, booking, cmd.IdempotencyKey, ct))
            return ToResult(booking);

        existing = await idempotency.GetAsync(scopedKey, ct);
        if (existing is null)
            throw new IdempotencyConflictException();

        return await ReplayAsync(existing, requestHash, ct);
    }

    private async Task<ConfirmBookingResult> ReplayAsync(IdempotencyRecord existing, string requestHash, CancellationToken ct)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            throw new IdempotencyConflictException();

        var booking = await bookings.GetByIdAsync(existing.ResultBookingId, ct)
            ?? throw new InvalidOperationException("Idempotency record exists without a booking.");
        return ToResult(booking);
    }

    private static ConfirmBookingResult ToResult(Booking booking) =>
        new(booking.Id, booking.Status, booking.QuoteSnapshotHash, booking.CreatedAt, booking.ConfirmedAt);
}
