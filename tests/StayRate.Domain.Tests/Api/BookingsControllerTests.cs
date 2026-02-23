using Microsoft.AspNetCore.Mvc;
using StayRate.Api.Controllers;
using StayRate.Application.Bookings.ConfirmBooking;
using StayRate.Domain;
using StayRate.Domain.Bookings;
using StayRate.Infrastructure.Hashing;
using StayRate.Infrastructure.Idempotency;
using StayRate.Infrastructure.Persistence;
using StayRate.Infrastructure.Time;

namespace StayRate.Domain.Tests.Api;

public sealed class BookingsControllerTests
{
    [Fact]
    public async Task Confirm_WithoutIdempotencyHeader_ReturnsBadRequest()
    {
        var controller = CreateController();
        var request = CreateRequest("prop-1");

        var result = await controller.Confirm(null, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
    }

    [Fact]
    public async Task Confirm_WithValidRequest_ReturnsOkResult()
    {
        var controller = CreateController();
        var request = CreateRequest("prop-2");

        var actionResult = await controller.Confirm("idem-api-1", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var payload = Assert.IsType<ConfirmBookingResult>(ok.Value);
        Assert.Equal(BookingStatus.Confirmed, payload.Status);
        Assert.NotEqual(Guid.Empty, payload.BookingId);
    }

    [Fact]
    public async Task Confirm_WhenSameKeyDifferentPayload_ReturnsConflict()
    {
        var controller = CreateController();

        await controller.Confirm("idem-api-2", CreateRequest("prop-3"), CancellationToken.None);
        var second = await controller.Confirm("idem-api-2", CreateRequest("prop-4"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(second.Result);
        Assert.Equal(409, conflict.StatusCode);
    }

    private static BookingsController CreateController()
    {
        var handler = new ConfirmBookingHandler(
            new InMemoryBookingRepository(),
            new InMemoryIdempotencyStore(),
            new CanonicalJsonRequestHasher(),
            new SystemClock());

        return new BookingsController(handler);
    }

    private static BookingsController.ConfirmBookingRequest CreateRequest(string propertyId)
    {
        return new BookingsController.ConfirmBookingRequest(
            propertyId,
            new DateOnly(2026, 3, 10),
            new DateOnly(2026, 3, 12),
            2,
            new List<BookingsController.QuoteSnapshotLineItemRequest>
            {
                new("nightly", "Nightly rate", new BookingsController.MoneyRequest(100m, "GBP"))
            },
            new BookingsController.MoneyRequest(200m, "GBP"));
    }
}
