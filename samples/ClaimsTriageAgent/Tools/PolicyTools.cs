using System.Text.Json;
using ClaimsTriageAgent.Models;
using ClaimsTriageAgent.Services;
using Jacquard.Core;

namespace ClaimsTriageAgent.Tools;

/// <summary>
/// Read-only tools over the policy administration system. Least-privilege by
/// design: the triage agent can look up coverage and history but can never
/// modify a policy, reserve funds, or issue a payment.
/// </summary>
public sealed partial class PolicyTools
{
    /// <summary>Looks up a policy's status, coverage, and limits.</summary>
    /// <param name="policyNumber">Carrier policy number, format POL-XXXXXX.</param>
    [Tool("Look up a policy in the policy administration system. Returns status (Active/Lapsed/Cancelled), " +
          "coverage dates, coverage type, deductible, and collision limit. Always call this first.")]
    public string LookupPolicy(
        [ToolParameterValidation(Required = true, Pattern = "^POL-[0-9]{6}$")] string policyNumber)
    {
        var policy = PolicyAdminSystem.LookupPolicy(policyNumber);
        return JsonSerializer.Serialize(policy, ClaimsJsonContext.Default.PolicyRecord);
    }

    /// <summary>Retrieves the prior-claims history for a policy.</summary>
    /// <param name="policyNumber">Carrier policy number, format POL-XXXXXX.</param>
    [Tool("Retrieve the prior-claims history for a policy: total claims, claims in the last 12 months, " +
          "and per-claim loss type, paid amount, and fault. Use this to judge claim frequency.")]
    public string GetClaimHistory(
        [ToolParameterValidation(Required = true, Pattern = "^POL-[0-9]{6}$")] string policyNumber)
    {
        var history = PolicyAdminSystem.GetClaimHistory(policyNumber, DateTimeOffset.UtcNow);
        return JsonSerializer.Serialize(history, ClaimsJsonContext.Default.ClaimHistoryRecord);
    }
}
