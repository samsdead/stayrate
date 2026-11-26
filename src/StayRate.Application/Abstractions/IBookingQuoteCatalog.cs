using StayRate.Domain.Bookings;

namespace StayRate.Application.Abstractions;

public interface IBookingQuoteCatalog
{
    QuoteSnapshot? FindQuote(string propertyId, DateOnly checkIn, DateOnly checkOut, int guests);
}
