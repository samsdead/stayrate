param(
    [string]$Profile = "",
    [string]$Region = "eu-west-2",
    [string]$EnableCodeDeployCanary = "true",
    [string]$AlertEmail = "",
    [string]$SlackWebhookUrl = ""
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

$tempDir = [System.IO.Path]::GetTempPath()
if ([string]::IsNullOrWhiteSpace($EnableCodeDeployCanary)) {
    $EnableCodeDeployCanary = "true"
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

$accountFile = Join-Path $tempDir "stayrate-account.txt"
Invoke-Checked -Command { aws sts get-caller-identity --query Account --output text | Out-File -FilePath $accountFile -Encoding ascii } -FailureMessage "Failed to resolve AWS account id."
$accountId = (Get-Content $accountFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($accountId)) {
    throw "Could not resolve AWS account id."
}

$artifactBucket = "stayrate-prod-artifacts-$accountId-$Region"
$artifactPrefix = "lambda"
$timestamp = Get-Date -Format "yyyyMMddHHmmss"
$artifactKey = "$artifactPrefix/stayrate-api-$timestamp.zip"
$stackName = "StayRateProdStack"

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

Invoke-Checked -Command { aws s3 cp $artifactPath "s3://$artifactBucket/$artifactKey" | Out-Null } -FailureMessage "Failed to upload Lambda artifact to S3."

$objectVersionFile = Join-Path $tempDir "stayrate-object-version.txt"
Invoke-Checked -Command {
    aws s3api head-object --bucket $artifactBucket --key $artifactKey --query VersionId --output text | Out-File -FilePath $objectVersionFile -Encoding ascii
} -FailureMessage "Failed to resolve S3 object version for Lambda artifact."
$objectVersion = (Get-Content $objectVersionFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($objectVersion) -or $objectVersion -eq "None") {
    throw "S3 object version was not returned for Lambda artifact."
}

Push-Location "infra"
try {
    Invoke-Checked -Command { cdk bootstrap "aws://$accountId/$Region" | Out-Null } -FailureMessage "CDK bootstrap failed."
    Invoke-Checked -Command {
        $deployArgs = @(
            "deploy", $stackName,
            "--require-approval", "never",
            "--parameters", "LambdaArtifactBucket=$artifactBucket",
            "--parameters", "LambdaArtifactKey=$artifactKey",
            "--parameters", "LambdaArtifactObjectVersion=$objectVersion",
            "-c", "environmentName=prod",
            "-c", "enableCodeDeployCanary=$($canaryEnabled.ToString().ToLowerInvariant())"
        )
        if (-not [string]::IsNullOrWhiteSpace($AlertEmail)) {
            $deployArgs += @("-c", "alertEmail=$AlertEmail")
        }
        if (-not [string]::IsNullOrWhiteSpace($SlackWebhookUrl)) {
            $deployArgs += @("-c", "slackWebhookUrl=$SlackWebhookUrl")
        }
        & cdk @deployArgs
    } -FailureMessage "CDK deploy failed."
}
finally {
    Pop-Location
}

Invoke-Checked -Command {
    aws cloudformation describe-stacks --stack-name $stackName --query "Stacks[0].Outputs[?OutputKey=='ApiEndpoint'].OutputValue" --output text | Out-File -FilePath (Join-Path $tempDir "stayrate-endpoint.txt") -Encoding ascii
} -FailureMessage "Failed to fetch API endpoint from CloudFormation outputs."
$apiEndpoint = (Get-Content (Join-Path $tempDir "stayrate-endpoint.txt") -Raw).Trim()

Write-Host "DEPLOY_COMPLETE"
Write-Host "Account: $accountId"
Write-Host "Region: $Region"
Write-Host "Bucket: $artifactBucket"
Write-Host "ArtifactKey: $artifactKey"
Write-Host "ArtifactObjectVersion: $objectVersion"
Write-Host "ApiEndpoint: $apiEndpoint"
Write-Host "API_ENDPOINT=$apiEndpoint"

if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "api_endpoint=$apiEndpoint"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "artifact_bucket=$artifactBucket"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "artifact_key=$artifactKey"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "artifact_object_version=$objectVersion"
}
