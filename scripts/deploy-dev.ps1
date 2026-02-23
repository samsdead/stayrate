param(
    [string]$Profile = "stayrate-dev",
    [string]$Region = "eu-west-2"
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

$env:AWS_PROFILE = $Profile
$env:AWS_REGION = $Region

$lambdaTool = Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-lambda.exe"
if (-not (Test-Path $lambdaTool)) {
    throw "Amazon.Lambda.Tools not found at $lambdaTool"
}

Invoke-Checked -Command { aws sts get-caller-identity --query Account --output text | Out-File -FilePath "$env:TEMP\stayrate-account.txt" -Encoding ascii } -FailureMessage "Failed to resolve AWS account id."
$accountId = (Get-Content "$env:TEMP\stayrate-account.txt" -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($accountId)) {
    throw "Could not resolve AWS account id."
}

$artifactBucket = "stayrate-dev-artifacts-$accountId-$Region"
$artifactPrefix = "lambda"
$timestamp = Get-Date -Format "yyyyMMddHHmmss"
$artifactKey = "$artifactPrefix/stayrate-api-$timestamp.zip"

$existingBucketRaw = aws s3api list-buckets --query "Buckets[?Name=='$artifactBucket'].Name" --output text
$existingBucket = if ($null -eq $existingBucketRaw) { "" } else { "$existingBucketRaw".Trim() }
$bucketExists = -not [string]::IsNullOrWhiteSpace($existingBucket)

if (-not $bucketExists) {
    if ($Region -eq "us-east-1") {
        Invoke-Checked -Command { aws s3api create-bucket --bucket $artifactBucket | Out-Null } -FailureMessage "Failed to create artifact bucket."
    }
    else {
        Invoke-Checked -Command { aws s3api create-bucket --bucket $artifactBucket --create-bucket-configuration LocationConstraint=$Region | Out-Null } -FailureMessage "Failed to create artifact bucket."
    }
}

$artifactDir = Join-Path $repoRoot "artifacts"
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
$artifactPath = Join-Path $artifactDir "stayrate-api.zip"
if (Test-Path $artifactPath) {
    Remove-Item $artifactPath -Force
}

Invoke-Checked -Command {
    & $lambdaTool package `
        --project-location "src/StayRate.Api" `
        --configuration "Release" `
        --framework "net10.0" `
        --function-architecture "arm64" `
        --output-package $artifactPath `
        --disable-interactive true `
        --msbuild-parameters "/p:SelfContained=true /p:PublishSingleFile=true /p:RuntimeIdentifier=linux-arm64 /p:InvariantGlobalization=true"
} -FailureMessage "Failed to package Lambda artifact."

Invoke-Checked -Command { aws s3 cp $artifactPath "s3://$artifactBucket/$artifactKey" | Out-Null } -FailureMessage "Failed to upload Lambda artifact to S3."

Push-Location "infra"
try {
    Invoke-Checked -Command { cdk bootstrap "aws://$accountId/$Region" | Out-Null } -FailureMessage "CDK bootstrap failed."
    Invoke-Checked -Command {
        cdk deploy StayRateDevStack `
            --require-approval never `
            --parameters "LambdaArtifactBucket=$artifactBucket" `
            --parameters "LambdaArtifactKey=$artifactKey"
    } -FailureMessage "CDK deploy failed."
}
finally {
    Pop-Location
}

Invoke-Checked -Command {
    aws cloudformation describe-stacks --stack-name StayRateDevStack --query "Stacks[0].Outputs[?OutputKey=='ApiEndpoint'].OutputValue" --output text | Out-File -FilePath "$env:TEMP\stayrate-endpoint.txt" -Encoding ascii
} -FailureMessage "Failed to fetch API endpoint from CloudFormation outputs."
$apiEndpoint = (Get-Content "$env:TEMP\stayrate-endpoint.txt" -Raw).Trim()

Write-Host "DEPLOY_COMPLETE"
Write-Host "Account: $accountId"
Write-Host "Region: $Region"
Write-Host "Bucket: $artifactBucket"
Write-Host "ArtifactKey: $artifactKey"
Write-Host "ApiEndpoint: $apiEndpoint"
