using System.Text.Json.Serialization;

namespace ClaimsTriageAgent.Models;

// ── Lambda input ──────────────────────────────────────────────────────────────

/// <summary>
/// First Notice of Loss (FNOL) event — the payload the claims intake channel
/// (API Gateway, SQS, EventBridge) delivers to the triage Lambda.
/// </summary>
public class ClaimEvent
{
    public string ClaimId { get; set; } = "";
    public string PolicyNumber { get; set; } = "";
    public string ClaimantName { get; set; } = "";
    /// <summary>ISO-8601 date the loss occurred.</summary>
    public string IncidentDate { get; set; } = "";
    /// <summary>ISO-8601 date the claim was reported.</summary>
    public string ReportedDate { get; set; } = "";
    /// <summary>collision | theft | hail | vandalism | flood | fire | glass</summary>
    public string LossType { get; set; } = "";
    public string IncidentDescription { get; set; } = "";
    public string Location { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public double ClaimedAmount { get; set; }
}

// ── Agent decision (the JSON the model must emit) ─────────────────────────────

/// <summary>Structured triage decision produced by the agent.</summary>
public class TriageDecision
{
    /// <summary>FAST_TRACK | ADJUSTER_REVIEW | SIU_REFERRAL | COVERAGE_REVIEW</summary>
    public string Decision { get; set; } = "";
    /// <summary>MINOR | MODERATE | SEVERE | TOTAL_LOSS</summary>
    public string Severity { get; set; } = "";
    public double FraudRiskScore { get; set; }
    public double EstimatedRepairCost { get; set; }
    public string Reasoning { get; set; } = "";
    public string[] NextSteps { get; set; } = [];
}

// ── Lambda output ─────────────────────────────────────────────────────────────

/// <summary>Full triage response returned by the Lambda, including run telemetry.</summary>
public class TriageResult
{
    public string ClaimId { get; set; } = "";
    public string PolicyNumber { get; set; } = "";
    public string ProcessedAtUtc { get; set; } = "";
    public TriageDecision Triage { get; set; } = new();
    public RunTelemetry Telemetry { get; set; } = new();
}

/// <summary>Observability metadata captured by hooks during the agent run.</summary>
public class RunTelemetry
{
    /// <summary>bedrock | offline</summary>
    public string ModelMode { get; set; } = "";
    public string ModelId { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int ToolCallCount { get; set; }
    public string[] ToolsUsed { get; set; } = [];
    public long DurationMs { get; set; }
}

// ── Tool / mock-service DTOs ──────────────────────────────────────────────────

/// <summary>Policy record returned by the (mock) policy administration system.</summary>
public class PolicyRecord
{
    public bool Found { get; set; }
    public string PolicyNumber { get; set; } = "";
    /// <summary>Active | Lapsed | Cancelled</summary>
    public string Status { get; set; } = "";
    public string HolderName { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public string EffectiveDate { get; set; } = "";
    public string ExpirationDate { get; set; } = "";
    public string CoverageType { get; set; } = "";
    public double Deductible { get; set; }
    public double CollisionLimit { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>Prior-claims summary returned by the (mock) claims data store.</summary>
public class ClaimHistoryRecord
{
    public string PolicyNumber { get; set; } = "";
    public int TotalClaims { get; set; }
    public int ClaimsLast12Months { get; set; }
    public PriorClaim[] Claims { get; set; } = [];
}

public class PriorClaim
{
    public string ClaimId { get; set; } = "";
    public string LossDate { get; set; } = "";
    public string LossType { get; set; } = "";
    public double PaidAmount { get; set; }
    /// <summary>Paid | Denied | Withdrawn | Open</summary>
    public string Status { get; set; } = "";
    public bool AtFault { get; set; }
}

/// <summary>Fraud signal report returned by the (mock) third-party scoring API.</summary>
public class FraudSignalReport
{
    public string PolicyNumber { get; set; } = "";
    /// <summary>0.0 (clean) to 1.0 (near-certain fraud).</summary>
    public double Score { get; set; }
    /// <summary>LOW | ELEVATED | HIGH</summary>
    public string Band { get; set; } = "";
    public bool WatchlistHit { get; set; }
    public string[] Signals { get; set; } = [];
}

/// <summary>Repair estimate returned by the (mock) collision estimating service.</summary>
public class RepairEstimate
{
    public string Vehicle { get; set; } = "";
    /// <summary>MINOR | MODERATE | SEVERE | TOTAL_LOSS</summary>
    public string Severity { get; set; } = "";
    public double LowEstimate { get; set; }
    public double HighEstimate { get; set; }
    public bool TotalLossLikely { get; set; }
    public string Notes { get; set; } = "";
}

// ── AOT-safe JSON context ─────────────────────────────────────────────────────
// Every type that crosses a JSON boundary (Lambda payloads, tool results,
// the model's decision JSON) is registered here so System.Text.Json can
// serialize it with source-generated code — zero runtime reflection.

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ClaimEvent))]
[JsonSerializable(typeof(TriageDecision))]
[JsonSerializable(typeof(TriageResult))]
[JsonSerializable(typeof(RunTelemetry))]
[JsonSerializable(typeof(PolicyRecord))]
[JsonSerializable(typeof(ClaimHistoryRecord))]
[JsonSerializable(typeof(PriorClaim))]
[JsonSerializable(typeof(PriorClaim[]))]
[JsonSerializable(typeof(FraudSignalReport))]
[JsonSerializable(typeof(RepairEstimate))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
public partial class ClaimsJsonContext : JsonSerializerContext { }
