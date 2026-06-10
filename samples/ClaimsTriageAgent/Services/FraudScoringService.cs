using ClaimsTriageAgent.Models;

namespace ClaimsTriageAgent.Services;

/// <summary>
/// Mock of a third-party fraud-analytics API (the role FRISS or Shift
/// Technology plays in a real carrier). Scoring is deterministic and
/// rule-based so demo runs are reproducible, but the shape of the response —
/// a 0–1 score, a band, a watchlist flag, and named signals — mirrors what
/// real fraud-scoring vendors return.
/// </summary>
public static class FraudScoringService
{
    /// <summary>Policies flagged by the (mock) industry fraud consortium watchlist.</summary>
    private static readonly HashSet<string> ConsortiumWatchlist = ["POL-204977"];

    private static readonly string[] SuspiciousPhrases =
    [
        "no witnesses", "no police report", "paid cash", "paid in cash",
        "recently purchased", "just bought", "aftermarket", "disappeared",
        "can't remember", "cannot remember"
    ];

    public static FraudSignalReport Score(
        string policyNumber,
        string lossType,
        string incidentDescription,
        string incidentDate,
        string reportedDate)
    {
        var signals = new List<string>();
        var score = 0.05; // baseline — every claim carries some residual risk

        if (ConsortiumWatchlist.Contains(policyNumber))
        {
            score += 0.35;
            signals.Add("Policy appears on the industry fraud consortium watchlist.");
        }

        var description = incidentDescription.ToLowerInvariant();
        var phraseHits = SuspiciousPhrases.Where(description.Contains).ToArray();
        if (phraseHits.Length > 0)
        {
            score += Math.Min(0.10 * phraseHits.Length, 0.20);
            signals.Add($"Narrative contains red-flag phrasing: {string.Join("; ", phraseHits)}.");
        }

        if (lossType is "theft" or "fire")
        {
            score += 0.15;
            signals.Add($"Loss type '{lossType}' is statistically over-represented in confirmed fraud.");
        }

        if (DateTimeOffset.TryParse(incidentDate, out var incident) &&
            DateTimeOffset.TryParse(reportedDate, out var reported))
        {
            var daysToReport = (reported - incident).TotalDays;
            if (daysToReport > 30)
            {
                score += 0.10;
                signals.Add($"Claim reported {daysToReport:0} days after the incident (threshold: 30).");
            }
        }

        // Frequency signal from the (mock) consortium claims database.
        var history = PolicyAdminSystem.GetClaimHistory(policyNumber, DateTimeOffset.UtcNow);
        if (history.ClaimsLast12Months >= 2)
        {
            score += 0.10 * (history.ClaimsLast12Months - 1);
            signals.Add($"{history.ClaimsLast12Months} claims filed in the last 12 months.");
        }

        score = Math.Min(score, 0.99);
        if (signals.Count == 0)
            signals.Add("No fraud indicators detected.");

        return new FraudSignalReport
        {
            PolicyNumber = policyNumber,
            Score = Math.Round(score, 2),
            Band = score switch
            {
                >= 0.65 => "HIGH",
                >= 0.30 => "ELEVATED",
                _ => "LOW"
            },
            WatchlistHit = ConsortiumWatchlist.Contains(policyNumber),
            Signals = [.. signals]
        };
    }
}
