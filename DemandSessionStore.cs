using System.Text.Json;

namespace Demand_Gen;

public sealed record DemandSession(
    string NetworkPath,
    string? Method,
    string? TotalDemand,
    string? FlowUnits,
    int ZoneCount,
    IReadOnlyList<ZoneInstructionDraft> Zones);

// Serializes the user's editable inputs; the network file itself remains at its original path.
public static class DemandSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static void Save(string filePath, DemandSession session)
    {
        // Indented JSON is easier to inspect or troubleshoot than a compact one-line document.
        File.WriteAllText(filePath, JsonSerializer.Serialize(session, JsonOptions));
    }

    public static DemandSession Load(string filePath)
    {
        // Reject malformed or incomplete sessions before the UI applies any saved values.
        var session = JsonSerializer.Deserialize<DemandSession>(File.ReadAllText(filePath), JsonOptions)
            ?? throw new InvalidDataException("The session file is empty or invalid.");

        if (string.IsNullOrWhiteSpace(session.NetworkPath))
        {
            throw new InvalidDataException("The session does not contain an input network path.");
        }

        if (session.ZoneCount is < 1 or > 100
            || session.Zones is null
            || session.Zones.Count != session.ZoneCount
            || session.Zones.Any(zone => zone is null))
        {
            throw new InvalidDataException("The session contains an invalid number of zone rows.");
        }

        return session;
    }
}