
using StayRate.Domain.Common;

namespace StayRate.Domain.Bookings;

public sealed class Booking : IAggregateRoot
{
    public Guid Id { get; private set; }

    public BookingStatus Status { get; private set; }

    public QuoteSnapshot QuoteSnapshot { get; private set; } = default!;

    public string QuoteSnapshotHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    // For serializers/ORMs later (EF Core) – keep private to protect invariants.
    private Booking() { }

    private Booking(Guid id, QuoteSnapshot snapshot, string snapshotHash, DateTimeOffset now)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshotHash)) throw new ArgumentException("Snapshot hash is required.", nameof(snapshotHash));

        ValidateSnapshot(snapshot);

        Id = id;
        QuoteSnapshot = snapshot;
        QuoteSnapshotHash = snapshotHash;

        Status = BookingStatus.Pending;
        CreatedAt = now;
    }

    public static Booking CreateFromQuoteSnapshot(QuoteSnapshot snapshot, string snapshotHash, DateTimeOffset now)
        => new(Guid.NewGuid(), snapshot, snapshotHash, now);

    public void Confirm(DateTimeOffset now)
    {
        if (Status == BookingStatus.Confirmed) return;

        if (Status != BookingStatus.Pending)
            throw new DomainRuleViolation($"Cannot confirm booking in status '{Status}'.");

        Status = BookingStatus.Confirmed;
        ConfirmedAt = now;
        CancelledAt = null;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status == BookingStatus.Cancelled) return;

        if (Status is not (BookingStatus.Pending or BookingStatus.Confirmed))
            throw new DomainRuleViolation($"Cannot cancel booking in status '{Status}'.");

        Status = BookingStatus.Cancelled;
        CancelledAt = now;
    }

    private static void ValidateSnapshot(QuoteSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.PropertyId))
            throw new DomainRuleViolation("Quote snapshot must include PropertyId.");

        if (snapshot.Guests <= 0)
            throw new DomainRuleViolation("Guests must be greater than 0.");

        if (snapshot.CheckOut <= snapshot.CheckIn)
            throw new DomainRuleViolation("CheckOut must be after CheckIn.");

        if (snapshot.LineItems is null)
            throw new DomainRuleViolation("LineItems must be provided.");

    }
}