namespace StayRate.Application.Bookings.ConfirmBooking;

public sealed class IdempotencyConflictException : Exception
{
    public string IdempotencyKey { get; }

    public IdempotencyConflictException(string idempotencyKey)
        : base($"Idempotency-Key '{idempotencyKey}' was reused with a different request payload.")
    {
        IdempotencyKey = idempotencyKey;
    }
}