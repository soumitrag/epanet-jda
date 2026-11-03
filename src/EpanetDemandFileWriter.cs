using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Demand_Gen;

public static class EpanetDemandFileWriter
{
    private static readonly Regex TokenPattern = new("\\S+", RegexOptions.Compiled);
    private static readonly Regex LineBreakPattern = new("\\r\\n|\\n|\\r", RegexOptions.Compiled);

    /// <summary>
    /// Creates a new input copy, then rereads it and verifies each junction demand before publishing it.
    /// </summary>
    public static string WriteVerifiedCopy(string sourcePath, DemandDistributionResult distribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(distribution);

        var sourceFullPath = Path.GetFullPath(sourcePath);
        var outputPath = GetAvailableOutputPath(sourceFullPath, distribution.Method);
        var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        var expectedDemands = CreateDemandMap(distribution);

        try
        {
            // Write to a unique temporary file so a failed update never leaves a partial final output.
            var source = ReadSourceText(sourceFullPath);
            var outputText = ReplaceDemands(source.Content, expectedDemands);
            WriteText(temporaryPath, outputText, source.Encoding, source.HasPreamble);

            // Validate the generated file structure and values before moving it to its final name.
            EpanetNetworkReader.Read(temporaryPath);
            VerifyDemands(temporaryPath, expectedDemands);
            File.Move(temporaryPath, outputPath);
            return outputPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static Dictionary<string, double> CreateDemandMap(DemandDistributionResult distribution)
    {
        // Build a case-insensitive lookup and reject duplicate or invalid allocations before editing text.
        var demands = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var junction in distribution.Zones.SelectMany(zone => zone.Junctions))
        {
            if (!double.IsFinite(junction.Demand) || junction.Demand < 0)
            {
                throw new InvalidDataException($"Junction '{junction.JunctionId}' has an invalid calculated demand.");
            }

            if (!demands.TryAdd(junction.JunctionId, junction.Demand))
            {
                throw new InvalidDataException($"Junction '{junction.JunctionId}' has more than one calculated demand.");
            }
        }

        if (demands.Count == 0)
        {
            throw new InvalidDataException("The calculation contains no junction demands to write.");
        }

        return demands;
    }

    private static string ReplaceDemands(string content, IReadOnlyDictionary<string, double> expectedDemands)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : content.Contains('\n') ? "\n" : "\r";
        var lines = LineBreakPattern.Split(content);
        var outputLines = new List<string>(lines.Length);
        var updatedJunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;

        foreach (var originalLine in lines)
        {
            var trimmedLine = originalLine.Trim();
            if (trimmedLine.StartsWith('[') && trimmedLine.Contains(']'))
            {
                section = trimmedLine[1..trimmedLine.IndexOf(']')].Trim().ToUpperInvariant();
                outputLines.Add(originalLine);
                continue;
            }

            if (section.Equals("DEMANDS", StringComparison.OrdinalIgnoreCase)
                && trimmedLine.Length > 0
                && !trimmedLine.StartsWith(';'))
            {
                // Remove active extra demands to prevent them being added on top of the new base demands.
                // Keep inline comments as standalone lines so user notes are not lost.
                var commentIndex = originalLine.IndexOf(';');
                if (commentIndex >= 0)
                {
                    outputLines.Add(originalLine[commentIndex..]);
                }

                continue;
            }

            if (!section.Equals("JUNCTIONS", StringComparison.OrdinalIgnoreCase)
                || trimmedLine.Length == 0
                || trimmedLine.StartsWith(';'))
            {
                outputLines.Add(originalLine);
                continue;
            }

            var commentStart = originalLine.IndexOf(';');
            var dataLength = commentStart >= 0 ? commentStart : originalLine.Length;
            var tokens = TokenPattern.Matches(originalLine[..dataLength]);
            if (tokens.Count < 2)
            {
                throw new InvalidDataException("A junction record is missing its ID or elevation.");
            }

            var junctionId = tokens[0].Value;
            if (!expectedDemands.TryGetValue(junctionId, out var demand))
            {
                throw new InvalidDataException($"Junction '{junctionId}' has no calculated demand.");
            }

            var formattedDemand = demand.ToString("G17", CultureInfo.InvariantCulture);
            if (tokens.Count >= 3)
            {
                // Replace only the demand token; leave spacing, optional pattern, and inline comment intact.
                var demandToken = tokens[2];
                outputLines.Add(originalLine.Remove(demandToken.Index, demandToken.Length)
                    .Insert(demandToken.Index, formattedDemand));
            }
            else
            {
                // EPANET permits junctions with no base-demand field, so insert one after elevation.
                var elevationToken = tokens[1];
                outputLines.Add(originalLine.Insert(elevationToken.Index + elevationToken.Length, $" {formattedDemand}"));
            }

            updatedJunctions.Add(junctionId);
        }

        var missingJunctions = expectedDemands.Keys.Where(id => !updatedJunctions.Contains(id)).ToArray();
        if (missingJunctions.Length > 0)
        {
            throw new InvalidDataException(
                $"Calculated junctions were not found in the source [JUNCTIONS] section: {string.Join(", ", missingJunctions)}.");
        }

        return string.Join(newline, outputLines);
    }

