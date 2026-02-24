
using StayRate.Domain.Bookings;

namespace StayRate.Application.Abstractions;

public interface IBookingRepository
{
    Task<Booking?> GetByIdAsync(Guid id, CancellationToken ct);
    Task AddAsync(Booking booking, CancellationToken ct);
}