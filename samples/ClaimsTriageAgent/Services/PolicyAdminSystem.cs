using ClaimsTriageAgent.Models;

namespace ClaimsTriageAgent.Services;

/// <summary>
/// Mock of the carrier's policy administration system (the role Guidewire
/// PolicyCenter or Duck Creek Policy plays in a real carrier). In production
/// the tools would call its REST API; here a deterministic in-memory data set
/// stands in so the agent can be run and demoed anywhere.
/// </summary>
public static class PolicyAdminSystem
{
    private static readonly Dictionary<string, PolicyRecord> Policies = new()
    {
        ["POL-100234"] = new PolicyRecord
        {
            Found = true,
            PolicyNumber = "POL-100234",
            Status = "Active",
            HolderName = "Sarah Mitchell",
            Vehicle = "2021 Honda Accord EX",
            EffectiveDate = "2025-11-01",
            ExpirationDate = "2026-11-01",
            CoverageType = "Comprehensive + Collision",
            Deductible = 500,
            CollisionLimit = 50_000,
            Notes = "Auto-pay enabled. Safe-driver discount tier 2."
        },
        ["POL-204977"] = new PolicyRecord
        {
            Found = true,
            PolicyNumber = "POL-204977",
            Status = "Active",
            HolderName = "Dale Hardin",
            Vehicle = "2019 Ford F-150 XLT",
            EffectiveDate = "2026-01-15",
            ExpirationDate = "2027-01-15",
            CoverageType = "Comprehensive + Collision",
            Deductible = 1_000,
            CollisionLimit = 45_000,
            Notes = "Coverage limits raised at last renewal at policyholder request."
        },
        ["POL-318562"] = new PolicyRecord
        {
            Found = true,
            PolicyNumber = "POL-318562",
            Status = "Lapsed",
            HolderName = "Miguel Ortega",
            Vehicle = "2018 Toyota Camry SE",
            EffectiveDate = "2025-05-01",
            ExpirationDate = "2026-05-01",
            CoverageType = "Liability + Collision",
            Deductible = 750,
            CollisionLimit = 30_000,
            Notes = "Lapsed 2026-04-30 for premium non-payment. Reinstatement window expired."
        },
        ["POL-447210"] = new PolicyRecord
        {
            Found = true,
            PolicyNumber = "POL-447210",
            Status = "Active",
            HolderName = "Janet Wu",
            Vehicle = "2024 BMW X5 xDrive40i",
            EffectiveDate = "2025-09-12",
            ExpirationDate = "2026-09-12",
            CoverageType = "Comprehensive + Collision",
            Deductible = 1_000,
            CollisionLimit = 90_000,
            Notes = "New-vehicle replacement endorsement active."
        },
        ["POL-552981"] = new PolicyRecord
        {
            Found = true,
            PolicyNumber = "POL-552981",
            Status = "Active",
            HolderName = "Robert Klein",
            Vehicle = "2020 Subaru Outback Premium",
            EffectiveDate = "2026-02-20",
            ExpirationDate = "2027-02-20",
            CoverageType = "Comprehensive + Collision",
            Deductible = 250,
            CollisionLimit = 40_000,
            Notes = "Claim-free discount tier 1."
        },
    };

    private static readonly Dictionary<string, PriorClaim[]> ClaimHistory = new()
    {
        ["POL-100234"] =
        [
            new PriorClaim
            {
                ClaimId = "CLM-2023-08812", LossDate = "2023-04-18", LossType = "glass",
                PaidAmount = 412.50, Status = "Paid", AtFault = false
            },
        ],
        ["POL-204977"] =
        [
            new PriorClaim
            {
                ClaimId = "CLM-2025-30417", LossDate = "2025-05-02", LossType = "collision",
                PaidAmount = 8_240, Status = "Paid", AtFault = false
            },
            new PriorClaim
            {
                ClaimId = "CLM-2025-41188", LossDate = "2025-10-19", LossType = "collision",
                PaidAmount = 11_960, Status = "Paid", AtFault = false
            },
            new PriorClaim
            {
                ClaimId = "CLM-2026-07733", LossDate = "2026-02-08", LossType = "vandalism",
                PaidAmount = 3_150, Status = "Paid", AtFault = false
            },
        ],
        ["POL-318562"] = [],
        ["POL-447210"] =
        [
            new PriorClaim
            {
                ClaimId = "CLM-2025-51902", LossDate = "2025-12-03", LossType = "glass",
                PaidAmount = 980, Status = "Paid", AtFault = false
            },
        ],
        ["POL-552981"] = [],
    };

    public static PolicyRecord LookupPolicy(string policyNumber)
    {
        if (Policies.TryGetValue(policyNumber, out var policy))
            return policy;

        return new PolicyRecord
        {
            Found = false,
            PolicyNumber = policyNumber,
            Status = "NotFound",
            Notes = "No policy with this number exists in the policy administration system."
        };
    }

    public static ClaimHistoryRecord GetClaimHistory(string policyNumber, DateTimeOffset asOf)
    {
        var claims = ClaimHistory.GetValueOrDefault(policyNumber, []);
        var last12Months = claims.Count(c =>
            DateTimeOffset.TryParse(c.LossDate, out var lossDate) &&
            lossDate >= asOf.AddMonths(-12));

        return new ClaimHistoryRecord
        {
            PolicyNumber = policyNumber,
            TotalClaims = claims.Length,
            ClaimsLast12Months = last12Months,
            Claims = claims
        };
    }
}
