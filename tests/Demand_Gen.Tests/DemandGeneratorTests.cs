using Xunit;

namespace Demand_Gen.Tests;

// Exercises parsing, validation, distribution, and file output using isolated temporary files.
public sealed class DemandGeneratorTests : IDisposable
{
    // This fixture covers junction-junction and reservoir/tank pipes plus active secondary demands.
    private const string ValidNetwork = """
        [OPTIONS]
        UNITS LPS
        [JUNCTIONS]
        J1 0 5
        J2 0 6
        J3 0 7
        [RESERVOIRS]
        R1 10
        [TANKS]
        T1 0 10 0 20 10
        [PIPES]
        P1 J1 J2 100 100 0 OPEN
        P2 J2 R1 200 100 0 OPEN
        P3 J3 T1 50 100 0 OPEN
        [DEMANDS]
        J1 99
        ; preserve this comment
        [PATTERNS]
        PAT1 1.0 1.1
        [END]
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Demand_Gen.Tests", Guid.NewGuid().ToString("N"));

    public DemandGeneratorTests()
    {
        // Give each test instance its own folder so output names cannot collide between tests.
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void NetworkReaderParsesCountsAndPipeLengths()
    {
        var network = ReadNetwork();

        Assert.Equal(3, network.JunctionIds.Count);
        Assert.Single(network.ReservoirIds);
        Assert.Single(network.TankIds);
        Assert.Equal(3, network.Pipes.Count);
        Assert.Equal(350, network.Pipes.Sum(pipe => pipe.Length));
        Assert.Equal("LPS", network.FlowUnits);
    }

    [Fact]
    public void NetworkReaderRejectsUndefinedLinkEndpoint()
    {
        var filePath = WriteNetwork(ValidNetwork.Replace("P1 J1 J2", "P1 J1 UNKNOWN", StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidDataException>(() => EpanetNetworkReader.Read(filePath));

        Assert.Contains("undefined node", exception.Message);
    }

    [Fact]
    public void ValidatorAcceptsCompleteZoneCoverageAndRanges()
    {
        var result = Validate(zones: [Zone("Z1", "100", "J1-J3")]);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(new[] { "J1", "J2", "J3" }, result.Zones[0].Junctions);
    }

    [Fact]
    public void ValidationReportCompressesJunctionRangeAcrossDigitWidth()
    {
        var network = EpanetNetworkReader.Read(WriteNetwork("""
            [OPTIONS]
            UNITS LPS
            [JUNCTIONS]
            J6 0 5
            J7 0 6
            J8 0 7
            J9 0 8
            J10 0 9
            [PIPES]
            P6 J6 J7 100 100 0 OPEN
            P7 J7 J8 100 100 0 OPEN
            P8 J8 J9 100 100 0 OPEN
            P9 J9 J10 100 100 0 OPEN
            [END]
            """));

        var result = DemandInputValidator.Validate(
            network,
            DemandDistributionMethod.UnitLength,
            "20",
            "LPS",
            [Zone("Z2", "20", "J6-J10")]);

        Assert.Contains("Junctions (5): J6-J10", result.BuildReport(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void ValidatorRejectsInvalidDemandValues(string demand)
    {
        var result = Validate(totalDemand: demand, zones: [Zone("Z1", demand, "J1-J3")]);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidatorRejectsUnsupportedAndMismatchedFlowUnits()
    {
        var unsupported = Validate(flowUnits: "XYZ");
        var mismatched = Validate(flowUnits: "GPM");

        Assert.Contains(unsupported.Errors, error => error.Contains("valid EPANET flow unit", StringComparison.Ordinal));
        Assert.Contains(mismatched.Errors, error => error.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsInvalidZoneNamesAndDuplicateJunctionAssignments()
    {
        var invalidName = Validate(zones: [Zone("1-zone", "100", "J1-J3")]);
        var duplicateJunction = Validate(zones:
        [
            Zone("Z1", "50", "J1-J2"),
            Zone("Z2", "50", "J2-J3")
        ]);

        Assert.Contains(invalidName.Errors, error => error.Contains("name must start with a letter", StringComparison.Ordinal));
        Assert.Contains(duplicateJunction.Errors, error => error.Contains("listed more than once", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("J4", "unknown ID")]
    [InlineData("J3-J1", "ends before it starts")]
    [InlineData("J1,,J2-J3", "empty item")]
    public void ValidatorRejectsMalformedOrUnknownJunctionLists(string junctions, string expectedError)
    {
        var result = Validate(zones: [Zone("Z1", "100", junctions)]);

        Assert.Contains(result.Errors, error => error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatorReportsSmallDemandDifferenceAsAbsoluteWarning()
    {
        var result = Validate(totalDemand: "100", zones: [Zone("Z1", "100.001", "J1-J3")]);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("Zone totals differ from the Network total by 0.001 LPS.", Assert.Single(result.Warnings));
    }

    [Fact]
    public void ValidatorRejectsDemandDifferenceOutsideTolerance()
    {
        var result = Validate(totalDemand: "100", zones: [Zone("Z1", "100.2", "J1-J3")]);

        Assert.False(result.IsValid);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ValidatorRejectsUnitLengthZoneWithNoPositiveEffectivePipeWeight()
    {
        var result = Validate(zones:
        [
            Zone("Z1", "100", "J1-J3", kZeroPipes: "P1-P3")
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("no positive K-weighted pipe length", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorAllowsZeroDemandZoneWithNoEffectivePipeWeight()
    {
        var result = Validate(
            totalDemand: "0",
            zones: [Zone("Z1", "0", "J1-J3", kZeroPipes: "P1-P3")]);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidatorRejectsUnitLengthPipeModifierOutsideItsZone()
    {
        var result = Validate(zones:
        [
            Zone("Z1", "40", "J1", kHalfPipes: "P3"),
            Zone("Z2", "60", "J2-J3")
        ]);

        Assert.Contains(result.Errors, error => error.Contains("neither endpoint junction belongs to that zone", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsConflictingUnitLengthKFactors()
    {
        var result = Validate(zones:
        [
            Zone("Z1", "100", "J1-J3", "P1", "P1")
        ]);

        Assert.Contains(result.Errors, error => error.Contains("both K = 0.5 and K = 0", StringComparison.Ordinal));
    }

    [Fact]
    public void UnitLengthSplitsPipeLengthAndAllowsZeroWeightJunctionWhenZoneHasOtherWeight()
    {
        var network = ReadNetwork();
        var validation = Validate(zones: [Zone("Z1", "100", "J1-J3", "P1", "P3")]);
        var result = UnitLengthDemandDistributor.Calculate(network, validation);
        var junctions = result.Zones[0].Junctions.ToDictionary(item => item.JunctionId);

        Assert.Equal(25, junctions["J1"].AllocationWeight);
        Assert.Equal(225, junctions["J2"].AllocationWeight);
        Assert.Equal(0, junctions["J3"].AllocationWeight);
        Assert.Equal(100, result.Zones[0].Junctions.Sum(item => item.Demand), 10);
        Assert.Equal(0, junctions["J3"].Demand);
    }

    [Fact]
    public void UnitLengthSplitsCrossZonePipesAndUsesFullWeightAtReservoirAndTankEndpoints()
    {
        var network = ReadNetwork();
        var validation = Validate(zones:
        [
            Zone("Z1", "40", "J1"),
            Zone("Z2", "60", "J2-J3")
        ]);
        var result = UnitLengthDemandDistributor.Calculate(network, validation);
        var junctions = result.Zones.SelectMany(zone => zone.Junctions).ToDictionary(item => item.JunctionId);

        Assert.Equal(50, junctions["J1"].AllocationWeight);
        Assert.Equal(250, junctions["J2"].AllocationWeight);
        Assert.Equal(50, junctions["J3"].AllocationWeight);
        Assert.Equal(40, junctions["J1"].Demand, 10);
        Assert.Equal(50, junctions["J2"].Demand, 10);
        Assert.Equal(10, junctions["J3"].Demand, 10);
    }

    [Fact]
    public void EqualDistributionAppliesHalfAndZeroWeights()
    {
        var validation = Validate(
            method: DemandDistributionMethod.EqualDistribution,
            totalDemand: "60",
            zones: [Zone("Z1", "60", "J1-J3", "J1", "J3")]);
        var result = EqualDistributionDemandDistributor.Calculate(validation);
        var junctions = result.Zones[0].Junctions.ToDictionary(item => item.JunctionId);

        Assert.Equal(20, junctions["J1"].Demand, 10);
        Assert.Equal(40, junctions["J2"].Demand, 10);
        Assert.Equal(0, junctions["J3"].Demand);
    }

    [Fact]
    public void ValidatorRejectsEqualDistributionPositiveDemandWhenEveryJunctionIsZeroPercent()
    {
        var result = Validate(
            method: DemandDistributionMethod.EqualDistribution,
            zones: [Zone("Z1", "60", "J1-J3", kZeroPipes: "J1-J3")]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("every junction is assigned 0%", StringComparison.Ordinal));
    }

    [Fact]
    public void FileWriterReplacesDemandsRemovesActiveDemandRowsAndPreservesSource()
    {
        var sourcePath = WriteNetwork(ValidNetwork);
        var sourceBefore = File.ReadAllText(sourcePath);
        var network = EpanetNetworkReader.Read(sourcePath);
        var validation = Validate(zones: [Zone("Z1", "100", "J1-J3")]);
        var distribution = UnitLengthDemandDistributor.Calculate(network, validation);

        var generatedPath = EpanetDemandFileWriter.WriteVerifiedCopy(sourcePath, distribution);
        var generatedText = File.ReadAllText(generatedPath);

        Assert.Equal(sourceBefore, File.ReadAllText(sourcePath));
        Assert.EndsWith("network_Unit-Length_1.inp", generatedPath, StringComparison.Ordinal);
        Assert.DoesNotContain("J1 99", generatedText, StringComparison.Ordinal);
        Assert.Contains("; preserve this comment", generatedText, StringComparison.Ordinal);
        Assert.Contains("[PATTERNS]", generatedText, StringComparison.Ordinal);
        Assert.Contains("PAT1 1.0 1.1", generatedText, StringComparison.Ordinal);
    }

    [Fact]
    public void FileWriterPreservesCrLfAndCommentsWhileRemovingAllActiveDemandRows()
    {
        var sourceText = ValidNetwork
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal)
            .Replace("J1 0 5", "J1 0 5 ; junction note", StringComparison.Ordinal)
            .Replace("J1 99\r\n; preserve this comment", "J1 99\r\nJ2 101 ; old demand note\r\n; preserve this comment", StringComparison.Ordinal)
            .Replace("[PATTERNS]", "\r\n[PATTERNS]", StringComparison.Ordinal);
        var sourcePath = WriteNetwork(sourceText);
        var network = EpanetNetworkReader.Read(sourcePath);
        var sourceBefore = File.ReadAllBytes(sourcePath);
        var validation = DemandInputValidator.Validate(
            network,
            DemandDistributionMethod.UnitLength,
            "100",
            "LPS",
            [Zone("Z1", "100", "J1-J3")]);
        var distribution = UnitLengthDemandDistributor.Calculate(network, validation);

        var generatedPath = EpanetDemandFileWriter.WriteVerifiedCopy(sourcePath, distribution);
        var generatedText = File.ReadAllText(generatedPath);

        Assert.Equal(sourceBefore, File.ReadAllBytes(sourcePath));
        Assert.DoesNotContain("J1 99", generatedText, StringComparison.Ordinal);
        Assert.DoesNotContain("J2 101", generatedText, StringComparison.Ordinal);
        Assert.Contains("; old demand note", generatedText, StringComparison.Ordinal);
        Assert.Contains("; preserve this comment", generatedText, StringComparison.Ordinal);
        Assert.Contains(" ; junction note", generatedText, StringComparison.Ordinal);
        Assert.Contains("\r\n\r\n[PATTERNS]\r\nPAT1 1.0 1.1", generatedText, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", generatedText.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void FileWriterUsesNextNameWhenOutputAlreadyExists()
    {
        var sourcePath = WriteNetwork(ValidNetwork);
        File.WriteAllText(Path.Combine(_directory, "network_Unit-Length_1.inp"), "existing output");
        var network = EpanetNetworkReader.Read(sourcePath);
        var validation = Validate(zones: [Zone("Z1", "100", "J1-J3")]);
        var distribution = UnitLengthDemandDistributor.Calculate(network, validation);

        var generatedPath = EpanetDemandFileWriter.WriteVerifiedCopy(sourcePath, distribution);

        Assert.EndsWith("network_Unit-Length_2.inp", generatedPath, StringComparison.Ordinal);
        Assert.Equal("existing output", File.ReadAllText(Path.Combine(_directory, "network_Unit-Length_1.inp")));
    }

    [Fact]
    public void TextReportIncludesNetworkAndFourDecimalResults()
    {
        var sourcePath = WriteNetwork(ValidNetwork);
        var network = EpanetNetworkReader.Read(sourcePath);
        var validation = Validate(zones: [Zone("Z1", "100", "J1-J3")]);
        var distribution = UnitLengthDemandDistributor.Calculate(network, validation);
        var generatedPath = EpanetDemandFileWriter.WriteVerifiedCopy(sourcePath, distribution);

        var reportPath = DemandGenerationReportWriter.Write(sourcePath, generatedPath, network, validation, distribution);
        var report = File.ReadAllText(reportPath);

        Assert.Contains("Network name: network", report, StringComparison.Ordinal);
        Assert.Contains("Total pipeline length: 350.0000 m", report, StringComparison.Ordinal);
        Assert.Contains("Total demand: 100.0000 LPS", report, StringComparison.Ordinal);
        Assert.Contains("J1 | 14.2857 LPS", report, StringComparison.Ordinal);
        Assert.Contains("Output verification", report, StringComparison.Ordinal);
        Assert.Contains("Junction base demands verified: 3 of 3", report, StringComparison.Ordinal);
        Assert.Contains("Allocation difference (allocated - entered): 0.0000 LPS", report, StringComparison.Ordinal);
        Assert.EndsWith("network_Unit-Length_1.txt", reportPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationReportGroupsZoneIssuesAndSeparatesNetworkIssues()
    {
        var result = Validate(totalDemand: "invalid", zones: [Zone("Z1", "invalid", "J1-J3")]);

        var report = result.BuildReport();

        Assert.Contains("Validation summary", report, StringComparison.Ordinal);
        Assert.Contains("Status: 2 issue(s) need correction.", report, StringComparison.Ordinal);
        Assert.Contains("Z1:", report, StringComparison.Ordinal);
        Assert.Contains("Z1 demand must be a number.", report, StringComparison.Ordinal);
        Assert.Contains("Network-wide or cross-zone:", report, StringComparison.Ordinal);
        Assert.Contains("Total demand must be a number.", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TextReportUsesNextNameWithoutOverwritingExistingReport()
    {
        var sourcePath = WriteNetwork(ValidNetwork);
        var network = EpanetNetworkReader.Read(sourcePath);
        var validation = Validate(zones: [Zone("Z1", "100", "J1-J3")]);
        var distribution = UnitLengthDemandDistributor.Calculate(network, validation);
        var generatedPath = EpanetDemandFileWriter.WriteVerifiedCopy(sourcePath, distribution);
        var existingReportPath = Path.ChangeExtension(generatedPath, ".txt");
        File.WriteAllText(existingReportPath, "existing report");

        var reportPath = DemandGenerationReportWriter.Write(sourcePath, generatedPath, network, validation, distribution);

        Assert.EndsWith("network_Unit-Length_1_2.txt", reportPath, StringComparison.Ordinal);
        Assert.Equal("existing report", File.ReadAllText(existingReportPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private EpanetNetworkSummary ReadNetwork() => EpanetNetworkReader.Read(WriteNetwork(ValidNetwork));

    private string WriteNetwork(string contents)
    {
        var filePath = Path.Combine(_directory, "network.inp");
        File.WriteAllText(filePath, contents);
        return filePath;
    }

    private DemandValidationResult Validate(
        DemandDistributionMethod method = DemandDistributionMethod.UnitLength,
        string totalDemand = "100",
        string? flowUnits = "LPS",
        IReadOnlyList<ZoneInstructionDraft>? zones = null) =>
        DemandInputValidator.Validate(
            ReadNetwork(),
            method,
            totalDemand,
            flowUnits,
            zones ?? [Zone("Z1", "100", "J1-J3")]);

    private static ZoneInstructionDraft Zone(
        string name,
        string demand,
        string junctions,
        string? kHalfPipes = null,
        string? kZeroPipes = null) =>
        new(name, demand, junctions, kHalfPipes, kZeroPipes);
}