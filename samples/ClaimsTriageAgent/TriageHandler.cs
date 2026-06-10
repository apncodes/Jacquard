using System.Diagnostics;
using System.Text.Json;
using ClaimsTriageAgent.Models;
using ClaimsTriageAgent.Tools;
using Jacquard.Core;
using Jacquard.Models.Bedrock;

namespace ClaimsTriageAgent;

/// <summary>
/// Core triage orchestration, shared by the Lambda handler and the local CLI
/// runner. Builds the Jacquard agent, registers observability hooks, runs the
/// event loop, and parses the model's JSON decision into the typed contract.
/// </summary>
public static class TriageHandler
{
    private const string SystemPrompt = """
        You are ClaimPilot, the first-notice-of-loss triage agent for an auto-insurance carrier.
        You triage exactly one claim per invocation. You are read-only: you never approve,
        deny, or pay a claim yourself — you route it with a documented recommendation.

        PROCESS — follow in order:
        1. Call LookupPolicy and GetClaimHistory for the claim's policy number.
        2. Call CheckFraudSignals (pass the claimant's incident description VERBATIM) and
           EstimateRepairCost.
        3. Apply the DECISION RULES below, in priority order, and emit your decision.

        DECISION RULES (first match wins):
        R1. Policy not found, OR status is not "Active", OR the incident date is outside the
            policy's effective-to-expiration period  -> decision COVERAGE_REVIEW
        R2. Fraud score >= 0.65 OR watchlistHit is true -> decision SIU_REFERRAL
        R3. Estimate severity is MINOR AND highEstimate <= 5000 AND fraud score < 0.30
            AND claimsLast12Months <= 1 -> decision FAST_TRACK
        R4. Otherwise -> decision ADJUSTER_REVIEW

        OUTPUT CONTRACT — your final reply must be ONLY a raw JSON object, no markdown fences,
        no commentary, starting with { and ending with }:
        {
          "decision": "FAST_TRACK | ADJUSTER_REVIEW | SIU_REFERRAL | COVERAGE_REVIEW",
          "severity": "MINOR | MODERATE | SEVERE | TOTAL_LOSS  (use the estimate's severity)",
          "fraudRiskScore": <number from the fraud report>,
          "estimatedRepairCost": <midpoint of the low and high estimates>,
          "reasoning": "2-3 sentences citing the specific rule and the tool data that triggered it",
          "nextSteps": ["3 concrete operational next steps for the claims team"]
        }
        """;

