using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Demand_Gen;

// Describes which weighting rules the validator and distributor should apply.
public enum DemandDistributionMethod
{
    UnitLength,
    EqualDistribution
}

// Raw grid values stay as strings until validation so invalid user input can be reported precisely.
public sealed record ZoneInstructionDraft(
    string? Name,
    string? Demand,
    string? Junctions,
    string? KHalfPipes,
    string? KZeroPipes);

// Contains normalized IDs and parsed values used after validation succeeds.
public sealed record ParsedZoneInstruction(
    string Name,
    double? Demand,
    IReadOnlyList<string> Junctions,
    IReadOnlyList<string> KHalfPipes,
    IReadOnlyList<string> KZeroPipes);

public sealed class DemandValidationResult
{
    internal DemandValidationResult(
        DemandDistributionMethod method,
        double? totalDemand,
        string? flowUnits,
        IReadOnlyList<ParsedZoneInstruction> zones,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings)
    {
        Method = method;
        TotalDemand = totalDemand;
        FlowUnits = flowUnits;
        Zones = zones;
        Errors = errors;
        Warnings = warnings;
    }

    public DemandDistributionMethod Method { get; }
    public double? TotalDemand { get; }
    public string? FlowUnits { get; }
    public IReadOnlyList<ParsedZoneInstruction> Zones { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsValid => Errors.Count == 0;

    // Renders instructions, zone-specific issues, cross-zone issues, and warnings for the UI.
    public string BuildReport()
    {
        var report = new StringBuilder();
        report.AppendLine("Parsed instructions");
        var isUnitLength = Method == DemandDistributionMethod.UnitLength;
        var flowUnits = FlowUnits ?? "(no unit selected)";
        report.AppendLine($"Total demand: {FormatNumber(TotalDemand)} {flowUnits}");

        foreach (var zone in Zones)
        {
            var halfModifier = isUnitLength ? "K = 0.5 pipes" : "50% junctions";
            var zeroModifier = isUnitLength ? "K = 0 pipes" : "0% junctions";
            report.AppendLine(
                $"{zone.Name}: demand {FormatNumber(zone.Demand)} {flowUnits} | " +
                $"Junctions ({zone.Junctions.Count}): {FormatIds(zone.Junctions)} | " +
                $"{halfModifier}: {FormatIds(zone.KHalfPipes)} | {zeroModifier}: {FormatIds(zone.KZeroPipes)}");
        }

        report.AppendLine();
        report.AppendLine("Validation summary");
        report.AppendLine(IsValid
            ? "Status: passed. No demands have been calculated or written."
            : $"Status: {Errors.Count} issue(s) need correction.");
        if (!IsValid)
        {
            report.AppendLine($"Issues to fix ({Errors.Count})");
            var remainingErrors = Errors.ToList();

            // Group messages by zone first; keep unmatched messages in a network-wide section.
            foreach (var zone in Zones)
            {
                var zoneErrors = remainingErrors
                    .Where(error => IsZoneIssue(error, zone.Name))
                    .ToArray();
                if (zoneErrors.Length == 0)
                {
                    continue;
                }

                report.AppendLine($"{zone.Name}:");
                foreach (var error in zoneErrors)
                {
                    report.AppendLine($"- {error}");
                    remainingErrors.Remove(error);
                }
            }

            if (remainingErrors.Count > 0)
            {
                report.AppendLine("Network-wide or cross-zone:");
                foreach (var error in remainingErrors)
                {
                    report.AppendLine($"- {error}");
                }
            }
        }

        if (Warnings.Count > 0)
        {
            report.AppendLine();
            report.AppendLine($"Warnings ({Warnings.Count})");
            foreach (var warning in Warnings)
            {
                report.AppendLine($"- {warning}");
            }
        }

        return report.ToString();
    }

    private static bool IsZoneIssue(string error, string zoneName) =>
        error.StartsWith($"{zoneName} ", StringComparison.OrdinalIgnoreCase)
        || error.StartsWith($"{zoneName}:", StringComparison.OrdinalIgnoreCase)
        || error.Contains($" under {zoneName}", StringComparison.OrdinalIgnoreCase)
        || error.Contains($" in {zoneName};", StringComparison.OrdinalIgnoreCase);

    private static string FormatNumber(double? value) =>
        value?.ToString("F4", CultureInfo.InvariantCulture) ?? "invalid";

    internal static string FormatIds(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return "none";
        }

        var formattedIds = new List<string>();
        var index = 0;
        while (index < ids.Count)
        {
            // Compress only consecutive numeric IDs that share an identical prefix.
            var startMatch = Regex.Match(ids[index], "^(?<prefix>.*?)(?<number>\\d+)$");
            if (!startMatch.Success
                || !int.TryParse(startMatch.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var startNumber))
            {
                formattedIds.Add(ids[index++]);
                continue;
            }

            var prefix = startMatch.Groups["prefix"].Value;
            var endIndex = index;
            var previousNumber = startNumber;
            while (endIndex + 1 < ids.Count)
            {
                var nextMatch = Regex.Match(ids[endIndex + 1], "^(?<prefix>.*?)(?<number>\\d+)$");
                if (!nextMatch.Success
                    || !prefix.Equals(nextMatch.Groups["prefix"].Value, StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(nextMatch.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var nextNumber)
                    || (long)nextNumber != (long)previousNumber + 1)
                {
                    break;
                }

                endIndex++;
                previousNumber = nextNumber;
            }

            formattedIds.Add(endIndex == index ? ids[index] : $"{ids[index]}-{ids[endIndex]}");
            index = endIndex + 1;
        }

        return string.Join(", ", formattedIds);
    }
}

public static class DemandInputValidator
{
    public const double DemandMismatchToleranceFraction = 0.001;
    private static readonly Regex ZoneNamePattern = new("^[A-Za-z][A-Za-z0-9_-]{0,30}$", RegexOptions.Compiled);
    private static readonly Regex RangePattern = new("^(?<startPrefix>.*?)(?<startNumber>\\d+)-(?<endPrefix>.*?)(?<endNumber>\\d+)$", RegexOptions.Compiled);
    private static readonly string[] FlowUnits = ["CFS", "GPM", "MGD", "IMGD", "AFD", "LPS", "LPM", "MLD", "CMH", "CMD"];

