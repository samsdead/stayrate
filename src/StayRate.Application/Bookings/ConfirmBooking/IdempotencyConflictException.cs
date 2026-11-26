namespace StayRate.Application.Bookings.ConfirmBooking;

public sealed class IdempotencyConflictException()
    : Exception("This idempotency key cannot be used for this request.");
