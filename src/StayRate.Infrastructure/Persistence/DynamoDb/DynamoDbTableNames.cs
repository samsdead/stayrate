namespace StayRate.Infrastructure.Persistence.DynamoDb;

public sealed record DynamoDbTableNames(
    string BookingsTableName,
    string IdempotencyTableName
);
