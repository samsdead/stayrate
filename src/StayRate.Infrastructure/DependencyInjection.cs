using Amazon.DynamoDBv2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StayRate.Application.Abstractions;
using StayRate.Infrastructure.Hashing;
using StayRate.Infrastructure.Idempotency.DynamoDb;
using StayRate.Infrastructure.Idempotency;
using StayRate.Infrastructure.Persistence.DynamoDb;
using StayRate.Infrastructure.Persistence;
using StayRate.Infrastructure.Time;

namespace StayRate.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IRequestHasher, CanonicalJsonRequestHasher>();
        services.AddSingleton<IClock, SystemClock>();

        var provider = configuration["Persistence:Provider"];
        if (string.Equals(provider, "DynamoDb", StringComparison.OrdinalIgnoreCase))
        {
            var bookingsTableName =
                configuration["DynamoDb:BookingsTableName"] ??
                configuration["Bookings:TableName"];

            var idempotencyTableName =
                configuration["DynamoDb:IdempotencyTableName"] ??
                configuration["Idempotency:TableName"];

            if (string.IsNullOrWhiteSpace(bookingsTableName))
                throw new InvalidOperationException("DynamoDb bookings table name is required.");

            if (string.IsNullOrWhiteSpace(idempotencyTableName))
                throw new InvalidOperationException("DynamoDb idempotency table name is required.");

            services.AddSingleton(new DynamoDbTableNames(bookingsTableName, idempotencyTableName));
            services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
            services.AddSingleton<IBookingRepository, DynamoDbBookingRepository>();
            services.AddSingleton<IIdempotencyStore, DynamoDbIdempotencyStore>();
            return services;
        }

        services.AddSingleton<InMemoryBookingRepository>();
        services.AddSingleton<IBookingRepository>(provider => provider.GetRequiredService<InMemoryBookingRepository>());
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        return services;
    }
}
