param(
    [string]$Profile = "",
    [string]$Region = "eu-west-2",
    [string]$EnableCodeDeployCanary = "false",
    [string]$AlertEmail = "",
    [string]$SlackWebhookSecretArn = "",
    [string]$BookingCatalogPath = "",
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^arn:[^:]+:iam::[0-9]{12}:policy/.+$')]
    [string]$ServiceRoleBoundaryArn
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

$configureSlackWebhook = $PSBoundParameters.ContainsKey("SlackWebhookSecretArn")
$bookingCatalogJson = ""
if (-not [string]::IsNullOrWhiteSpace($BookingCatalogPath)) {
    $bookingCatalogJson = Get-Content -LiteralPath $BookingCatalogPath -Raw
    $config = $bookingCatalogJson | ConvertFrom-Json
    if ($null -eq $config.Bookings.Properties) {
        throw "The booking catalog must contain Bookings.Properties."
    }
    $bookingCatalogJson = $config | ConvertTo-Json -Depth 12 -Compress
}
if ([string]::IsNullOrWhiteSpace($EnableCodeDeployCanary)) {
    $EnableCodeDeployCanary = "false"
}
$canaryEnabled = $EnableCodeDeployCanary.ToLowerInvariant() -in @("true", "1", "yes", "y")
if (-not [string]::IsNullOrWhiteSpace($Profile)) {
    $env:AWS_PROFILE = $Profile
}
$env:AWS_REGION = $Region

Invoke-Checked -Command { dotnet lambda --help | Out-Null } -FailureMessage "Amazon.Lambda.Tools is not installed or not on PATH."
if ($canaryEnabled) {
    aws deploy list-applications --output text | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "CodeDeploy is not available in this AWS account. Disable canary or enable CodeDeploy subscription first."
    }
}

$accountId = aws sts get-caller-identity --query Account --output text
if ($LASTEXITCODE -ne 0 -or $accountId -notmatch '^\d{12}$') {
    throw "Could not resolve AWS account id."
}

$artifactBucket = "stayrate-dev-artifacts-$accountId-$Region"
$artifactPrefix = "lambda"
$timestamp = Get-Date -Format "yyyyMMddHHmmss"
$artifactKey = "$artifactPrefix/stayrate-api-$timestamp.zip"
$stackName = "StayRateDevStack"

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

Invoke-Checked -Command {
    aws s3api put-bucket-versioning --bucket $artifactBucket --versioning-configuration Status=Enabled | Out-Null
} -FailureMessage "Failed to enable S3 bucket versioning."

$artifactDir = Join-Path $repoRoot "artifacts"
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
$artifactPath = Join-Path $artifactDir "stayrate-api.zip"
if (Test-Path $artifactPath) {
    Remove-Item $artifactPath -Force
}

Invoke-Checked -Command {
    dotnet lambda package `
        --project-location "src/StayRate.Api" `
        --configuration "Release" `
        --framework "net10.0" `
        --function-architecture "arm64" `
        --output-package $artifactPath `
        --disable-interactive true `
        --msbuild-parameters "/p:SelfContained=true /p:PublishSingleFile=true /p:RuntimeIdentifier=linux-arm64 /p:InvariantGlobalization=true"
} -FailureMessage "Failed to package Lambda artifact."

$objectVersion = aws s3api put-object --bucket $artifactBucket --key $artifactKey --body $artifactPath --expected-bucket-owner $accountId --checksum-algorithm SHA256 --query VersionId --output text
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($objectVersion) -or $objectVersion -in @("None", "null")) {
    throw "S3 did not return a version for the uploaded Lambda artifact."
}

Push-Location "infra"
try {
    Invoke-Checked -Command {
        $deployArgs = @(
            "deploy", $stackName,
            "--require-approval", "never",
            "--parameters", "LambdaArtifactBucket=$artifactBucket",
            "--parameters", "LambdaArtifactKey=$artifactKey",
            "--parameters", "LambdaArtifactObjectVersion=$objectVersion",
            "-c", "environmentName=dev",
            "-c", "enableCodeDeployCanary=$($canaryEnabled.ToString().ToLowerInvariant())"
        )
        if (-not [string]::IsNullOrWhiteSpace($AlertEmail)) {
            $deployArgs += @("-c", "alertEmail=$AlertEmail")
        }
        if ($configureSlackWebhook) {
            $deployArgs += @("--parameters", "SlackWebhookSecretArn=$SlackWebhookSecretArn")
        }
        $deployArgs += @("--parameters", "ServiceRoleBoundaryArn=$ServiceRoleBoundaryArn")
        if ($bookingCatalogJson) {
            $deployArgs += @("--parameters", "BookingCatalogJson=$bookingCatalogJson")
        }
        & cdk @deployArgs
    } -FailureMessage "CDK deploy failed."
}
finally {
    Pop-Location
}

$apiEndpoint = aws cloudformation describe-stacks --stack-name $stackName --query "Stacks[0].Outputs[?OutputKey=='ApiEndpoint'].OutputValue | [0]" --output text
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($apiEndpoint)) {
    throw "Failed to fetch the API endpoint from CloudFormation outputs."
}
Write-Host "Deployment completed."
Write-Output $apiEndpoint
