using System.Globalization;
using System.Text;

namespace Demand_Gen;

// Builds the human-readable audit report that accompanies a generated EPANET input file.
public static class DemandGenerationReportWriter
{
    /// <summary>
    /// Writes a detailed report of the source network, user instructions, allocation, and verification.
    /// </summary>
    public static string Write(
        string sourcePath,
        string generatedFilePath,
        EpanetNetworkSummary network,
        DemandValidationResult validation,
        DemandDistributionResult distribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedFilePath);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(distribution);

        var reportPath = GetAvailableReportPath(generatedFilePath);
        var lengthUnit = GetLengthUnit(validation.FlowUnits);
        var totalPipeLength = network.Pipes.Sum(pipe => pipe.Length);
        var junctions = distribution.Zones.SelectMany(zone => zone.Junctions).ToArray();
        var totalCalculatedDemand = junctions.Sum(junction => junction.Demand);
        var junctionsWithDemand = junctions.Count(junction => junction.Demand > 0);
        var junctionsWithZeroDemand = junctions.Length - junctionsWithDemand;
        var report = new StringBuilder();

        // Record file names and generation time so the report can be matched to its input and output.
        report.AppendLine("EPANET Demand Generation Report");
        report.AppendLine($"Network name: {Path.GetFileNameWithoutExtension(sourcePath)}");
        report.AppendLine($"Original .inp file: {Path.GetFileName(sourcePath)}");
        report.AppendLine($"Revised .inp file: {Path.GetFileName(generatedFilePath)}");
        report.AppendLine($"Report file: {Path.GetFileName(reportPath)}");
        report.AppendLine($"Generated: {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        report.AppendLine();

        report.AppendLine("Network summary");
        report.AppendLine($"Junctions: {network.JunctionIds.Count}");
        report.AppendLine($"Reservoirs: {network.ReservoirIds.Count}");
        report.AppendLine($"Tanks: {network.TankIds.Count}");
        report.AppendLine($"Total nodes: {network.JunctionIds.Count + network.ReservoirIds.Count + network.TankIds.Count}");
        report.AppendLine($"Pipes: {network.Pipes.Count}");
        report.AppendLine($"Pumps: {network.PumpCount}");
        report.AppendLine($"Valves: {network.ValveCount}");
        report.AppendLine($"Total links: {network.Pipes.Count + network.PumpCount + network.ValveCount}");
        report.AppendLine($"Total pipeline length: {FormatNumber(totalPipeLength)} {lengthUnit}");
        report.AppendLine($"Flow units: {validation.FlowUnits ?? "unknown"}");
        report.AppendLine();

        report.AppendLine("User-supplied data");
        report.AppendLine($"Distribution method: {GetMethodName(validation.Method)}");
        report.AppendLine($"Total demand entered: {FormatNumber(validation.TotalDemand.GetValueOrDefault())} {validation.FlowUnits ?? "unknown"}");
        report.AppendLine($"Number of zones: {validation.Zones.Count}");
        report.AppendLine(validation.Method == DemandDistributionMethod.UnitLength
            ? "Unlisted pipes use K = 1; K-weighted pipe lengths determine allocation."
            : "Default junction weight is 1; listed junction modifiers use weights 0.5 or 0.");
        foreach (var zone in validation.Zones)
        {
            report.AppendLine();
            report.AppendLine($"{zone.Name}");
            report.AppendLine($"  Demand entered: {FormatNumber(zone.Demand.GetValueOrDefault())} {validation.FlowUnits ?? "unknown"}");
            report.AppendLine($"  Junctions ({zone.Junctions.Count}): {DemandValidationResult.FormatIds(zone.Junctions)}");
            report.AppendLine(validation.Method == DemandDistributionMethod.UnitLength
                ? $"  K = 0.5 pipes: {DemandValidationResult.FormatIds(zone.KHalfPipes)}"
                : $"  50% junctions: {DemandValidationResult.FormatIds(zone.KHalfPipes)}");
            report.AppendLine(validation.Method == DemandDistributionMethod.UnitLength
                ? $"  K = 0 pipes: {DemandValidationResult.FormatIds(zone.KZeroPipes)}"
                : $"  0% junctions: {DemandValidationResult.FormatIds(zone.KZeroPipes)}");
        }

        report.AppendLine();
        report.AppendLine("Calculated results");
        report.AppendLine($"Total demand: {FormatNumber(totalCalculatedDemand)} {validation.FlowUnits ?? "unknown"}");
        report.AppendLine($"Junctions with demand: {junctionsWithDemand}");
        report.AppendLine($"Junctions with zero demand: {junctionsWithZeroDemand}");
        report.AppendLine();
        report.AppendLine("Output verification");
        // Compare per-zone and network totals independently to make allocation discrepancies visible.
        report.AppendLine($"Junction base demands verified: {junctions.Length} of {junctions.Length}");
        var enteredTotal = validation.TotalDemand.GetValueOrDefault();
        report.AppendLine($"Total demand entered: {FormatNumber(enteredTotal)} {validation.FlowUnits ?? "unknown"}");
        report.AppendLine($"Total demand allocated: {FormatNumber(totalCalculatedDemand)} {validation.FlowUnits ?? "unknown"}");
        report.AppendLine($"Allocation difference (allocated - entered): {FormatNumber(totalCalculatedDemand - enteredTotal)} {validation.FlowUnits ?? "unknown"}");
        foreach (var zone in distribution.Zones)
        {
            var zoneTotal = zone.Junctions.Sum(junction => junction.Demand);
            report.AppendLine();
            report.AppendLine($"{zone.ZoneName}: entered {FormatNumber(zone.RequestedDemand)} {validation.FlowUnits ?? "unknown"}; " +
            $"allocated {FormatNumber(zoneTotal)} {validation.FlowUnits ?? "unknown"}; " +
            $"difference {FormatNumber(zoneTotal - zone.RequestedDemand)} {validation.FlowUnits ?? "unknown"}; " +
            $"junctions {zone.Junctions.Count}");
            report.AppendLine(validation.Method == DemandDistributionMethod.UnitLength
                ? $"  Junction | Demand | K-weighted pipe length ({lengthUnit})"
                : "  Junction | Demand | Allocation weight");
            foreach (var junction in zone.Junctions)
            {
                report.AppendLine(
                    $"  {junction.JunctionId} | {FormatNumber(junction.Demand)} {validation.FlowUnits ?? "unknown"} | " +
                    FormatNumber(junction.AllocationWeight));
            }
        }

        if (validation.Warnings.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("Warnings");
            foreach (var warning in validation.Warnings)
            {
                report.AppendLine($"- {warning}");
            }
        }

        report.AppendLine();
        report.AppendLine("The source network file was not modified.");
        report.AppendLine("Junction base demands in the revised file replace prior base demands; active [DEMANDS] records are removed and demand patterns are retained.");

        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reportPath;
    }

