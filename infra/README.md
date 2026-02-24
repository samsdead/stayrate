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
- `prod`: table removal policy `RETAIN`, log retention `1 month`

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

## CodeDeploy Canary

Canary rollout is controlled via `enableCodeDeployCanary=true` CDK context.

If your AWS account does not have CodeDeploy enabled for this service, deploy scripts fail fast with a clear message.

## GitHub Actions Deploy

Required repository variable:

- `AWS_DEPLOY_ROLE_ARN` = IAM role ARN assumed by GitHub OIDC
The workflow has a fallback to `arn:aws:iam::762397612240:role/stayrate-github-deploy-role` if this variable is not set.

Workflows:

- `.github/workflows/deploy-dev.yml` (push + manual)
- `.github/workflows/deploy-prod.yml` (manual, GitHub `production` environment)

Optional variable for prod:

- `AWS_DEPLOY_ROLE_ARN_PROD`

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

aws iam attach-role-policy \
  --role-name stayrate-github-deploy-role \
  --policy-arn arn:aws:iam::aws:policy/AdministratorAccess
```
