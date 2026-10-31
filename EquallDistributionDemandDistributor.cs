namespace Demand_Gen;

// Allocates each zone evenly after applying the supported junction weight modifiers.
public static class EqualDistributionDemandDistributor
{
    /// <summary>
    /// Distributes demand using only junction modifiers; pipe lengths and K factors are ignored.
    /// </summary>
    public static DemandDistributionResult Calculate(DemandValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Demand instructions must pass validation before calculation.");
        }

        if (validation.Method != DemandDistributionMethod.EqualDistribution)
        {
            throw new InvalidOperationException("Equal-Distribution calculation requires Equal-Distribution validation.");
        }

        var zoneAllocations = new List<ZoneDemandAllocation>(validation.Zones.Count);
        foreach (var zone in validation.Zones)
        {
            var requestedDemand = zone.Demand
                ?? throw new InvalidOperationException($"Zone '{zone.Name}' has no validated demand.");

            // The input model reuses the K-named modifier fields for junction modifiers in this method.
            var halfDemandJunctions = new HashSet<string>(zone.KHalfPipes, StringComparer.OrdinalIgnoreCase);
            var excludedJunctions = new HashSet<string>(zone.KZeroPipes, StringComparer.OrdinalIgnoreCase);
            var weights = zone.Junctions.ToDictionary(
                id => id,
                id => excludedJunctions.Contains(id) ? 0.0 : halfDemandJunctions.Contains(id) ? 0.5 : 1.0,
                StringComparer.OrdinalIgnoreCase);
            var totalWeight = weights.Values.Sum();

            if (requestedDemand > 0 && totalWeight <= 0)
            {
                throw new InvalidOperationException(
                    $"Zone '{zone.Name}' has positive demand but no junction with positive allocation weight.");
            }

            var demands = zone.Junctions.ToDictionary(
                id => id,
                _ => 0.0,
                StringComparer.OrdinalIgnoreCase);
            if (requestedDemand > 0)
            {
                // Normalize each junction's weight so the zone receives exactly its requested total.
                foreach (var junctionId in zone.Junctions)
                {
                    demands[junctionId] = requestedDemand * (weights[junctionId] / totalWeight);
                }

                // Correct tiny floating-point residue on the junction with the greatest allocation weight.
                var allocated = zone.Junctions.Sum(id => demands[id]);
                var correctionJunction = zone.Junctions.MaxBy(id => weights[id]);
                demands[correctionJunction!] += requestedDemand - allocated;
            }

            var junctionAllocations = zone.Junctions
                .Select(id => new JunctionDemandAllocation(id, zone.Name, weights[id], demands[id]))
                .ToArray();
            zoneAllocations.Add(new ZoneDemandAllocation(zone.Name, requestedDemand, junctionAllocations));
        }

        return new DemandDistributionResult(DemandDistributionMethod.EqualDistribution, zoneAllocations);
    }
}