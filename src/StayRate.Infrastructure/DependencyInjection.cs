using Microsoft.Extensions.DependencyInjection;
using StayRate.Application.Abstractions;
using StayRate.Infrastructure.Hashing;
using StayRate.Infrastructure.Idempotency;
using StayRate.Infrastructure.Persistence;
using StayRate.Infrastructure.Time;

namespace StayRate.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IBookingRepository, InMemoryBookingRepository>();
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.AddSingleton<IRequestHasher, CanonicalJsonRequestHasher>();
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }
}
