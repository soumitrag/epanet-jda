using System.Globalization;

namespace Demand_Gen;

public sealed record EpanetPipe(string Id, string StartNodeId, string EndNodeId, double Length);

public sealed class EpanetNetworkSummary
{
    internal EpanetNetworkSummary(
        IReadOnlyList<string> junctionIds,
        IReadOnlyList<string> reservoirIds,
        IReadOnlyList<string> tankIds,
        IReadOnlyList<EpanetPipe> pipes,
        int pumpCount,
        int valveCount,
        string? flowUnits)
    {
        JunctionIds = junctionIds;
        ReservoirIds = reservoirIds;
        TankIds = tankIds;
        Pipes = pipes;
        PumpCount = pumpCount;
        ValveCount = valveCount;
        FlowUnits = flowUnits;
    }

    public IReadOnlyList<string> JunctionIds { get; }
    public IReadOnlyList<string> ReservoirIds { get; }
    public IReadOnlyList<string> TankIds { get; }
    public IReadOnlyList<EpanetPipe> Pipes { get; }
    public int PumpCount { get; }
    public int ValveCount { get; }
    public string? FlowUnits { get; }
}

public static class EpanetNetworkReader
{
    /// <summary>
    /// Reads the node, pipe, pump, valve, and flow-unit data needed by the allocator.
    /// </summary>
    public static EpanetNetworkSummary Read(string filePath)
    {
        var junctionIds = new List<string>();
        var reservoirIds = new List<string>();
        var tankIds = new List<string>();
        var pipes = new List<EpanetPipe>();
        var links = new List<(string Id, string StartNodeId, string EndNodeId)>();
        var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pumpCount = 0;
        var valveCount = 0;
        string? flowUnits = null;
        var section = string.Empty;
        var lines = File.ReadAllLines(filePath);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            // Blank lines and full-line comments do not contribute network records.
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.Contains(']'))
            {
                section = line[1..line.IndexOf(']')].Trim().ToUpperInvariant();
                continue;
            }

            // Strip inline comments before splitting the EPANET data fields.
            var commentIndex = line.IndexOf(';');
            if (commentIndex >= 0)
            {
                line = line[..commentIndex].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var lineNumber = index + 1;

            // Only parse the network sections needed for summaries and demand allocation.
            switch (section)
            {
                case "JUNCTIONS":
                    RequireFields(fields, 2, section, lineNumber);
                    AddUnique(fields[0], junctionIds, nodeIds, section, lineNumber);
                    break;
                case "RESERVOIRS":
                    RequireFields(fields, 2, section, lineNumber);
                    AddUnique(fields[0], reservoirIds, nodeIds, section, lineNumber);
                    break;
                case "TANKS":
                    RequireFields(fields, 6, section, lineNumber);
                    AddUnique(fields[0], tankIds, nodeIds, section, lineNumber);
                    break;
                case "PIPES":
                    RequireFields(fields, 6, section, lineNumber);
                    AddLink(fields[0], fields[1], fields[2], links, linkIds, section, lineNumber);
                    if (!double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var length)
                        || !double.IsFinite(length)
                        || length < 0)
                    {
                        throw InvalidRecord(section, lineNumber, "pipe length must be a non-negative number");
                    }

                    pipes.Add(new EpanetPipe(fields[0], fields[1], fields[2], length));
                    break;
                case "PUMPS":
                    RequireFields(fields, 4, section, lineNumber);
                    if (!fields.Skip(3).Any(field =>
                            field.Equals("POWER", StringComparison.OrdinalIgnoreCase)
                            || field.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw InvalidRecord(section, lineNumber, "pump must specify POWER or HEAD");
                    }

                    AddLink(fields[0], fields[1], fields[2], links, linkIds, section, lineNumber);
                    pumpCount++;
                    break;
                case "VALVES":
                    RequireFields(fields, 7, section, lineNumber);
                    AddLink(fields[0], fields[1], fields[2], links, linkIds, section, lineNumber);
                    valveCount++;
                    break;
                case "OPTIONS":
                    if (fields.Length >= 2 && fields[0].Equals("UNITS", StringComparison.OrdinalIgnoreCase))
                    {
                        flowUnits = fields[1];
                    }

                    break;
            }
        }

        if (junctionIds.Count == 0)
        {
            throw new InvalidDataException("The input file does not contain any junctions in its [JUNCTIONS] section.");
        }

        // Check every encountered link after all node sections have been read.
        foreach (var link in links)
        {
            if (!nodeIds.Contains(link.StartNodeId) || !nodeIds.Contains(link.EndNodeId))
            {
                throw new InvalidDataException(
                    $"Link '{link.Id}' refers to an undefined node ('{link.StartNodeId}' or '{link.EndNodeId}').");
            }
        }

        return new EpanetNetworkSummary(
            junctionIds.ToArray(),
            reservoirIds.ToArray(),
            tankIds.ToArray(),
            pipes.ToArray(),
            pumpCount,
            valveCount,
            flowUnits);
    }

    private static void AddUnique(
        string id,
        ICollection<string> ids,
        ISet<string> allIds,
        string section,
        int lineNumber)
    {
        // Node IDs share one namespace, so duplicates across node types are rejected as well.
        if (!allIds.Add(id))
        {
            throw InvalidRecord(section, lineNumber, $"duplicate node ID '{id}'");
        }

        ids.Add(id);
    }

    private static void AddLink(
        string id,
        string startNodeId,
        string endNodeId,
        ICollection<(string Id, string StartNodeId, string EndNodeId)> links,
        ISet<string> allIds,
        string section,
        int lineNumber)
    {
        // Link IDs are unique across pipes, pumps, and valves.
        if (!allIds.Add(id))
        {
            throw InvalidRecord(section, lineNumber, $"duplicate link ID '{id}'");
        }

        links.Add((id, startNodeId, endNodeId));
    }

    private static void RequireFields(string[] fields, int count, string section, int lineNumber)
    {
        if (fields.Length < count)
        {
            throw InvalidRecord(section, lineNumber, $"expected at least {count} fields but found {fields.Length}");
        }
    }

    private static InvalidDataException InvalidRecord(string section, int lineNumber, string message) =>
        new($"Invalid record in [{section}] on line {lineNumber}: {message}.");
}