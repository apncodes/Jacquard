using System.Text.Json;
using ClaimsTriageAgent.Models;
using ClaimsTriageAgent.Services;
using Jacquard.Core;

namespace ClaimsTriageAgent.Tools;

/// <summary>Tool over the third-party fraud-analytics API (mocked in this sample).</summary>
public sealed partial class FraudTools
{
    /// <summary>Runs fraud-signal analysis for a claim.</summary>
    /// <param name="policyNumber">Carrier policy number, format POL-XXXXXX.</param>
    /// <param name="lossType">Loss type, e.g. collision, theft, hail, vandalism, flood, fire, glass.</param>
    /// <param name="incidentDescription">The claimant's narrative of the incident, verbatim.</param>
    /// <param name="incidentDate">ISO-8601 date the loss occurred.</param>
    /// <param name="reportedDate">ISO-8601 date the claim was reported.</param>
    [Tool("Run fraud-signal analysis on a claim via the fraud-scoring service. Returns a 0-1 risk score, " +
          "a band (LOW/ELEVATED/HIGH), a consortium watchlist flag, and the named signals found. " +
          "Pass the claimant's incident description verbatim.")]
    public string CheckFraudSignals(
        [ToolParameterValidation(Required = true, Pattern = "^POL-[0-9]{6}$")] string policyNumber,
        [ToolParameterValidation(Required = true)] string lossType,
        [ToolParameterValidation(Required = true, MinLength = 10)] string incidentDescription,
        string incidentDate,
        string reportedDate)
    {
        var report = FraudScoringService.Score(
            policyNumber, lossType, incidentDescription, incidentDate, reportedDate);
        return JsonSerializer.Serialize(report, ClaimsJsonContext.Default.FraudSignalReport);
    }
}
