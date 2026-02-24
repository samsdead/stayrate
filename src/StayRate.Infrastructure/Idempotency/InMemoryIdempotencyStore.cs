using System.Collections.Concurrent;
using StayRate.Application.Abstractions;

namespace StayRate.Infrastructure.Idempotency;

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

    public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _records.TryGetValue(key, out var record);
        return Task.FromResult<IdempotencyRecord?>(record);
    }

    public Task TryAddAsync(IdempotencyRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_records.TryAdd(record.Key, record))
            throw new InvalidOperationException($"Idempotency key '{record.Key}' already exists.");

        return Task.CompletedTask;
    }
}
