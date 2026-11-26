# StayRate

Production-focused .NET 10 backend built with Clean Architecture for pricing and idempotent booking confirmation.
Production demo project, not for real booking traffic.

## Highlights

- .NET 10, xUnit, Clean Architecture (`Api`, `Application`, `Domain`, `Infrastructure`)
- Authenticated booking confirmation with server-owned rates and atomic idempotency
- Global exception handling + Problem Details
- Request validation and health endpoints (`/health`, `/ready`)
- Structured logging with request context
- Serverless deployment on AWS (Lambda + HTTP API + DynamoDB)
- CI/CD with GitHub Actions, OIDC deploy role, semantic tag releases
- CloudWatch alarms, dashboard, and SNS alerting

## Architecture

```text
Client
  -> API (ASP.NET Core on Lambda)
    -> Application use cases
      -> Domain model + rules
      -> Infrastructure adapters
         -> DynamoDB (bookings, idempotency)
         -> CloudWatch / SNS / API Gateway
```

## Booking confirmation

1. Send a signed `POST /api/v1/bookings/quote` with the property ID, dates, and guest count.
2. Send the returned quote in a signed `POST /api/v1/bookings/confirm` with an `Idempotency-Key`.
3. For a new confirmation, the API matches the submitted detail against the configured property, nightly rate, currency, and guest limit, then persists the server's quote.
4. The booking and idempotency record are written in one transaction. Repeating the same request under the same IAM principal returns the existing result. Reusing its key with different details returns `409 Conflict`.

Booking writes are disabled when no property catalog is configured. Pre-existing bookings remain stored. Legacy keys without an authenticated owner return a conflict until their idempotency record expires.

## Project Structure

- `src/StayRate.Api`
- `src/StayRate.Application`
- `src/StayRate.Domain`
- `src/StayRate.Infrastructure`
- `tests/StayRate.Domain.Tests`
- `infra` (AWS CDK + deployment docs/policies)

## Local Run

```bash
dotnet build StayRate.slnx -v minimal
dotnet test StayRate.slnx -v minimal
dotnet run --project src/StayRate.Api
```

For loopback booking tests during local development, save the following in the ignored `src/StayRate.Api/appsettings.Local.json`. Keep the development host bound to loopback.

```json
{
  "Bookings": {
    "AllowLoopbackDevelopment": true,
    "Properties": {
      "demo-property": { "NightlyRate": 100, "Currency": "GBP", "MaxGuests": 4 }
    }
  }
}
```

Use the property ID above, a `nightly` line item described as `Nightly rate` with an amount of 100 GBP, and a total equal to 100 GBP times the number of nights. The local setting is limited to the ASP.NET Core Development environment and loopback connections.

## Deployment

- Dev deploy workflow: `.github/workflows/deploy-dev.yml` (auto on push to `master`)
- Prod deploy workflow: `.github/workflows/deploy-prod.yml` (manual, semantic tag input)
- Release workflow: `.github/workflows/release.yml` (`v*.*.*` tags)

For AWS infra/deploy details, see `infra/README.md`.

## API Endpoints

- `POST /api/v1/quote` calculates a public pricing estimate
- `POST /api/v1/bookings/quote` returns the property quote and requires AWS IAM on the deployed API
- `POST /api/v1/bookings/confirm` requires AWS IAM on the deployed API
- `GET /health`
- `GET /ready`

## Quality Gates

- Unit and integration tests in CI
- Local integration tests for authentication, quote verification, and idempotency
- Public health and readiness checks after deployment
