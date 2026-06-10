using ClaimsTriageAgent.Models;

namespace ClaimsTriageAgent.Services;

/// <summary>
/// Mock of a collision-estimating service (the role CCC ONE or Mitchell
/// plays in a real carrier). Severity is inferred from damage keywords and
/// the estimate is scaled by vehicle class — deterministic, so demo runs
/// are reproducible.
/// </summary>
public static class RepairEstimator
{
    private static readonly string[] SevereKeywords =
    [
        "airbag", "frame", "rollover", "flood", "fire", "engine",
        "totaled", "undriveable", "submerged", "crumple"
    ];

    private static readonly string[] ModerateKeywords =
    [
        "door", "quarter panel", "suspension", "axle", "radiator",
        "hood", "roof", "fender", "stolen", "theft", "shattered"
    ];

    private static readonly string[] LuxuryMakes =
    [
        "bmw", "mercedes", "audi", "tesla", "lexus", "porsche", "land rover"
    ];

    public static RepairEstimate Estimate(string vehicle, string lossType, string damageDescription)
    {
        var text = $"{lossType} {damageDescription}".ToLowerInvariant();

        var severeHits = SevereKeywords.Count(text.Contains);
        var moderateHits = ModerateKeywords.Count(text.Contains);

        var (severity, baseLow, baseHigh) = (severeHits, moderateHits) switch
        {
            ( >= 2, _) => ("TOTAL_LOSS", 22_000.0, 38_000.0),
            ( >= 1, _) => ("SEVERE", 14_000.0, 24_000.0),
            (_, >= 2) => ("MODERATE", 6_500.0, 12_500.0),
            (_, >= 1) => ("MODERATE", 4_500.0, 9_000.0),
            _ => ("MINOR", 900.0, 3_200.0)
        };

        var vehicleLower = vehicle.ToLowerInvariant();
        var multiplier = LuxuryMakes.Any(vehicleLower.Contains) ? 1.8
            : vehicleLower.Contains("f-150") || vehicleLower.Contains("truck") || vehicleLower.Contains("suv") ? 1.3
            : 1.0;

        // Total theft of the vehicle is an actual-cash-value payout, not a repair.
        var totalTheft = lossType == "theft" && !text.Contains("attempted");
        if (totalTheft)
        {
            severity = "TOTAL_LOSS";
            (baseLow, baseHigh) = (18_000.0, 32_000.0);
        }

        return new RepairEstimate
        {
            Vehicle = vehicle,
            Severity = severity,
            LowEstimate = Math.Round(baseLow * multiplier, 0),
            HighEstimate = Math.Round(baseHigh * multiplier, 0),
            TotalLossLikely = severity == "TOTAL_LOSS",
            Notes = totalTheft
                ? "Vehicle theft — estimate reflects actual cash value (ACV) payout range, not repair cost."
                : $"Keyword-derived severity '{severity}'; vehicle class multiplier {multiplier:0.0}x applied."
        };
    }
}
