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

## GitHub Actions deployment

Create two GitHub environments named `development` and `production`. Limit both to the protected `master` branch. Require a production reviewer and protect `v*.*.*` tags from unauthorized creation and changes. The production workflow builds a reviewed tag reachable from the branch. CI checks the API routes before upload and holds AWS credentials only in the deployment job.

Configure these variables in each environment:

| Variable | Development | Production |
| --- | --- | --- |
| `AWS_DEPLOY_ACCOUNT_ID` | Development AWS account ID | Production AWS account ID |
| `AWS_DEPLOY_ROLE_ARN` | ARN of `stayrate-github-dev` | ARN of `stayrate-github-prod` |
| `AWS_CLOUDFORMATION_ROLE_ARN` | ARN of `stayrate-dev-cloudformation` | ARN of `stayrate-prod-cloudformation` |
| `AWS_SERVICE_ROLE_BOUNDARY_ARN` | ARN of `stayrate-dev-service-boundary` | ARN of `stayrate-prod-service-boundary` |
| `SLACK_WEBHOOK_SECRET_ARN` | Optional Secrets Manager ARN | Optional Secrets Manager ARN |

In each AWS environment, provision a versioned artifact bucket named `stayrate-dev-artifacts-<account-id>-eu-west-2` or `stayrate-prod-artifacts-<account-id>-eu-west-2`. Enable S3 Block Public Access and enforce bucket-owner ownership. Provision the CloudFormation role once through your AWS administrator. Scope its permissions to that environment's stack resources, including reading the versioned artifact, and require the environment permissions boundary on the runtime service roles that it manages. The deployment passes the boundary ARN to the stack so it can attach the policy to its Lambda and optional CodeDeploy roles. If you supply an execution role after migrating an existing stack, confirm its scope before updating the stack.

Use [the checked-in deploy role policy template](iam/stayrate-github-deploy-inline-policy.json) for each GitHub role. Replace `<stage>` with `dev` or `prod`, `<stack-name>` with the matching stack name, `<account-id>` with the environment account, and `<region>` with the deployment region. Each GitHub role may upload its own Lambda artifact, deploy its one stack, and pass its one execution role to CloudFormation. It has no direct IAM policy management or runtime API invocation permission.

### OIDC trust policy

Set each GitHub deploy role's trust to the exact environment subject. The example below shows the development role in classic subject format. For production use `environment:production` and the production role's account. If the repository uses immutable or customized subjects, replace the value with that environment's exact subject before attaching the trust policy.

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
          "token.actions.githubusercontent.com:aud": "sts.amazonaws.com",
          "token.actions.githubusercontent.com:sub": "repo:samsdead/stayrate:environment:development"
        }
      }
    }
  ]
}
```

Remove the previous shared deployment role after both workflows use their new roles. AWS IAM and GitHub environment configuration happens outside the repository.

### Catalog and alert configuration

Set `BOOKING_CATALOG_JSON` as an environment secret when the stack should accept new bookings. Use a compact JSON object with the shape `{"Bookings":{"Properties":{"demo-property":{"NightlyRate":100,"Currency":"GBP","MaxGuests":4}}}}`. The deployment workflow uses an empty catalog when the secret is unset. Manage the catalog as deployment configuration containing rates and guest limits. Keep personal data and service credentials out of it.

Booking callers need a same-account IAM policy for `execute-api:Invoke` on `arn:aws:execute-api:<region>:<account-id>:<api-id>/$default/POST/api/v1/bookings/quote` and `arn:aws:execute-api:<region>:<account-id>:<api-id>/$default/POST/api/v1/bookings/confirm`. They must sign requests with SigV4 for the `execute-api` service. The health, ready, and quote routes are public and throttled. The dev default is 10 requests per second with a burst of 20, and prod is 25 with a burst of 50. The booking routes are limited to 2 and 5 requests per second respectively.

For Slack, point `SLACK_WEBHOOK_SECRET_ARN` at a same-account, same-region Secrets Manager secret whose value is the complete Slack webhook URL. The relay retrieves it at runtime and sends only the alarm name and state. To rotate a previously configured plaintext webhook, create a replacement on Slack, store it in Secrets Manager, then revoke the old webhook. Passing an empty Slack ARN explicitly when deploying manually clears an existing value.

### Manual deployments

From the repository root, with AWS CLI, a pre-bootstrapped CDK setup, .NET 10, and Amazon.Lambda.Tools:

```powershell
./scripts/deploy-dev.ps1 -Profile stayrate-dev -Region eu-west-2
./scripts/deploy-prod.ps1 -Profile stayrate-prod -Region eu-west-2 -EnableCodeDeployCanary false
```

Either script accepts `BookingCatalogPath`, `SlackWebhookSecretArn`, `ServiceRoleBoundaryArn`, and `AlertEmail`. Use the GitHub workflows for routine releases. Configure budget alerts in AWS separately if the account needs cost notifications.

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
  1. Correlate with app logs using server-generated trace ID
  2. Validate downstream DynamoDB health/throttles
  3. Redeploy last known good build if regression confirmed

Alarm delivery:

- SNS topic: `stayrate-<env>-alerts`
- Relay Lambda: `stayrate-<env>-alerts-relay`
- Optional email subscription via `AlertEmail`

Manual confirmation required:

- SNS email subscriptions stay in `PendingConfirmation` until the recipient clicks the confirmation link.
