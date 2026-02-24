
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
}