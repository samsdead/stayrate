# StayRate Infra

This CDK app deploys two serverless stacks:

- `StayRateDevStack`
- `StayRateProdStack`

Each stack includes:

- Lambda function for `StayRate.Api`
- Lambda alias (`live`)
- API Gateway HTTP API
- DynamoDB table: bookings
- DynamoDB table: idempotency
- SNS topic for alerts
- Alerts relay Lambda subscribed to SNS
- CloudWatch alarms and dashboard
- SSM parameters for table names

Environment differences:

- `dev`: table removal policy `DESTROY`, log retention `2 weeks`
- `prod`: table removal policy `RETAIN`, log retention `1 month`, DynamoDB PITR `enabled`

## Prerequisites

- AWS CLI configured (`AWS_PROFILE`, `AWS_REGION`)
- CDK CLI installed
- .NET SDK installed
- Amazon Lambda .NET tool installed (`Amazon.Lambda.Tools`)

## Deploy Dev

From repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-dev.ps1 -Profile stayrate-dev -Region eu-west-2
```

## Deploy Prod

From repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-prod.ps1 -Profile stayrate-dev -Region eu-west-2 -EnableCodeDeployCanary false
```

The script will:

1. Package Lambda artifact from `src/StayRate.Api`
2. Upload artifact to S3
3. Bootstrap CDK (if needed)
4. Deploy selected stack
5. Print deployed API endpoint

`AlertEmail` and `SlackWebhookUrl` can be passed to either script:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-dev.ps1 -Profile stayrate-dev -Region eu-west-2 -AlertEmail "you@example.com"
```

## CodeDeploy Canary

Canary rollout is controlled via `enableCodeDeployCanary=true` CDK context.

If your AWS account does not have CodeDeploy enabled for this service, deploy scripts fail fast with a clear message.

## GitHub Actions Deploy

Required repository variable:

- `AWS_DEPLOY_ROLE_ARN` = IAM role ARN assumed by GitHub OIDC

Workflows:

- `.github/workflows/deploy-dev.yml` (push + manual)
- `.github/workflows/deploy-prod.yml` (manual only, requires a semantic tag input and uses GitHub `production` environment)
- `.github/workflows/release.yml` (auto-generates release notes on `v*.*.*` tags)

Optional variable for prod:

- `AWS_DEPLOY_ROLE_ARN_PROD`
- `ALERT_EMAIL`
- `ALERT_EMAIL_PROD`

IAM deploy policy:

- `infra/iam/stayrate-github-deploy-inline-policy.json`
- Attach as inline policy to `stayrate-github-deploy-role`

## Cost Guardrails

Budget configured:

- `stayrate-monthly-cap`
- Monthly cap: `5 USD`
- Alert: `ACTUAL > 80%`
- Subscriber: configured via `ALERT_EMAIL` / `ALERT_EMAIL_PROD` repository variables

## Persistence Configuration

Runtime provider resolution:

1. `Persistence__Provider` environment variable
2. `Persistence:Provider` in appsettings

Table name resolution (when provider is `DynamoDb`):

1. `DynamoDb:BookingsTableName`, then `Bookings:TableName`
2. `DynamoDb:IdempotencyTableName`, then `Idempotency:TableName`

Deployed Lambda sets:

- `Persistence__Provider=DynamoDb`
- `Bookings__TableName=<stack table>`
- `Idempotency__TableName=<stack table>`

Local development remains `InMemory` by default via `src/StayRate.Api/appsettings.json`.

## Runbook

Alarm: `stayrate-<env>-lambda-errors`

- Check: CloudWatch Alarm history, then Lambda logs for `stayrate-<env>-api`
- Recover:
  1. Inspect recent deploy run and smoke test output
  2. Roll forward a fix with new artifact deploy
  3. If canary is enabled in account, allow auto-rollback to complete before redeploy

Alarm: `stayrate-<env>-api-5xx`

- Check: CloudWatch dashboard `stayrate-<env>`, API Gateway metrics (`5xx`, `Latency`, `Count`)
- Recover:
  1. Correlate with app logs using trace id and idempotency key
  2. Validate downstream DynamoDB health/throttles
  3. Redeploy last known good build if regression confirmed

Alarm delivery:

- SNS topic: `stayrate-<env>-alerts`
- Relay Lambda: `stayrate-<env>-alerts-relay`
- Optional email subscription via `AlertEmail`

Manual confirmation required:

- SNS email subscriptions stay in `PendingConfirmation` until the recipient clicks the confirmation link.

Trust policy for the IAM role:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": {
        "Federated": "arn:aws:iam::<account-id>:oidc-provider/token.actions.githubusercontent.com"
      },
      "Action": "sts:AssumeRoleWithWebIdentity",
      "Condition": {
        "StringEquals": {
          "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
        },
        "StringLike": {
          "token.actions.githubusercontent.com:sub": "repo:samsdead/stayrate:*"
        }
      }
    }
  ]
}
```

Create role and attach deploy permissions:

```bash
aws iam create-role \
  --role-name stayrate-github-deploy-role \
  --assume-role-policy-document file://trust-policy.json

aws iam put-role-policy \
  --role-name stayrate-github-deploy-role \
  --policy-name stayrate-github-deploy-inline \
  --policy-document file://infra/iam/stayrate-github-deploy-inline-policy.json
```
