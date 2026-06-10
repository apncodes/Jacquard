using System.Text.Json;
using ClaimsTriageAgent.Models;
using ClaimsTriageAgent.Services;
using Jacquard.Core;

namespace ClaimsTriageAgent.Tools;

/// <summary>Tool over the collision-estimating service (mocked in this sample).</summary>
public sealed partial class RepairTools
{
    /// <summary>Produces a preliminary repair-cost estimate for the damage described.</summary>
    /// <param name="vehicle">Year, make, and model of the insured vehicle.</param>
    /// <param name="lossType">Loss type, e.g. collision, theft, hail, vandalism, flood, fire, glass.</param>
    /// <param name="damageDescription">Description of the damage to the vehicle.</param>
    [Tool("Get a preliminary repair-cost estimate from the collision-estimating service. Returns severity " +
          "(MINOR/MODERATE/SEVERE/TOTAL_LOSS), a low-high estimate range in USD, and whether the vehicle " +
          "is likely a total loss.")]
    public string EstimateRepairCost(
        [ToolParameterValidation(Required = true)] string vehicle,
        [ToolParameterValidation(Required = true)] string lossType,
        [ToolParameterValidation(Required = true, MinLength = 10)] string damageDescription)
    {
        var estimate = RepairEstimator.Estimate(vehicle, lossType, damageDescription);
        return JsonSerializer.Serialize(estimate, ClaimsJsonContext.Default.RepairEstimate);
    }
}
