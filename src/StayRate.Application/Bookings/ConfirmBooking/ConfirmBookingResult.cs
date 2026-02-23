using StayRate.Domain.Bookings;

namespace StayRate.Application.Bookings.ConfirmBooking;

public sealed record ConfirmBookingResult(
    Guid BookingId,
    BookingStatus Status,
    string QuoteSnapshotHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt
);