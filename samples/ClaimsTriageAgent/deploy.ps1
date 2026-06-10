# ═══════════════════════════════════════════════════════════════════════════
# ClaimPilot — build + deploy to AWS Lambda (NativeAOT, arm64 Graviton2)
#
# Prerequisites:
#   - Docker Desktop for Windows (with Linux containers enabled)
#   - AWS CLI configured with credentials
#   - $env:LAMBDA_ROLE_ARN — an execution role with:
#       bedrock:InvokeModel + bedrock:InvokeModelWithResponseStream
#       logs:CreateLogGroup, logs:CreateLogStream, logs:PutLogEvents
#
# Model provider selection (set MODEL_PROVIDER env var):
#   bedrock   — Amazon Bedrock via Converse API (default, uses IAM credentials)
#   anthropic — Anthropic direct API (set ANTHROPIC_API_KEY)
#   openai    — OpenAI or compatible endpoint (set OPENAI_API_KEY, optionally OPENAI_BASE_URL)
#   gemini    — Google Gemini (set GEMINI_API_KEY)
#
# Usage:
#   $env:LAMBDA_ROLE_ARN = "arn:aws:iam::YOUR_ACCOUNT:role/YOUR_LAMBDA_ROLE"
#   .\deploy.ps1                 # create or update the function (default: bedrock)
#   .\deploy.ps1 invoke          # send events/claim-fasttrack.json to the function
#
# To deploy with a different model provider:
#   $env:MODEL_PROVIDER = "anthropic"; $env:ANTHROPIC_API_KEY = "sk-ant-..."; .\deploy.ps1
#   $env:MODEL_PROVIDER = "openai"; $env:OPENAI_API_KEY = "sk-..."; .\deploy.ps1
#   $env:MODEL_PROVIDER = "gemini"; $env:GEMINI_API_KEY = "AI..."; .\deploy.ps1
# ═══════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = "Stop"

$FunctionName = if ($env:FUNCTION_NAME) { $env:FUNCTION_NAME } else { "claimpilot-triage" }
$Region = if ($env:AWS_REGION) { $env:AWS_REGION } else { "us-east-1" }
$Memory = 1024
$Timeout = 60
$Arch = "linux-arm64"
$PublishDir = "publish-arm64"
$ModelProvider = if ($env:MODEL_PROVIDER) { $env:MODEL_PROVIDER } else { "bedrock" }

# ── Model provider environment variables ──────────────────────────────────────

function Build-EnvVars {
    $vars = "MODEL_PROVIDER=$ModelProvider"

    switch ($ModelProvider) {
        "bedrock" {
            $modelId = if ($env:BEDROCK_MODEL_ID) { $env:BEDROCK_MODEL_ID } else { "us.anthropic.claude-haiku-4-5-20251001-v1:0" }
            $vars += ",BEDROCK_MODEL_ID=$modelId"
        }
        "anthropic" {
            if (-not $env:ANTHROPIC_API_KEY) { throw "ANTHROPIC_API_KEY is required when MODEL_PROVIDER=anthropic" }
            $modelId = if ($env:ANTHROPIC_MODEL_ID) { $env:ANTHROPIC_MODEL_ID } else { "claude-haiku-4-5-20241022" }
            $vars += ",ANTHROPIC_API_KEY=$($env:ANTHROPIC_API_KEY),ANTHROPIC_MODEL_ID=$modelId"
        }
        "openai" {
            if (-not $env:OPENAI_API_KEY) { throw "OPENAI_API_KEY is required when MODEL_PROVIDER=openai" }
            $baseUrl = if ($env:OPENAI_BASE_URL) { $env:OPENAI_BASE_URL } else { "https://api.openai.com/v1" }
            $modelId = if ($env:OPENAI_MODEL_ID) { $env:OPENAI_MODEL_ID } else { "gpt-4o" }
            $vars += ",OPENAI_API_KEY=$($env:OPENAI_API_KEY),OPENAI_BASE_URL=$baseUrl,OPENAI_MODEL_ID=$modelId"
        }
        "gemini" {
            if (-not $env:GEMINI_API_KEY) { throw "GEMINI_API_KEY is required when MODEL_PROVIDER=gemini" }
            $modelId = if ($env:GEMINI_MODEL_ID) { $env:GEMINI_MODEL_ID } else { "gemini-2.0-flash" }
            $vars += ",GEMINI_API_KEY=$($env:GEMINI_API_KEY),GEMINI_MODEL_ID=$modelId"
        }
        default {
            throw "Unknown MODEL_PROVIDER '$ModelProvider'. Valid values: bedrock, anthropic, openai, gemini"
        }
    }

    return $vars
}