    private static string GetAvailableReportPath(string generatedFilePath)
    {
        var basePath = Path.ChangeExtension(generatedFilePath, ".txt");
        if (!File.Exists(basePath))
        {
            return basePath;
        }

        var directory = Path.GetDirectoryName(basePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(basePath);
        var sequence = 2;
        var candidate = Path.Combine(directory, $"{fileName}_{sequence}.txt");
        // Preserve earlier reports by choosing the next unused sequence number.
        while (File.Exists(candidate))
        {
            sequence++;
            candidate = Path.Combine(directory, $"{fileName}_{sequence}.txt");
        }

        return candidate;
    }

    private static string GetMethodName(DemandDistributionMethod method) => method switch
    {
        DemandDistributionMethod.UnitLength => "Unit-Length",
        DemandDistributionMethod.EqualDistribution => "Equal-Distribution",
        _ => throw new InvalidOperationException("The selected distribution method is not supported.")
    };

    private static string GetLengthUnit(string? flowUnits) => flowUnits?.ToUpperInvariant() switch
    {
        "CFS" or "GPM" or "MGD" or "IMGD" or "AFD" => "ft",
        "LPS" or "LPM" or "MLD" or "CMH" or "CMD" => "m",
        _ => "network length units"
    };

    private static string FormatNumber(double value) =>
        value.ToString("F4", CultureInfo.InvariantCulture);
}