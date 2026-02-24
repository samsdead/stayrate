
using StayRate.Domain.Bookings;

namespace StayRate.Application.Bookings.ConfirmBooking;

public sealed record ConfirmBookingCommand(
    string IdempotencyKey,
    QuoteSnapshot Snapshot
);