# ── Navigate to script directory ──────────────────────────────────────────────
Push-Location $PSScriptRoot

try {
    # ── Invoke mode ───────────────────────────────────────────────────────────
    if ($args[0] -eq "invoke") {
        aws lambda invoke `
            --function-name $FunctionName `
            --payload "file://events/claim-fasttrack.json" `
            --cli-binary-format raw-in-base64-out `
            --region $Region `
            response.json
        Get-Content response.json
        Remove-Item response.json -ErrorAction SilentlyContinue
        exit 0
    }

    # ── Build NativeAOT binary in Docker ──────────────────────────────────────
    Write-Host "── Building NativeAOT binary ($Arch) in Docker ─────────────────────────"

    # Convert Windows path to Docker-compatible format
    $srcPath = (Get-Location).Path -replace '\\', '/'

    docker run --rm -v "${srcPath}:/src" -w /src `
        mcr.microsoft.com/dotnet/sdk:10.0 `
        bash -c "apt-get update -qq && apt-get install -y -qq clang zlib1g-dev && dotnet publish ClaimsTriageAgent.csproj --configuration Release --runtime $Arch --output $PublishDir"

    if ($LASTEXITCODE -ne 0) { throw "Docker build failed" }

    # ── Package ───────────────────────────────────────────────────────────────
    Write-Host "── Packaging (custom runtime requires the binary to be named 'bootstrap')"
    Copy-Item "$PublishDir/ClaimsTriageAgent" "$PublishDir/bootstrap" -Force

    # Create zip using .NET (no external zip tool dependency)
    $zipPath = "$PublishDir/function.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        "$((Get-Location).Path)\$PublishDir",
        "$((Get-Location).Path)\$zipPath",
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    # Actually we only want bootstrap in the zip, so build it properly
    Remove-Item $zipPath -ErrorAction SilentlyContinue
    $tempZipDir = "$PublishDir/zip-staging"
    New-Item -ItemType Directory -Path $tempZipDir -Force | Out-Null
    Copy-Item "$PublishDir/bootstrap" "$tempZipDir/bootstrap" -Force
    Compress-Archive -Path "$tempZipDir/bootstrap" -DestinationPath $zipPath -Force
    Remove-Item $tempZipDir -Recurse -Force

    $envVars = Build-EnvVars
    Write-Host "── Model provider: $ModelProvider ──────────────────────────────────────"

    # ── Deploy ────────────────────────────────────────────────────────────────
    $exists = $null
    try { $exists = aws lambda get-function --function-name $FunctionName --region $Region 2>$null } catch {}

    if ($exists) {
        Write-Host "── Updating existing function $FunctionName ──────────────────────────"
        aws lambda update-function-code `
            --function-name $FunctionName `
            --zip-file "fileb://$PublishDir/function.zip" `
            --region $Region

        # Wait for code update to stabilize
        aws lambda wait function-updated --function-name $FunctionName --region $Region 2>$null

        aws lambda update-function-configuration `
            --function-name $FunctionName `
            --environment "Variables={$envVars}" `
            --region $Region
    }
    else {
        Write-Host "── Creating function $FunctionName ───────────────────────────────────"
        if (-not $env:LAMBDA_ROLE_ARN) { throw "Set LAMBDA_ROLE_ARN to your Lambda execution role ARN" }

        aws lambda create-function `
            --function-name $FunctionName `
            --runtime provided.al2023 `
            --handler bootstrap `
            --architectures arm64 `
            --role $env:LAMBDA_ROLE_ARN `
            --zip-file "fileb://$PublishDir/function.zip" `
            --memory-size $Memory `
            --timeout $Timeout `
            --environment "Variables={$envVars}" `
            --region $Region
    }

    Write-Host "Done. Try: .\deploy.ps1 invoke"
}
finally {
    Pop-Location
}
