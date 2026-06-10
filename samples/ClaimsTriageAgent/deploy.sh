#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# ClaimPilot — build + deploy to AWS Lambda (NativeAOT, arm64 Graviton2)
#
# Prerequisites:
#   - Docker (NativeAOT needs a Linux linker; the build runs in a container)
#   - AWS CLI configured with credentials
#   - $LAMBDA_ROLE_ARN — an execution role with:
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
#   export LAMBDA_ROLE_ARN=arn:aws:iam::YOUR_ACCOUNT:role/YOUR_LAMBDA_ROLE
#   ./deploy.sh                 # create or update the function (default: bedrock)
#   ./deploy.sh invoke          # send events/claim-fasttrack.json to the function
#
# To deploy with a different model provider:
#   MODEL_PROVIDER=anthropic ANTHROPIC_API_KEY=sk-ant-... ./deploy.sh
#   MODEL_PROVIDER=openai OPENAI_API_KEY=sk-... ./deploy.sh
#   MODEL_PROVIDER=gemini GEMINI_API_KEY=AI... ./deploy.sh
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

FUNCTION_NAME="${FUNCTION_NAME:-claimpilot-triage}"
REGION="${AWS_REGION:-us-east-1}"
MEMORY=1024   # measured sweet spot for Jacquard AOT agents (see AotLambda sample)
TIMEOUT=60
ARCH=linux-arm64
PUBLISH_DIR=publish-arm64

# ── Model provider configuration ──────────────────────────────────────────────
MODEL_PROVIDER="${MODEL_PROVIDER:-bedrock}"

build_env_vars() {
    local vars="MODEL_PROVIDER=$MODEL_PROVIDER"

    case "$MODEL_PROVIDER" in
        bedrock)
            vars="$vars,BEDROCK_MODEL_ID=${BEDROCK_MODEL_ID:-us.anthropic.claude-haiku-4-5-20251001-v1:0}"
            ;;
        anthropic)
            : "${ANTHROPIC_API_KEY:?ANTHROPIC_API_KEY is required when MODEL_PROVIDER=anthropic}"
            vars="$vars,ANTHROPIC_API_KEY=$ANTHROPIC_API_KEY"
            vars="$vars,ANTHROPIC_MODEL_ID=${ANTHROPIC_MODEL_ID:-claude-haiku-4-5-20241022}"
            ;;
        openai)
            : "${OPENAI_API_KEY:?OPENAI_API_KEY is required when MODEL_PROVIDER=openai}"
            vars="$vars,OPENAI_API_KEY=$OPENAI_API_KEY"
            vars="$vars,OPENAI_BASE_URL=${OPENAI_BASE_URL:-https://api.openai.com/v1}"
            vars="$vars,OPENAI_MODEL_ID=${OPENAI_MODEL_ID:-gpt-4o}"
            ;;
        gemini)
            : "${GEMINI_API_KEY:?GEMINI_API_KEY is required when MODEL_PROVIDER=gemini}"
            vars="$vars,GEMINI_API_KEY=$GEMINI_API_KEY"
            vars="$vars,GEMINI_MODEL_ID=${GEMINI_MODEL_ID:-gemini-2.0-flash}"
            ;;
        *)
            echo "ERROR: Unknown MODEL_PROVIDER '$MODEL_PROVIDER'. Valid: bedrock, anthropic, openai, gemini"
            exit 1
            ;;
    esac

    echo "$vars"
}

cd "$(dirname "$0")"

if [[ "${1:-}" == "invoke" ]]; then
  aws lambda invoke \
    --function-name "$FUNCTION_NAME" \
    --payload file://events/claim-fasttrack.json \
    --cli-binary-format raw-in-base64-out \
    --region "$REGION" \
    /dev/stdout
  exit 0
fi

echo "── Building NativeAOT binary ($ARCH) in Docker ─────────────────────────"
docker run --rm -v "$(pwd)":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c "apt-get update -qq && apt-get install -y -qq clang zlib1g-dev && \
    dotnet publish ClaimsTriageAgent.csproj \
      --configuration Release \
      --runtime $ARCH \
      --output $PUBLISH_DIR"

echo "── Packaging (custom runtime requires the binary to be named 'bootstrap')"
cp "$PUBLISH_DIR/ClaimsTriageAgent" "$PUBLISH_DIR/bootstrap"
(cd "$PUBLISH_DIR" && zip -qj function.zip bootstrap)

ENV_VARS=$(build_env_vars)
echo "── Model provider: $MODEL_PROVIDER ──────────────────────────────────────"

if aws lambda get-function --function-name "$FUNCTION_NAME" --region "$REGION" >/dev/null 2>&1; then
  echo "── Updating existing function $FUNCTION_NAME ──────────────────────────"
  aws lambda update-function-code \
    --function-name "$FUNCTION_NAME" \
    --zip-file "fileb://$PUBLISH_DIR/function.zip" \
    --region "$REGION"

  # Wait for code update to stabilize before updating config
  aws lambda wait function-updated --function-name "$FUNCTION_NAME" --region "$REGION" 2>/dev/null || true

  aws lambda update-function-configuration \
    --function-name "$FUNCTION_NAME" \
    --environment "Variables={$ENV_VARS}" \
    --region "$REGION"
else
  echo "── Creating function $FUNCTION_NAME ───────────────────────────────────"
  : "${LAMBDA_ROLE_ARN:?Set LAMBDA_ROLE_ARN to your Lambda execution role ARN}"
  aws lambda create-function \
    --function-name "$FUNCTION_NAME" \
    --runtime provided.al2023 \
    --handler bootstrap \
    --architectures arm64 \
    --role "$LAMBDA_ROLE_ARN" \
    --zip-file "fileb://$PUBLISH_DIR/function.zip" \
    --memory-size "$MEMORY" \
    --timeout "$TIMEOUT" \
    --environment "Variables={$ENV_VARS}" \
    --region "$REGION"
fi

echo "Done. Try: ./deploy.sh invoke"
