using StayRate.Application.Abstractions;
using StayRate.Domain;
using StayRate.Domain.Bookings;

namespace StayRate.Api;

public sealed class BookingQuoteCatalog(IConfiguration configuration) : IBookingQuoteCatalog
{
    public QuoteSnapshot? FindQuote(string propertyId, DateOnly checkIn, DateOnly checkOut, int guests)
    {
        var nights = checkOut.DayNumber - checkIn.DayNumber;
        if (nights is < 1 or > 30 || guests is < 1 or > 20 ||
            propertyId.Length is < 1 or > 80 ||
            propertyId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return null;

        var property = configuration.GetSection($"Bookings:Properties:{propertyId}");
        var nightly = property.GetValue<decimal>("NightlyRate");
        var currency = property["Currency"];
        var maxGuests = property.GetValue<int>("MaxGuests");

        if (nightly is < 0.01m or > 1_000_000m || decimal.Round(nightly, 2) != nightly || guests > maxGuests ||
            currency is not { Length: 3 } || !currency.All(char.IsAsciiLetter))
            return null;

        return new QuoteSnapshot(
            propertyId, checkIn, checkOut, guests,
            [new("nightly", "Nightly rate", new Money(nightly, currency))],
            new Money(nightly * nights, currency));
    }
}