    private static void VerifyDemands(string filePath, IReadOnlyDictionary<string, double> expectedDemands)
    {
        var actualDemands = ReadJunctionDemands(filePath);
        foreach (var (junctionId, expectedDemand) in expectedDemands)
        {
            // Allow only a tiny floating-point tolerance after writing with round-trip precision.
            if (!actualDemands.TryGetValue(junctionId, out var actualDemand)
                || Math.Abs(actualDemand - expectedDemand) > Math.Max(1.0, Math.Abs(expectedDemand)) * 1e-12)
            {
                throw new InvalidDataException($"Generated demand verification failed for junction '{junctionId}'.");
            }
        }
    }

    private static Dictionary<string, double> ReadJunctionDemands(string filePath)
    {
        var demands = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;

        foreach (var originalLine in File.ReadLines(filePath))
        {
            var trimmedLine = originalLine.Trim();
            if (trimmedLine.StartsWith('[') && trimmedLine.Contains(']'))
            {
                section = trimmedLine[1..trimmedLine.IndexOf(']')].Trim().ToUpperInvariant();
                continue;
            }

            if (!section.Equals("JUNCTIONS", StringComparison.OrdinalIgnoreCase)
                || trimmedLine.Length == 0
                || trimmedLine.StartsWith(';'))
            {
                continue;
            }

            var commentStart = originalLine.IndexOf(';');
            var dataLength = commentStart >= 0 ? commentStart : originalLine.Length;
            var tokens = TokenPattern.Matches(originalLine[..dataLength]);
            if (tokens.Count < 3
                || !double.TryParse(tokens[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var demand)
                || !double.IsFinite(demand)
                || !demands.TryAdd(tokens[0].Value, demand))
            {
                throw new InvalidDataException("The generated [JUNCTIONS] section contains an invalid or duplicate demand record.");
            }
        }

        return demands;
    }

    private static string GetAvailableOutputPath(string sourcePath, DemandDistributionMethod method)
    {
        var directory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidDataException("Could not determine the source network folder.");
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        var methodName = method switch
        {
            DemandDistributionMethod.UnitLength => "Unit-Length",
            DemandDistributionMethod.EqualDistribution => "Equal-Distribution",
            _ => throw new InvalidDataException("The selected distribution method is not supported.")
        };
        var sequence = 1;
        var candidate = Path.Combine(directory, $"{fileName}_{methodName}_{sequence}.inp");
        // Never overwrite an earlier generated file; advance the suffix until the name is free.
        while (File.Exists(candidate))
        {
            sequence++;
            candidate = Path.Combine(directory, $"{fileName}_{methodName}_{sequence}.inp");
        }

        return candidate;
    }

    private static SourceText ReadSourceText(string filePath)
    {
        // Detect encoding and byte-order mark so the generated copy retains the source text format.
        var bytes = File.ReadAllBytes(filePath);
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();
        var encoding = reader.CurrentEncoding;
        var preamble = encoding.GetPreamble();
        var hasPreamble = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble);
        return new SourceText(content, encoding, hasPreamble);
    }

    private static void WriteText(string filePath, string content, Encoding encoding, bool hasPreamble)
    {
        // CreateNew prevents an unexpected existing temporary file from being overwritten.
        using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (hasPreamble)
        {
            stream.Write(encoding.GetPreamble());
        }

        stream.Write(encoding.GetBytes(content));
    }

    private sealed record SourceText(string Content, Encoding Encoding, bool HasPreamble);
}