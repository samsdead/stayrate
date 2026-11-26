using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace StayRate.Domain.Tests.Api;

public sealed class BookingsEndpointIntegrationTests : IClassFixture<BookingsEndpointIntegrationTests.StayRateApiFactory>
{
    private readonly HttpClient _client;

    public BookingsEndpointIntegrationTests(StayRateApiFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-StayRate-Test-Authenticated", "test");
    }

    [Fact]
    public async Task Confirm_WithoutIdempotencyKey_ReturnsBadRequest()
    {
        var request = CreateValidRequest();
        var response = await _client.PostAsJsonAsync("/api/v1/bookings/confirm", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Confirm_SameKeyAndPayload_ReturnsSameBooking()
    {
        var request = CreateValidRequest();
        var key = $"it-replay-{Guid.NewGuid():N}";

        using var message1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/confirm")
        {
            Content = JsonContent.Create(request)
        };
        message1.Headers.Add("Idempotency-Key", key);

        using var message2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/confirm")
        {
            Content = JsonContent.Create(request)
        };
        message2.Headers.Add("Idempotency-Key", key);

        var response1 = await _client.SendAsync(message1);
        var response2 = await _client.SendAsync(message2);

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        var body1 = JsonDocument.Parse(await response1.Content.ReadAsStringAsync());
        var body2 = JsonDocument.Parse(await response2.Content.ReadAsStringAsync());

        var bookingId1 = body1.RootElement.GetProperty("bookingId").GetString();
        var bookingId2 = body2.RootElement.GetProperty("bookingId").GetString();
        var hash1 = body1.RootElement.GetProperty("quoteSnapshotHash").GetString();
        var hash2 = body2.RootElement.GetProperty("quoteSnapshotHash").GetString();

        Assert.Equal(bookingId1, bookingId2);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task Confirm_SameKeyDifferentPayload_ReturnsConflict()
    {
        var key = $"it-conflict-{Guid.NewGuid():N}";
        var request1 = CreateValidRequest();
        var request2 = CreateValidRequest("property-2");

        using var message1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/confirm")
        {
            Content = JsonContent.Create(request1)
        };
        message1.Headers.Add("Idempotency-Key", key);

        using var message2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/confirm")
        {
            Content = JsonContent.Create(request2)
        };
        message2.Headers.Add("Idempotency-Key", key);

        var response1 = await _client.SendAsync(message1);
        var response2 = await _client.SendAsync(message2);
        var response2Body = await response2.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.True(
            response2.StatusCode == HttpStatusCode.Conflict,
            $"Expected 409, got {(int)response2.StatusCode}: {response2Body}");

        var problem = JsonDocument.Parse(response2Body);
        Assert.Equal("Conflict", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(409, problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Confirm_RequiresAuthenticatedCaller()
    {
        _client.DefaultRequestHeaders.Remove("X-StayRate-Test-Authenticated");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/confirm")
        {
            Content = JsonContent.Create(CreateValidRequest())
        };
        message.Headers.Add("Idempotency-Key", $"it-auth-{Guid.NewGuid():N}");

        var result = await _client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task Confirm_UsesConfiguredQuote()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/confirm")
        {
            Content = JsonContent.Create(CreateValidRequest(total: 250m))
        };
        message.Headers.Add("Idempotency-Key", $"it-quote-{Guid.NewGuid():N}");

        var result = await _client.SendAsync(message);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, result.StatusCode);
    }

    [Fact]
    public async Task BookingQuote_CanBeSubmittedForConfirmation()
    {
        var details = new
        {
            propertyId = "property-1",
            checkIn = "2026-04-01",
            checkOut = "2026-04-03",
            guests = 2
        };
        using var quote = await _client.PostAsJsonAsync("/api/v1/bookings/quote", details);
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);

        using var confirmation = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings/confirm")
        {
            Content = new StringContent(await quote.Content.ReadAsStringAsync(), System.Text.Encoding.UTF8, "application/json")
        };
        confirmation.Headers.Add("Idempotency-Key", $"it-booking-quote-{Guid.NewGuid():N}");
        using var result = await _client.SendAsync(confirmation);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    private static object CreateValidRequest(string propertyId = "property-1", decimal total = 200m)
    {
        return new
        {
            propertyId,
            checkIn = "2026-04-01",
            checkOut = "2026-04-03",
            guests = 2,
            lineItems = new[]
            {
                new
                {
                    code = "nightly",
                    description = "Nightly rate",
                    amount = new
                    {
                        amount = 100m,
                        currency = "GBP"
                    }
                }
            },
            total = new
            {
                amount = total,
                currency = "GBP"
            }
        };
    }

    private sealed class GatewayTestContext : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, pipeline) =>
            {
                if (context.Request.Headers["X-StayRate-Test-Authenticated"] == "test")
                {
                    context.Items[AbstractAspNetCoreFunction.LAMBDA_REQUEST_OBJECT] = new APIGatewayHttpApiV2ProxyRequest
                    {
                        RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                        {
                            RouteKey = $"POST {context.Request.Path}",
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
                }
                await pipeline(context);
            });
            next(app);
        };
    }

    public sealed class StayRateApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new[] { "property-1", "property-2" }.SelectMany(propertyId =>
                    new Dictionary<string, string?>
                    {
                        [$"Bookings:Properties:{propertyId}:NightlyRate"] = "100",
                        [$"Bookings:Properties:{propertyId}:Currency"] = "GBP",
                        [$"Bookings:Properties:{propertyId}:MaxGuests"] = "4"
                    })));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, GatewayTestContext>());
        }
    }
}
