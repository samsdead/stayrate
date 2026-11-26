using StayRate.Application.Abstractions;
using StayRate.Domain.Bookings;
using StayRate.Infrastructure.Persistence;

namespace StayRate.Infrastructure.Idempotency;

public sealed class InMemoryIdempotencyStore(InMemoryBookingRepository bookings) : IIdempotencyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

    public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_records.GetValueOrDefault(key));
    }

    public Task TryAddAsync(IdempotencyRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_records.TryAdd(record.Key, record))
                throw new InvalidOperationException("The idempotency record already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryAddBookingAsync(
        IdempotencyRecord record, Booking booking, string legacyKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_records.ContainsKey(record.Key) || _records.ContainsKey(legacyKey))
                return Task.FromResult(false);

            bookings.Add(booking);
            _records.Add(record.Key, record);
            return Task.FromResult(true);
        }
    }
}
