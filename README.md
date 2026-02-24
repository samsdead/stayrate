# StayRate

Production-focused .NET 10 backend built with Clean Architecture for pricing and idempotent booking confirmation.
Production demo project, not for real booking traffic.

## Highlights

- .NET 10, xUnit, Clean Architecture (`Api`, `Application`, `Domain`, `Infrastructure`)
- Idempotent booking confirmation with request hashing and replay safety
- Global exception handling + Problem Details
- Request validation and health endpoints (`/health`, `/ready`)
- Structured logging with request context
- Serverless deployment on AWS (Lambda + HTTP API + DynamoDB)
- CI/CD with GitHub Actions, OIDC deploy role, semantic tag releases
- CloudWatch alarms + dashboard, SNS alerting, AWS budget guardrail

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

## Key Flow: Confirm Booking

1. `POST /api/v1/bookings/confirm` with `Idempotency-Key`
2. Request snapshot is hashed
3. If key exists:
   - same hash -> return existing booking
   - different hash -> `409 Conflict`
4. If key is new:
   - create + confirm booking
   - persist booking + idempotency record
   - return confirmed result

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

## Deployment

- Dev deploy workflow: `.github/workflows/deploy-dev.yml` (auto on push to `master`)
- Prod deploy workflow: `.github/workflows/deploy-prod.yml` (manual, semantic tag input)
- Release workflow: `.github/workflows/release.yml` (`v*.*.*` tags)

For AWS infra/deploy details, see `infra/README.md`.

## API Endpoints

- `POST /api/v1/quote`
- `POST /api/v1/bookings/confirm`
- `GET /health`
- `GET /ready`

## Quality Gates

- Unit and integration tests in CI
- Live smoke tests after deploy
- Idempotency replay verification in pipeline
