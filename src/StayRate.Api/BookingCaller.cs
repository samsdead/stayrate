using System.Net;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;

namespace StayRate.Api;

public sealed class BookingCaller(IHostEnvironment environment, IConfiguration configuration)
{
    public string? Resolve(HttpContext? context, string routeKey)
    {
        if (context is null)
            return null;

        if (context.Items.TryGetValue(AbstractAspNetCoreFunction.LAMBDA_REQUEST_OBJECT, out var request))
        {
            var gateway = (request as APIGatewayHttpApiV2ProxyRequest)?.RequestContext;
            var iam = gateway?.Authorizer?.IAM;
            if (gateway?.RouteKey != routeKey ||
                string.IsNullOrWhiteSpace(iam?.AccountId) || string.IsNullOrWhiteSpace(iam.UserId))
                return null;

            // IAM role sessions share the stable principal ID before the session name.
            var principalId = iam.UserId.Split(':', 2)[0];
            return principalId.Length > 0 ? $"{iam.AccountId}:{principalId}" : null;
        }

        if (environment.IsDevelopment() &&
            configuration.GetValue<bool>("Bookings:AllowLoopbackDevelopment") &&
            context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address))
            return "local-development";

        return null;
    }
}
