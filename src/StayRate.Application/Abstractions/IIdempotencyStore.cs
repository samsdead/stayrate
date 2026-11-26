
using StayRate.Domain.Bookings;

namespace StayRate.Application.Abstractions;

public sealed record IdempotencyRecord(
    string Key,
    string RequestHash,
    Guid ResultBookingId,
    DateTimeOffset CreatedAt
);

public interface IIdempotencyStore
{
    Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct);
    Task TryAddAsync(IdempotencyRecord record, CancellationToken ct);
    Task<bool> TryAddBookingAsync(IdempotencyRecord record, Booking booking, string legacyKey, CancellationToken ct);
}