    /// <summary>
    /// Parses and checks network-wide and zone-specific input without calculating or writing demands.
    /// </summary>
    public static DemandValidationResult Validate(
        EpanetNetworkSummary network,
        DemandDistributionMethod method,
        string? totalDemandText,
        string? flowUnits,
        IReadOnlyList<ZoneInstructionDraft> drafts)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var parsedZones = new List<ParsedZoneInstruction>();
        var junctionIds = network.JunctionIds.ToDictionary(id => id, StringComparer.OrdinalIgnoreCase);
        var pipesById = network.Pipes.ToDictionary(pipe => pipe.Id, StringComparer.OrdinalIgnoreCase);
        var pipeIds = pipesById.Keys.ToDictionary(id => id, StringComparer.OrdinalIgnoreCase);
        var modifierIds = method == DemandDistributionMethod.UnitLength ? pipeIds : junctionIds;
        var isUnitLength = method == DemandDistributionMethod.UnitLength;
        var zoneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var junctionOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pipeAssignments = new Dictionary<string, (double K, string Zone)>(StringComparer.OrdinalIgnoreCase);
        var totalDemand = ParseNonNegativeNumber(totalDemandText, "Total demand", errors);

        // The selected units must be supported and must agree with the source network when it declares units.
        if (flowUnits is null || !FlowUnits.Contains(flowUnits, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("Select a valid EPANET flow unit.");
        }
        else if (network.FlowUnits is not null && !flowUnits.Equals(network.FlowUnits, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Selected flow unit '{flowUnits}' does not match the input network's flow unit '{network.FlowUnits}'.");
        }

        if (drafts.Count == 0)
        {
            errors.Add("At least one zone is required.");
        }

        var parsedDemands = new List<double>();
        foreach (var draft in drafts)
        {
            // Validate and normalize each row while accumulating ownership and modifier conflicts.
            var fallbackName = $"Zone row {parsedZones.Count + 1}";
            var zoneName = draft.Name?.Trim() ?? string.Empty;
            if (!ZoneNamePattern.IsMatch(zoneName))
            {
                errors.Add($"{fallbackName}: name must start with a letter and contain only letters, numbers, underscores, or hyphens (up to 31 characters).");
                zoneName = fallbackName;
            }
            else if (!zoneNames.Add(zoneName))
            {
                errors.Add($"Zone name '{zoneName}' is repeated.");
            }

            var zoneLabel = zoneName;
            var zoneDemand = ParseNonNegativeNumber(draft.Demand, $"{zoneLabel} demand", errors);
            if (zoneDemand.HasValue)
            {
                parsedDemands.Add(zoneDemand.Value);
            }

            var zoneJunctions = ParseIdList(
                draft.Junctions,
                $"{zoneLabel} junctions",
                junctionIds,
                required: true,
                errors);
            foreach (var junctionId in zoneJunctions)
            {
                if (!junctionOwners.TryAdd(junctionId, zoneLabel))
                {
                    errors.Add($"Junction '{junctionId}' is listed more than once, in {junctionOwners[junctionId]} and {zoneLabel}.");
                }
            }

            var kHalfPipes = ParseIdList(
                draft.KHalfPipes,
                isUnitLength ? $"{zoneLabel} K = 0.5 pipes" : $"{zoneLabel} 50% junctions",
                modifierIds,
                required: false,
                errors);
            var kZeroPipes = ParseIdList(
                draft.KZeroPipes,
                isUnitLength ? $"{zoneLabel} K = 0 pipes" : $"{zoneLabel} 0% junctions",
                modifierIds,
                required: false,
                errors);

            var halfPipeSet = new HashSet<string>(kHalfPipes, StringComparer.OrdinalIgnoreCase);
            foreach (var pipeId in kZeroPipes)
            {
                if (halfPipeSet.Contains(pipeId))
                {
                    errors.Add(isUnitLength
                        ? $"Pipe '{pipeId}' in {zoneLabel} is assigned both K = 0.5 and K = 0."
                        : $"Junction '{pipeId}' in {zoneLabel} is assigned both 50% and 0% demand.");
                }
            }

            if (isUnitLength)
            {
                AddPipeAssignments(kHalfPipes, 0.5, zoneLabel, pipeAssignments, errors);
                AddPipeAssignments(kZeroPipes, 0.0, zoneLabel, pipeAssignments, errors);
            }

            parsedZones.Add(new ParsedZoneInstruction(
                zoneLabel,
                zoneDemand,
                zoneJunctions,
                kHalfPipes,
                kZeroPipes));
        }

        if (parsedDemands.Count == drafts.Count && totalDemand.HasValue)
        {
            // A mismatch beyond 0.1% is an error; smaller non-rounding differences are shown as warnings.
            var zoneDemandTotal = parsedDemands.Sum();
            var difference = Math.Abs(zoneDemandTotal - totalDemand.Value);
            var tolerance = Math.Abs(totalDemand.Value) * DemandMismatchToleranceFraction;
            var roundingTolerance = Math.Max(1.0, Math.Abs(totalDemand.Value)) * 1e-9;
            if (difference > tolerance)
            {
                errors.Add($"Zone demands total {zoneDemandTotal.ToString("G", CultureInfo.InvariantCulture)}, but total demand is {totalDemand.Value.ToString("G", CultureInfo.InvariantCulture)}.");
            }
            else if (difference > roundingTolerance)
            {
                warnings.Add(
                    $"Zone totals differ from the Network total by {difference.ToString("F3", CultureInfo.InvariantCulture)} {flowUnits ?? "unknown"}.");
            }
        }

        foreach (var zone in parsedZones)
        {
            // A modifier must refer to an element assigned to (or incident to) the zone where it is entered.
            foreach (var modifierId in zone.KHalfPipes.Concat(zone.KZeroPipes))
            {
                if (isUnitLength)
                {
                    var pipe = pipesById[modifierId];
                    var connectsToZone =
                        junctionOwners.TryGetValue(pipe.StartNodeId, out var startOwner)
                            && startOwner.Equals(zone.Name, StringComparison.OrdinalIgnoreCase)
                        || junctionOwners.TryGetValue(pipe.EndNodeId, out var endOwner)
                            && endOwner.Equals(zone.Name, StringComparison.OrdinalIgnoreCase);

                    if (!connectsToZone)
                    {
                        errors.Add($"Pipe '{modifierId}' is listed under {zone.Name}, but neither endpoint junction belongs to that zone.");
                    }
                }
                else if (!junctionOwners.TryGetValue(modifierId, out var owner)
                    || !owner.Equals(zone.Name, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Junction '{modifierId}' is listed as a demand modifier under {zone.Name}, but it is not assigned to that zone.");
                }
            }

            if (isUnitLength && zone.Demand.GetValueOrDefault() > 0)
            {
                // Mirror the distributor's endpoint split to reject zones that cannot receive positive weight.
                var totalPipeWeight = 0.0;
                foreach (var pipe in network.Pipes)
                {
                    var k = pipeAssignments.TryGetValue(pipe.Id, out var assignment) ? assignment.K : 1.0;
                    var effectiveLength = pipe.Length * k;
                    var hasStartJunction = junctionOwners.TryGetValue(pipe.StartNodeId, out var startOwner);
                    var hasEndJunction = junctionOwners.TryGetValue(pipe.EndNodeId, out var endOwner);
                    var endpointWeight = hasStartJunction && hasEndJunction
                        ? effectiveLength / 2.0
                        : effectiveLength;

                    if (hasStartJunction && startOwner!.Equals(zone.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        totalPipeWeight += endpointWeight;
                    }

                    if (hasEndJunction && endOwner!.Equals(zone.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        totalPipeWeight += endpointWeight;
                    }
                }

                if (!double.IsFinite(totalPipeWeight))
                {
                    errors.Add($"{zone.Name} has a non-finite total K-weighted pipe length.");
                }
                else if (totalPipeWeight <= 0)
                {
                    errors.Add($"{zone.Name} has positive demand, but no positive K-weighted pipe length.");
                }
            }

            if (!isUnitLength
                && zone.Demand.GetValueOrDefault() > 0
                && zone.Junctions.Count > 0
                && zone.Junctions.All(id => zone.KZeroPipes.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                errors.Add($"{zone.Name} has positive demand, but every junction is assigned 0% demand.");
            }
        }

        var omittedJunctions = network.JunctionIds.Where(id => !junctionOwners.ContainsKey(id)).ToArray();
        if (omittedJunctions.Length > 0)
        {
            errors.Add($"Junctions omitted from all zones: {DemandValidationResult.FormatIds(omittedJunctions)}.");
        }

        return new DemandValidationResult(method, totalDemand, flowUnits, parsedZones, errors, warnings);
    }

    private static double? ParseNonNegativeNumber(string? text, string fieldName, ICollection<string> errors)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            errors.Add($"{fieldName} must be a number.");
            return null;
        }

        if (!double.IsFinite(value) || value < 0)
        {
            errors.Add($"{fieldName} must be a finite, non-negative number.");
            return null;
        }

        return value;
    }

    private static IReadOnlyList<string> ParseIdList(
        string? text,
        string fieldName,
        IReadOnlyDictionary<string, string> availableIds,
        bool required,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (required)
            {
                errors.Add($"{fieldName} is required.");
            }

            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Commas separate IDs/ranges; an empty token is an error rather than something to silently ignore.
        var tokens = text.Split(',', StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (token.Length == 0)
            {
                errors.Add($"{fieldName} contains an empty item.");
                continue;
            }

            if (!TryExpandToken(token, availableIds.Count, out var expandedIds, out var rangeError))
            {
                errors.Add($"{fieldName}: {rangeError}");
                continue;
            }

            foreach (var expandedId in expandedIds)
            {
                if (!availableIds.TryGetValue(expandedId, out var canonicalId))
                {
                    errors.Add($"{fieldName} refers to unknown ID '{expandedId}'.");
                    continue;
                }

                if (!seen.Add(canonicalId))
                {
                    errors.Add($"{fieldName} repeats ID '{canonicalId}'.");
                    continue;
                }

                result.Add(canonicalId);
            }
        }

        return result;
    }

    private static bool TryExpandToken(string token, int maximumCount, out IReadOnlyList<string> ids, out string error)
    {
        var match = RangePattern.Match(token);
        if (!match.Success)
        {
            ids = [token];
            error = string.Empty;
            return true;
        }

        var startPrefix = match.Groups["startPrefix"].Value;
        var endPrefix = match.Groups["endPrefix"].Value;
        if (!startPrefix.Equals(endPrefix, StringComparison.OrdinalIgnoreCase))
        {
            ids = [];
            error = $"range '{token}' must use the same prefix at both ends.";
            return false;
        }

        if (!int.TryParse(match.Groups["startNumber"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !int.TryParse(match.Groups["endNumber"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var end))
        {
            ids = [];
            error = $"range '{token}' contains an invalid number.";
            return false;
        }

        if (end < start)
        {
            ids = [];
            error = $"range '{token}' ends before it starts.";
            return false;
        }

        var count = (long)end - start + 1;
        if (count > Math.Max(maximumCount, 1))
        {
            ids = [];
            error = $"range '{token}' expands beyond the number of items in the network.";
            return false;
        }

        var width = match.Groups["startNumber"].Length;
        // Retain zero padding from the first ID (for example, J01-J03 expands to J01, J02, J03).
        ids = Enumerable.Range(start, (int)count)
            .Select(number => $"{startPrefix}{number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}")
            .ToArray();
        error = string.Empty;
        return true;
    }

    private static void AddPipeAssignments(
        IEnumerable<string> pipeIds,
        double k,
        string zone,
        IDictionary<string, (double K, string Zone)> assignments,
        ICollection<string> errors)
    {
        foreach (var pipeId in pipeIds)
        {
            // A pipe can have one K value only, even when it connects two different zones.
            if (assignments.TryGetValue(pipeId, out var existing))
            {
                errors.Add($"Pipe '{pipeId}' has a repeated K assignment in {zone}; it is already assigned K = {existing.K.ToString("G", CultureInfo.InvariantCulture)} in {existing.Zone}.");
                continue;
            }

            assignments.Add(pipeId, (k, zone));
        }
    }
}