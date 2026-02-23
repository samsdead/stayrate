using StayRate.Domain;
using StayRate.Domain.Bookings;
using StayRate.Domain.Common;

namespace StayRate.Domain.Tests.Bookings;

public sealed class BookingTests
{
    [Fact]
    public void CreateFromQuoteSnapshot_InitializesPendingBooking()
    {
        var now = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero);
        var snapshot = CreateSnapshot();

        var booking = Booking.CreateFromQuoteSnapshot(snapshot, "hash-1", now);

        Assert.NotEqual(Guid.Empty, booking.Id);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Equal(snapshot, booking.QuoteSnapshot);
        Assert.Equal("hash-1", booking.QuoteSnapshotHash);
        Assert.Equal(now, booking.CreatedAt);
        Assert.Null(booking.ConfirmedAt);
        Assert.Null(booking.CancelledAt);
    }

    [Fact]
    public void Confirm_FromPending_SetsConfirmedState()
    {
        var created = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero);
        var confirmed = created.AddMinutes(5);
        var booking = Booking.CreateFromQuoteSnapshot(CreateSnapshot(), "hash-2", created);

        booking.Confirm(confirmed);

        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(confirmed, booking.ConfirmedAt);
        Assert.Null(booking.CancelledAt);
    }

    [Fact]
    public void Confirm_WhenAlreadyConfirmed_IsIdempotent()
    {
        var created = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero);
        var firstConfirm = created.AddMinutes(5);
        var secondConfirm = created.AddMinutes(10);
        var booking = Booking.CreateFromQuoteSnapshot(CreateSnapshot(), "hash-3", created);
        booking.Confirm(firstConfirm);

        booking.Confirm(secondConfirm);

        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(firstConfirm, booking.ConfirmedAt);
    }

    [Fact]
    public void Cancel_FromPending_SetsCancelledState()
    {
        var created = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero);
        var cancelled = created.AddMinutes(5);
        var booking = Booking.CreateFromQuoteSnapshot(CreateSnapshot(), "hash-4", created);

        booking.Cancel(cancelled);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(cancelled, booking.CancelledAt);
    }

    [Fact]
    public void Cancel_FromConfirmed_SetsCancelledState()
    {
        var created = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero);
        var confirmed = created.AddMinutes(5);
        var cancelled = created.AddMinutes(15);
        var booking = Booking.CreateFromQuoteSnapshot(CreateSnapshot(), "hash-5", created);
        booking.Confirm(confirmed);

        booking.Cancel(cancelled);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(cancelled, booking.CancelledAt);
    }

    [Fact]
    public void Confirm_WhenCancelled_ThrowsDomainRuleViolation()
    {
        var created = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero);
        var booking = Booking.CreateFromQuoteSnapshot(CreateSnapshot(), "hash-6", created);
        booking.Cancel(created.AddMinutes(5));

        Assert.Throws<DomainRuleViolation>(() => booking.Confirm(created.AddMinutes(10)));
    }

    [Fact]
    public void CreateFromQuoteSnapshot_WithInvalidSnapshot_ThrowsDomainRuleViolation()
    {
        var now = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero);
        var invalid = new QuoteSnapshot(
            "",
            new DateOnly(2026, 3, 10),
            new DateOnly(2026, 3, 9),
            0,
            new List<QuoteSnapshotLineItem>(),
            new Money(0m, "GBP"));

        Assert.Throws<DomainRuleViolation>(() => Booking.CreateFromQuoteSnapshot(invalid, "hash-7", now));
    }

    [Fact]
    public void Rehydrate_ConfirmedBooking_RestoresState()
    {
        var created = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero);
        var confirmed = created.AddMinutes(5);

        var booking = Booking.Rehydrate(
            Guid.NewGuid(),
            BookingStatus.Confirmed,
            CreateSnapshot(),
            "hash-8",
            created,
            confirmed,
            null);

        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(confirmed, booking.ConfirmedAt);
        Assert.Null(booking.CancelledAt);
    }

    [Fact]
    public void Rehydrate_CancelledBooking_RestoresState()
    {
        var created = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero);
        var confirmed = created.AddMinutes(5);
        var cancelled = created.AddMinutes(10);

        var booking = Booking.Rehydrate(
            Guid.NewGuid(),
            BookingStatus.Cancelled,
            CreateSnapshot(),
            "hash-9",
            created,
            confirmed,
            cancelled);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(confirmed, booking.ConfirmedAt);
        Assert.Equal(cancelled, booking.CancelledAt);
    }

    [Fact]
    public void Rehydrate_ConfirmedWithoutConfirmedAt_ThrowsDomainRuleViolation()
    {
        var created = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero);

        Assert.Throws<DomainRuleViolation>(() =>
            Booking.Rehydrate(
                Guid.NewGuid(),
                BookingStatus.Confirmed,
                CreateSnapshot(),
                "hash-10",
                created,
                null,
                null));
    }

    private static QuoteSnapshot CreateSnapshot() =>
        new(
            "prop-1",
            new DateOnly(2026, 3, 10),
            new DateOnly(2026, 3, 12),
            2,
            new List<QuoteSnapshotLineItem>
            {
                new("nightly", "Nightly rate", new Money(100m, "GBP"))
            },
            new Money(200m, "GBP"));
}
