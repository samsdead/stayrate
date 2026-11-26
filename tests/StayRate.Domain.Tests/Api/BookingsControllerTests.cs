using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using StayRate.Api;
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
    public async Task Confirm_WhenSameKeyDifferentPayload_ThrowsIdempotencyConflictException()
    {
        var controller = CreateController();

        await controller.Confirm("idem-api-2", CreateRequest("prop-3"), CancellationToken.None);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            controller.Confirm("idem-api-2", CreateRequest("prop-4"), CancellationToken.None));
    }

    private static BookingsController CreateController()
    {
        var repository = new InMemoryBookingRepository();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new[] { "prop-1", "prop-2", "prop-3", "prop-4" }.SelectMany(propertyId =>
                new Dictionary<string, string?>
                {
                    [$"Bookings:Properties:{propertyId}:NightlyRate"] = "100",
                    [$"Bookings:Properties:{propertyId}:Currency"] = "GBP",
                    [$"Bookings:Properties:{propertyId}:MaxGuests"] = "4"
                })).Build();
        var handler = new ConfirmBookingHandler(
            repository,
            new InMemoryIdempotencyStore(repository),
            new CanonicalJsonRequestHasher(),
            new SystemClock(),
            new BookingQuoteCatalog(config));

        var context = new DefaultHttpContext();
        context.Items[AbstractAspNetCoreFunction.LAMBDA_REQUEST_OBJECT] = new APIGatewayHttpApiV2ProxyRequest
        {
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                RouteKey = "POST /api/v1/bookings/confirm",
                Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                {
                    IAM = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.IAMDescription
                    {
                        AccountId = "000000000000",
                        UserId = "test-caller"
                    }
                }
            }
        };
        return new BookingsController(handler, new BookingCaller(Mock.Of<Microsoft.Extensions.Hosting.IHostEnvironment>(), config), new BookingQuoteCatalog(config))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static BookingsController.ConfirmBookingRequest CreateRequest(string propertyId)
    {
        return new BookingsController.ConfirmBookingRequest
        {
            PropertyId = propertyId,
            CheckIn = new DateOnly(2026, 3, 10),
            CheckOut = new DateOnly(2026, 3, 12),
            Guests = 2,
            LineItems =
            [
                new BookingsController.QuoteSnapshotLineItemRequest
                {
                    Code = "nightly",
                    Description = "Nightly rate",
                    Amount = new BookingsController.MoneyRequest
                    {
                        Amount = 100m,
                        Currency = "GBP"
                    }
                }
            ],
            Total = new BookingsController.MoneyRequest
            {
                Amount = 200m,
                Currency = "GBP"
            }
        };
    }
}
