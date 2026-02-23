using System.Collections.Concurrent;
using StayRate.Application.Abstractions;
using StayRate.Domain.Bookings;

namespace StayRate.Infrastructure.Persistence;

public sealed class InMemoryBookingRepository : IBookingRepository
{
    private readonly ConcurrentDictionary<Guid, Booking> _bookings = new();

    public Task<Booking?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _bookings.TryGetValue(id, out var booking);
        return Task.FromResult<Booking?>(booking);
    }

    public Task AddAsync(Booking booking, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_bookings.TryAdd(booking.Id, booking))
            throw new InvalidOperationException($"Booking with id '{booking.Id}' already exists.");

        return Task.CompletedTask;
    }
}
