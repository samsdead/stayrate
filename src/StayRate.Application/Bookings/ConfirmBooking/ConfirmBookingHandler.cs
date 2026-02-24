
using StayRate.Application.Abstractions;
using StayRate.Domain.Bookings;

namespace StayRate.Application.Bookings.ConfirmBooking;

public sealed class ConfirmBookingHandler
{
    private readonly IBookingRepository _bookings;
    private readonly IIdempotencyStore _idempotency;
    private readonly IRequestHasher _hasher;
    private readonly IClock _clock;

    public ConfirmBookingHandler(
        IBookingRepository bookings,
        IIdempotencyStore idempotency,
        IRequestHasher hasher,
        IClock clock)
    {
        _bookings = bookings;
        _idempotency = idempotency;
        _hasher = hasher;
        _clock = clock;
    }

    public async Task<ConfirmBookingResult> HandleAsync(ConfirmBookingCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required.", nameof(cmd.IdempotencyKey));

        if (cmd.Snapshot is null)
            throw new ArgumentNullException(nameof(cmd.Snapshot));

        var requestHash = _hasher.Hash(cmd.Snapshot);

        var existing = await _idempotency.GetAsync(cmd.IdempotencyKey, ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                throw new IdempotencyConflictException(cmd.IdempotencyKey);

            var booking = await _bookings.GetByIdAsync(existing.ResultBookingId, ct);
            if (booking is null)
                throw new InvalidOperationException("Idempotency record exists but booking was not found.");

            booking.Confirm(_clock.UtcNow);

            return new ConfirmBookingResult(
                booking.Id,
                booking.Status,
                booking.QuoteSnapshotHash,
                booking.CreatedAt,
                booking.ConfirmedAt
            );
        }

        var now = _clock.UtcNow;

        var newBooking = Booking.CreateFromQuoteSnapshot(cmd.Snapshot, requestHash, now);
        newBooking.Confirm(now);

        await _bookings.AddAsync(newBooking, ct);

        await _idempotency.TryAddAsync(
            new IdempotencyRecord(cmd.IdempotencyKey, requestHash, newBooking.Id, now),
            ct
        );

        return new ConfirmBookingResult(
            newBooking.Id,
            newBooking.Status,
            newBooking.QuoteSnapshotHash,
            newBooking.CreatedAt,
            newBooking.ConfirmedAt
        );
    }
}