    public static async Task<TriageResult> HandleAsync(
        ClaimEvent claim, Action<string> log, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var (mode, model, modelId) = CreateModel();

        log($"[ClaimPilot] Triaging claim {claim.ClaimId} (policy {claim.PolicyNumber}, " +
            $"loss type '{claim.LossType}') — model: {mode} ({modelId})");

        // ── Observability hooks ──────────────────────────────────────────────
        // BeforeToolCall: audit trail of every LOB-system access (name + args).
        // AfterToolCall:  result size and error flag.
        // AfterModelCall: token accounting across all event-loop iterations.
        var usage = TokenUsage.Zero;
        var toolsUsed = new List<string>();
        var hooks = new HookRegistry();

        hooks.Register<BeforeToolCallEvent>(e =>
        {
            toolsUsed.Add(e.Call.Name);
            log($"[audit] tool call -> {e.Call.Name}({e.Call.Input.GetRawText()})");
            return Task.CompletedTask;
        });

        hooks.Register<AfterToolCallEvent>(e =>
        {
            log($"[audit] tool done -> {e.Call.Name} " +
                $"({e.Result.Content.Length} chars{(e.Result.IsError ? ", ERROR" : "")})");
            return Task.CompletedTask;
        });

        hooks.Register<AfterModelCallEvent>(e =>
        {
            usage += e.Response.Usage;
            return Task.CompletedTask;
        });

        var agent = new Agent(
            model: model,
            systemPrompt: SystemPrompt,
            toolProviders: [new PolicyTools(), new FraudTools(), new RepairTools()],
            hooks: hooks,
            config: new AgentConfig { MaxIterations = 8 });

        var result = await agent.InvokeAsync(BuildPrompt(claim), ct);
        log($"[ClaimPilot] Event loop finished: {result.StopReason}, " +
            $"{usage.Total} tokens, {toolsUsed.Count} tool calls");

        var decision = ParseDecision(result.Message) ?? new TriageDecision
        {
            Decision = "ADJUSTER_REVIEW",
            Severity = "MODERATE",
            Reasoning = "The model response could not be parsed into a triage decision; " +
                        "claim defaulted to manual adjuster review (fail-safe).",
            NextSteps = ["Assign to the adjuster queue for full manual triage."],
        };

        stopwatch.Stop();
        return new TriageResult
        {
            ClaimId = claim.ClaimId,
            PolicyNumber = claim.PolicyNumber,
            ProcessedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            Triage = decision,
            Telemetry = new RunTelemetry
            {
                ModelMode = mode,
                ModelId = modelId,
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                ToolCallCount = toolsUsed.Count,
                ToolsUsed = [.. toolsUsed],
                DurationMs = stopwatch.ElapsedMilliseconds,
            },
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // MODEL PROVIDER SELECTION
    //
    // Set the MODEL_PROVIDER environment variable to choose a provider:
    //   bedrock   — Amazon Bedrock (default, uses AWS credentials)
    //   anthropic — Anthropic direct API (requires ANTHROPIC_API_KEY)
    //   openai    — OpenAI or any compatible endpoint (requires OPENAI_API_KEY)
    //   gemini    — Google Gemini (requires GEMINI_API_KEY)
    //
    // Each provider reads its model ID from the corresponding env var:
    //   BEDROCK_MODEL_ID    (default: us.anthropic.claude-haiku-4-5-20251001-v1:0)
    //   ANTHROPIC_MODEL_ID  (default: claude-haiku-4-5-20241022)
    //   OPENAI_MODEL_ID     (default: gpt-4o)
    //   GEMINI_MODEL_ID     (default: gemini-2.0-flash)
    //
    // For OpenAI-compatible endpoints, also set OPENAI_BASE_URL (default: https://api.openai.com/v1)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (string Mode, IModel Model, string ModelId) CreateModel()
    {
        var provider = Environment.GetEnvironmentVariable("MODEL_PROVIDER")?.ToLowerInvariant() ?? "bedrock";

        return provider switch
        {
            "bedrock" => CreateBedrockModel(),
            "anthropic" => CreateAnthropicModel(),
            "openai" => CreateOpenAIModel(),
            "gemini" => CreateGeminiModel(),
            _ => throw new InvalidOperationException(
                $"Unknown MODEL_PROVIDER '{provider}'. Valid values: bedrock, anthropic, openai, gemini")
        };
    }

    private static (string, IModel, string) CreateBedrockModel()
    {
        var modelId = Environment.GetEnvironmentVariable("BEDROCK_MODEL_ID")
                      ?? "us.anthropic.claude-haiku-4-5-20251001-v1:0";
        var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1";
        return ("bedrock", new BedrockModel(region: region, modelId: modelId), modelId);
    }

    private static (string, IModel, string) CreateAnthropicModel()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                     ?? throw new InvalidOperationException(
                         "ANTHROPIC_API_KEY environment variable is required when MODEL_PROVIDER=anthropic");
        var modelId = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL_ID")
                      ?? "claude-haiku-4-5-20241022";
        return ("anthropic", new AnthropicModel(apiKey, modelId), modelId);
    }

    private static (string, IModel, string) CreateOpenAIModel()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                     ?? throw new InvalidOperationException(
                         "OPENAI_API_KEY environment variable is required when MODEL_PROVIDER=openai");
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
                      ?? "https://api.openai.com/v1";
        var modelId = Environment.GetEnvironmentVariable("OPENAI_MODEL_ID") ?? "gpt-4o";
        return ("openai", new OpenAICompatibleModel(baseUrl, apiKey, modelId), modelId);
    }

    private static (string, IModel, string) CreateGeminiModel()
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                     ?? throw new InvalidOperationException(
                         "GEMINI_API_KEY environment variable is required when MODEL_PROVIDER=gemini");
        var modelId = Environment.GetEnvironmentVariable("GEMINI_MODEL_ID")
                      ?? "gemini-2.0-flash";
        return ("gemini", new GeminiModel(apiKey, modelId), modelId);
    }

    private static string BuildPrompt(ClaimEvent claim) => $"""
        Triage this first-notice-of-loss claim:

        Claim ID:        {claim.ClaimId}
        Policy number:   {claim.PolicyNumber}
        Claimant:        {claim.ClaimantName}
        Vehicle:         {claim.Vehicle}
        Loss type:       {claim.LossType}
        Incident date:   {claim.IncidentDate}
        Reported date:   {claim.ReportedDate}
        Location:        {claim.Location}
        Claimed amount:  ${claim.ClaimedAmount:0}

        Incident description (verbatim from claimant):
        {claim.IncidentDescription}
        """;

    /// <summary>
    /// AOT-safe decision parsing: strip any markdown fences, find the outermost
    /// JSON object, deserialize with the source-generated context. (The SDK's
    /// GetStructuredOutputAsync uses reflection-based schema generation, which
    /// is not NativeAOT-safe — so this Lambda parses the prompted JSON itself,
    /// the same pattern as the SDK's DurableWorkflow sample.)
    /// </summary>
    private static TriageDecision? ParseDecision(string text)
    {
        var stripped = text;
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var contentStart = text.IndexOf('\n', fenceStart);
            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (contentStart >= 0 && fenceEnd > contentStart)
                stripped = text[(contentStart + 1)..fenceEnd].Trim();
        }

        var start = stripped.IndexOf('{');
        var end = stripped.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            return JsonSerializer.Deserialize(
                stripped[start..(end + 1)], ClaimsJsonContext.Default.TriageDecision);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
