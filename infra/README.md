# StayRate Infra

This CDK app deploys the `dev` serverless stack:

- Lambda function for `StayRate.Api`
- API Gateway HTTP API
- DynamoDB table: bookings
- DynamoDB table: idempotency
- SSM parameters for table names

## Prerequisites

- AWS CLI configured (`AWS_PROFILE`, `AWS_REGION`)
- CDK CLI installed
- .NET SDK installed
- Amazon Lambda .NET tool installed (`Amazon.Lambda.Tools`)

## Deploy

From repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-dev.ps1 -Profile stayrate-dev -Region eu-west-2
```

The script will:

1. Package Lambda artifact from `src/StayRate.Api`
2. Upload artifact to S3
3. Bootstrap CDK (if needed)
4. Deploy `StayRateDevStack`
5. Print deployed API endpoint

## GitHub Actions Deploy

Workflow: `.github/workflows/deploy-dev.yml`

Required repository variable:

- `AWS_DEPLOY_ROLE_ARN` = IAM role ARN assumed by GitHub OIDC

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
