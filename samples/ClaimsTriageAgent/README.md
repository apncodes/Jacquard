# ClaimPilot — FNOL Claims-Triage Agent on AWS Lambda

An AI agent built with [Jacquard.NET](https://github.com/apncodes/Jacquard.NET) that triages
**first-notice-of-loss (FNOL) auto-insurance claims**, published as a **NativeAOT** binary for
the AWS Lambda `provided.al2023` custom runtime.

## The scenario

When a policyholder reports a loss, a carrier has minutes — not days — to make the first
routing decision: pay it fast, send an adjuster, investigate it, or question coverage.
That decision needs four line-of-business lookups per claim:

1. **Policy administration system** — is the policy active, what's covered, what's the deductible?
2. **Claims history store** — how often has this policy claimed before?
3. **Third-party fraud analytics** — score the narrative and the pattern (FRISS / Shift
   Technology play this role in real carriers).
4. **Collision estimating service** — how bad is the damage (CCC ONE / Mitchell in real life)?

ClaimPilot is the agent that does this. For each claim it calls all four systems, applies the
carrier's documented triage rules, and returns one structured decision:

| Decision | Meaning |
|---|---|
| `FAST_TRACK` | Straight-through processing — pay and close, no human touch |
| `ADJUSTER_REVIEW` | Real damage, real coverage — route to a licensed adjuster |
| `SIU_REFERRAL` | Fraud indicators — Special Investigations Unit before any payment |
| `COVERAGE_REVIEW` | Policy lapsed / not found / loss outside the coverage period |

## Model providers

The agent supports **four model providers**, selected via the `MODEL_PROVIDER` environment variable:

| Provider | Env Var | API Key Env Var | Default Model |
|---|---|---|---|
| `bedrock` (default) | `BEDROCK_MODEL_ID` | _(uses IAM credentials)_ | `us.anthropic.claude-haiku-4-5-20251001-v1:0` |
| `anthropic` | `ANTHROPIC_MODEL_ID` | `ANTHROPIC_API_KEY` | `claude-haiku-4-5-20241022` |
| `openai` | `OPENAI_MODEL_ID` | `OPENAI_API_KEY` | `gpt-4o` |
| `gemini` | `GEMINI_MODEL_ID` | `GEMINI_API_KEY` | `gemini-2.0-flash` |

For OpenAI-compatible endpoints (Azure OpenAI, Ollama, LM Studio), also set `OPENAI_BASE_URL`.

## Architecture

```
 FNOL intake (API Gateway / SQS / EventBridge)
        │  ClaimEvent JSON
        ▼
┌──────────────────────────────────────────────────────────────┐
│  AWS Lambda — ClaimPilot (NativeAOT, provided.al2023, arm64) │
│                                                              │
│   Jacquard Agent ── event loop ── LLM (configurable)         │
│        │                                                     │
│        ├─ LookupPolicy ───────► PolicyAdminSystem (mock)     │
│        ├─ GetClaimHistory ────► PolicyAdminSystem (mock)     │
│        ├─ CheckFraudSignals ──► FraudScoringService (mock)   │
│        └─ EstimateRepairCost ─► RepairEstimator (mock)       │
│                                                              │
│   Hooks: tool-call audit log · token accounting              │
└──────────────────────────────────────────────────────────────┘
        │  TriageResult JSON (decision + reasoning + telemetry)
        ▼
 Claims workflow (adjuster queues, payment engine, SIU case mgmt)
```

## Run it locally

### Prerequisites

- .NET 10 SDK
- Docker (for the deploy scripts — not needed for local runs)
- AWS CLI configured with credentials (for Bedrock or Lambda deployment)

### With Bedrock (default — requires AWS credentials + model access)

```bash
cd samples/ClaimsTriageAgent
dotnet run -- --local events/claim-fasttrack.json   # → FAST_TRACK
dotnet run -- --local events/claim-siu.json         # → SIU_REFERRAL
dotnet run -- --local events/claim-lapsed.json      # → COVERAGE_REVIEW
dotnet run -- --local events/claim-adjuster.json    # → ADJUSTER_REVIEW
```

> **Note:** The expected decisions above reflect the business rules. Since the agent uses a real
> LLM, occasional non-determinism is possible — the fail-safe defaults to `ADJUSTER_REVIEW` if
> the model's JSON output can't be parsed.

### With Anthropic

**macOS / Linux:**
```bash
ANTHROPIC_API_KEY=sk-ant-... MODEL_PROVIDER=anthropic \
  dotnet run -- --local events/claim-fasttrack.json
```

**Windows (PowerShell):**
```powershell
$env:MODEL_PROVIDER = "anthropic"; $env:ANTHROPIC_API_KEY = "sk-ant-..."
dotnet run -- --local events/claim-fasttrack.json
```

### With OpenAI

**macOS / Linux:**
```bash
OPENAI_API_KEY=sk-... MODEL_PROVIDER=openai \
  dotnet run -- --local events/claim-fasttrack.json
```

**Windows (PowerShell):**
```powershell
$env:MODEL_PROVIDER = "openai"; $env:OPENAI_API_KEY = "sk-..."
dotnet run -- --local events/claim-fasttrack.json
```

### With Gemini

**macOS / Linux:**
```bash
GEMINI_API_KEY=AI... MODEL_PROVIDER=gemini \
  dotnet run -- --local events/claim-fasttrack.json
```

**Windows (PowerShell):**
```powershell
$env:MODEL_PROVIDER = "gemini"; $env:GEMINI_API_KEY = "AI..."
dotnet run -- --local events/claim-fasttrack.json
```

## Deploy to AWS Lambda

### macOS / Linux

```bash
export LAMBDA_ROLE_ARN=arn:aws:iam::YOUR_ACCOUNT:role/YOUR_LAMBDA_ROLE

# Deploy with Bedrock (default)
./deploy.sh

# Deploy with Anthropic
MODEL_PROVIDER=anthropic ANTHROPIC_API_KEY=sk-ant-... ./deploy.sh

# Deploy with OpenAI
MODEL_PROVIDER=openai OPENAI_API_KEY=sk-... ./deploy.sh

# Deploy with Gemini
MODEL_PROVIDER=gemini GEMINI_API_KEY=AI... ./deploy.sh

# Invoke the deployed function
./deploy.sh invoke
```

### Windows (PowerShell)

```powershell
$env:LAMBDA_ROLE_ARN = "arn:aws:iam::YOUR_ACCOUNT:role/YOUR_LAMBDA_ROLE"

# Deploy with Bedrock (default)
.\deploy.ps1

# Deploy with Anthropic
$env:MODEL_PROVIDER = "anthropic"; $env:ANTHROPIC_API_KEY = "sk-ant-..."; .\deploy.ps1

# Deploy with OpenAI
$env:MODEL_PROVIDER = "openai"; $env:OPENAI_API_KEY = "sk-..."; .\deploy.ps1

# Deploy with Gemini
$env:MODEL_PROVIDER = "gemini"; $env:GEMINI_API_KEY = "AI..."; .\deploy.ps1

# Invoke the deployed function
.\deploy.ps1 invoke
```

The function uses the configured model via the Lambda environment variables. Memory is set to
1024 MB — the measured cold-start sweet spot for Jacquard AOT agents on Graviton2.

The Lambda execution role needs `bedrock:InvokeModel` / `bedrock:InvokeModelWithResponseStream`
(only when using Bedrock) plus CloudWatch Logs permissions.

## Decision rules (the business policy)

Encoded in the system prompt, evaluated in priority order:

| # | Rule | Decision |
|---|---|---|
| R1 | Policy not found / not Active / loss date outside coverage period | `COVERAGE_REVIEW` |
| R2 | Fraud score ≥ 0.65 or consortium watchlist hit | `SIU_REFERRAL` |
| R3 | MINOR severity ∧ high estimate ≤ $5,000 ∧ fraud < 0.30 ∧ ≤ 1 claim in 12 months | `FAST_TRACK` |
| R4 | Everything else | `ADJUSTER_REVIEW` |

## Jacquard.NET features used

| Feature | Where |
|---|---|
| `[Tool]` + Roslyn source generator (zero-reflection tool dispatch) | `Tools/*.cs` |
| `[ToolParameterValidation]` (regex pattern, required, min-length) | `Tools/*.cs` |
| Model-driven event loop with parallel tool execution | `TriageHandler.cs` |
| Hooks: `BeforeToolCallEvent` / `AfterToolCallEvent` audit trail, `AfterModelCallEvent` token accounting | `TriageHandler.cs` |
| `BedrockModel` / `AnthropicModel` / `OpenAICompatibleModel` / `GeminiModel` (all four supported) | `TriageHandler.cs` |
| NativeAOT publish + `SourceGeneratorLambdaJsonSerializer` | `Function.cs`, `.csproj` |

## Project layout

```
ClaimsTriageAgent/
├── Function.cs                  Lambda bootstrap (NativeAOT) + local CLI runner
├── TriageHandler.cs             Agent wiring: system prompt, model selection, hooks, decision parsing
├── Models/ClaimContracts.cs     ClaimEvent/TriageResult contracts + AOT JSON context
├── Tools/PolicyTools.cs         [Tool] LookupPolicy, GetClaimHistory
├── Tools/FraudTools.cs          [Tool] CheckFraudSignals
├── Tools/RepairTools.cs         [Tool] EstimateRepairCost
├── Services/                    Mock LOB systems (policy admin, fraud API, estimator)
├── events/                      Sample FNOL payloads (one per decision path)
├── deploy.sh                    macOS/Linux: Docker arm64 AOT build + Lambda create/update/invoke
└── deploy.ps1                   Windows: PowerShell equivalent of deploy.sh
```

## Taking it to production

- Put SQS between intake and the Lambda for buffering and per-claim retry; the handler is
  already stateless and idempotent-friendly (decisions are derived, never stored in-process).
- Replace the `Services/` mocks with typed HTTP clients for your policy admin system,
  fraud vendor, and estimating vendor — the tool classes and agent code don't change.
- Add a Bedrock Guardrail (`BedrockGuardrailConfig` on `BedrockModel`) to screen claimant
  narratives, and wire the `GuardrailViolationEvent` hook into your audit pipeline.
- A `BeforeToolCallEvent` hook with `e.Interrupt = true` gives you human-in-the-loop
  approval if you later add any non-read-only tool (e.g. auto-issuing fast-track payments).
