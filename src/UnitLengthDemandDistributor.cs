using System.Globalization;
using System.Text;

namespace Demand_Gen;

public sealed record JunctionDemandAllocation(
    string JunctionId,
    string ZoneName,
    double AllocationWeight,
    double Demand);

// Groups the computed junction demands and their requested total for one zone.
public sealed record ZoneDemandAllocation(
    string ZoneName,
    double RequestedDemand,
    IReadOnlyList<JunctionDemandAllocation> Junctions);

// Holds the complete allocation result independently of the UI or file writer.
public sealed record DemandDistributionResult(
    DemandDistributionMethod Method,
    IReadOnlyList<ZoneDemandAllocation> Zones)
{
    // Produces a concise summary for the application's validation/report panel.
    public string BuildReport(string? flowUnits)
    {
        var report = new StringBuilder();
        report.AppendLine(Method == DemandDistributionMethod.UnitLength
            ? "Unit-Length calculation:"
            : "Equal-Distribution calculation:");

        // Flatten zone results so network-wide totals and junction counts include every zone.
        var junctions = Zones.SelectMany(zone => zone.Junctions).ToArray();
        var totalDemand = junctions.Sum(junction => junction.Demand);
        var junctionsWithDemand = junctions.Count(junction => junction.Demand > 0);
        var junctionsWithZeroDemand = junctions.Length - junctionsWithDemand;
        report.AppendLine($"Total demand: {FormatNumber(totalDemand)} {flowUnits ?? "unknown"}");
        report.AppendLine($"Junctions with demand: {junctionsWithDemand}");
        report.AppendLine($"Junctions with zero demand: {junctionsWithZeroDemand}");
        return report.ToString();
    }

    private static string FormatNumber(double value) =>
        value.ToString("F4", CultureInfo.InvariantCulture);
}

public static class UnitLengthDemandDistributor
{
    /// <summary>
    /// Allocates each zone's demand in proportion to K-weighted pipe length at its junctions.
    /// </summary>
    public static DemandDistributionResult Calculate(
        EpanetNetworkSummary network,
        DemandValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(validation);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Demand instructions must pass validation before calculation.");
        }

        if (validation.Method != DemandDistributionMethod.UnitLength)
        {
            throw new InvalidOperationException("Unit-Length calculation requires Unit-Length validation.");
        }

        // Resolve junction ownership and explicit K overrides once, using EPANET's case-insensitive IDs.
        var zonesByJunction = new Dictionary<string, ParsedZoneInstruction>(StringComparer.OrdinalIgnoreCase);
        var kByPipe = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in validation.Zones)
        {
            foreach (var junctionId in zone.Junctions)
            {
                if (!zonesByJunction.TryAdd(junctionId, zone))
                {
                    throw new InvalidOperationException($"Junction '{junctionId}' is assigned to more than one zone.");
                }
            }

            foreach (var pipeId in zone.KHalfPipes)
            {
                // TryAdd also guards against duplicate assignments if this method is called without valid input.
                if (!kByPipe.TryAdd(pipeId, 0.5))
                {
                    throw new InvalidOperationException($"Pipe '{pipeId}' has more than one K-factor assignment.");
                }
            }

            foreach (var pipeId in zone.KZeroPipes)
            {
                if (!kByPipe.TryAdd(pipeId, 0.0))
                {
                    throw new InvalidOperationException($"Pipe '{pipeId}' has more than one K-factor assignment.");
                }
            }
        }

        var pipeWeights = network.JunctionIds.ToDictionary(
            id => id,
            _ => 0.0,
            StringComparer.OrdinalIgnoreCase);

        // Accumulate each pipe's effective length at its junction endpoints.
        // Junction-to-junction pipes are split evenly; a reservoir/tank endpoint receives no share.
        foreach (var pipe in network.Pipes)
        {
            var k = kByPipe.TryGetValue(pipe.Id, out var assignedK) ? assignedK : 1.0;
            var effectiveLength = pipe.Length * k;
            if (!double.IsFinite(effectiveLength))
            {
                throw new InvalidOperationException($"Pipe '{pipe.Id}' produces a non-finite K-weighted length.");
            }

            var hasStartJunction = zonesByJunction.ContainsKey(pipe.StartNodeId);
            var hasEndJunction = zonesByJunction.ContainsKey(pipe.EndNodeId);
            if (hasStartJunction && hasEndJunction)
            {
                var endpointWeight = effectiveLength / 2.0;
                AddWeight(pipeWeights, pipe.StartNodeId, endpointWeight);
                AddWeight(pipeWeights, pipe.EndNodeId, endpointWeight);
            }
            else if (hasStartJunction)
            {
                AddWeight(pipeWeights, pipe.StartNodeId, effectiveLength);
            }
            else if (hasEndJunction)
            {
                AddWeight(pipeWeights, pipe.EndNodeId, effectiveLength);
            }
        }

        var zoneAllocations = new List<ZoneDemandAllocation>(validation.Zones.Count);
        foreach (var zone in validation.Zones)
        {
            var requestedDemand = zone.Demand
                ?? throw new InvalidOperationException($"Zone '{zone.Name}' has no validated demand.");
            var includedIds = zone.Junctions;
            var totalIncludedWeight = includedIds.Sum(id => pipeWeights[id]);
            if (!double.IsFinite(totalIncludedWeight))
            {
                throw new InvalidOperationException($"Zone '{zone.Name}' has a non-finite total pipe weight.");
            }

            if (requestedDemand > 0 && totalIncludedWeight <= 0)
            {
                throw new InvalidOperationException(
                    $"Zone '{zone.Name}' has no positive K-weighted pipe length for its demand.");
            }

            // A zero-demand zone remains valid even when all its junction weights are zero.
            var demands = zone.Junctions.ToDictionary(
                id => id,
                _ => 0.0,
                StringComparer.OrdinalIgnoreCase);
            if (requestedDemand > 0)
            {
                foreach (var junctionId in includedIds)
                {
                    demands[junctionId] = requestedDemand * (pipeWeights[junctionId] / totalIncludedWeight);
                }

                // Put floating-point rounding residue on the highest-weight junction to preserve the zone total.
                var allocated = includedIds.Sum(id => demands[id]);
                var correctionJunction = includedIds.MaxBy(id => pipeWeights[id]);
                demands[correctionJunction!] += requestedDemand - allocated;
            }

            var junctionAllocations = zone.Junctions
                .Select(id => new JunctionDemandAllocation(id, zone.Name, pipeWeights[id], demands[id]))
                .ToArray();
            zoneAllocations.Add(new ZoneDemandAllocation(zone.Name, requestedDemand, junctionAllocations));
        }

        return new DemandDistributionResult(DemandDistributionMethod.UnitLength, zoneAllocations);
    }

    private static void AddWeight(IDictionary<string, double> pipeWeights, string junctionId, double weight)
    {
        pipeWeights[junctionId] += weight;
        // Detect overflow at the point it enters the accumulated junction weight.
        if (!double.IsFinite(pipeWeights[junctionId]))
        {
            throw new InvalidOperationException($"Junction '{junctionId}' has a non-finite pipe weight.");
        }
    }
}