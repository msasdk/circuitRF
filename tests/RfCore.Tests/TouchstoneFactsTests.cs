// ================================================================
//  TouchstoneFactsTests.cs — what a Touchstone file says about itself
//
//  The S-Parameter Viewer's file pane, `circuitrf snp` and `check`'s
//  structural findings all read TouchstoneFacts, so these pin what it
//  says: how the grid is classified (a file printed to a few digits is
//  still uniform), which defects are findings, and that a file read from
//  text arrives with the facts its option line stated.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Diagnostics;
using NumFlat;
using RfCore;
using RfCore.Data;
using Xunit;

namespace RfCore.Tests;

public class TouchstoneFactsTests
{
    private static SNP TwoPort(params double[] f) =>
        new(f, [.. f.Select(_ => new Mat<Complex>(2, 2))], MatrixType.S, MatrixFormat.MA, new Complex(50, 0));

    [Fact]
    public void AConstantStepIsUniform_AndCarriesItsStep()
    {
        var f = TouchstoneFacts.Describe(TwoPort([.. Enumerable.Range(0, 201).Select(i => 1e9 + i * 10e6)]));
        Assert.Equal(FrequencyGrid.Uniform, f.Grid);
        Assert.Equal(10e6, f.StepHz, 1e-3);
        Assert.Equal(201, f.FrequencyCount);
        Assert.Equal(1e9, f.FirstFrequencyHz);
        Assert.Equal(3e9, f.LastFrequencyHz, 1e-3);
        Assert.True(double.IsNaN(f.PointsPerDecade));
    }

    /// <summary>A sweep written in GHz to six significant digits is still the uniform sweep it was taken as —
    /// its rounding moves the top steps by a part in 10³ of the step.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASweepPrintedToSixDigitsKeepsItsSpacing(bool log)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var f = TouchstoneFacts.Describe(TwoPort([.. Enumerable.Range(0, 301)
            .Select(i => log ? 0.001 * Math.Pow(10, i / 100.0) : 1.0 + i / 150.0)
            .Select(ghz => double.Parse(ghz.ToString("G6", inv), inv) * 1e9)]));
        Assert.Equal(log ? FrequencyGrid.Logarithmic : FrequencyGrid.Uniform, f.Grid);
    }

    [Fact]
    public void AStepThatChangesByMoreThanRoundingIsNotUniform()
    {
        var f = TouchstoneFacts.Describe(TwoPort([.. Enumerable.Range(0, 101).Select(i => 1e9 + i * 10e6 + (i == 50 ? 0.2e6 : 0))]));
        Assert.Equal(FrequencyGrid.Irregular, f.Grid);
    }

    [Fact]
    public void AConstantRatioIsLogarithmic_AndCarriesItsDensity()
    {
        var f = TouchstoneFacts.Describe(TwoPort([.. Enumerable.Range(0, 31).Select(i => 1e6 * Math.Pow(10, i / 10.0))]));
        Assert.Equal(FrequencyGrid.Logarithmic, f.Grid);
        Assert.Equal(10.0, f.PointsPerDecade, 1e-6);
        Assert.True(double.IsNaN(f.StepHz));
    }

    [Fact]
    public void NeitherIsIrregular_WithItsSmallestAndLargestStep()
    {
        var f = TouchstoneFacts.Describe(TwoPort(1e9, 1.1e9, 1.5e9, 1.6e9, 3e9));
        Assert.Equal(FrequencyGrid.Irregular, f.Grid);
        Assert.Equal(0.1e9, f.SmallestStepHz, 1);
        Assert.Equal(1.4e9, f.LargestStepHz, 1);
        Assert.False(f.FrequenciesOutOfOrder);
        Assert.Empty(f.Findings("x.s2p"));
    }

    [Fact]
    public void ASampleAtZeroHertzIsADcPoint_AndAnyOtherSweepIsNot()
    {
        Assert.True(TouchstoneFacts.Describe(TwoPort(0, 1e9, 2e9)).HasDcPoint);
        Assert.False(TouchstoneFacts.Describe(TwoPort(1e9, 2e9)).HasDcPoint);
    }

    [Fact]
    public void OnePointHasNoSpacing()
    {
        var f = TouchstoneFacts.Describe(TwoPort(2e9));
        Assert.Equal(FrequencyGrid.SinglePoint, f.Grid);
        Assert.True(double.IsNaN(f.SmallestStepHz));
    }

    [Fact]
    public void AFrequencyThatGoesBackIsAFinding_NamingTheOneBasedPoint()
    {
        var f = TouchstoneFacts.Describe(TwoPort(1e9, 2e9, 1.5e9, 3e9));
        Assert.True(f.FrequenciesOutOfOrder);
        Assert.Equal(2, f.FirstNonIncreasingIndex);
        Assert.Equal(FrequencyGrid.Irregular, f.Grid);

        var d = Assert.Single(f.Findings("amp.s2p"));
        Assert.Equal("touchstone.frequency-order", d.Id);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(3, d.Arguments["point"]);
    }

    [Fact]
    public void ANameThatClaimsOtherPortsIsAFinding()
    {
        var f = TouchstoneFacts.Describe(TwoPort(1e9, 2e9), "part.s3p");
        Assert.Equal(3, f.ExtensionPorts);
        Assert.True(f.PortsDisagreeWithName);
        Assert.Equal("touchstone.port-mismatch", Assert.Single(f.Findings("part.s3p")).Id);

        Assert.False(TouchstoneFacts.Describe(TwoPort(1e9, 2e9), "part.s2p").PortsDisagreeWithName);
        Assert.Null(TouchstoneFacts.Describe(TwoPort(1e9, 2e9), "part.txt").ExtensionPorts);
    }

    [Fact]
    public void AReferenceWithNoPositiveResistanceIsAFinding()
    {
        var snp = TwoPort(1e9, 2e9);
        snp.Z0 = new Complex(0, 0);
        Assert.Equal("touchstone.reference-unusable", Assert.Single(TouchstoneFacts.Describe(snp).Findings("x.s2p")).Id);

        var perPort = TwoPort(1e9, 2e9);
        perPort.Z0PerPort = [new Complex(50, 0), new Complex(-5, 0)];
        Assert.True(TouchstoneFacts.Describe(perPort).Z0Unusable);
    }

    [Fact]
    public void AFileReadFromTextStatesItsOptionLine_AndItsNoiseBlock()
    {
        string path = Path.Combine(Path.GetTempPath(), $"facts-{Guid.NewGuid():N}.s2p");
        File.WriteAllText(path,
            "! a two-port with noise\n" +
            "# MHz S DB R 75\n" +
            "100 -20 10 -1 -5 -40 30 -18 -20\n" +
            "200 -21 12 -1.1 -10 -41 31 -19 -22\n" +
            "300 -22 14 -1.2 -15 -42 32 -20 -24\n" +
            "! noise\n" +
            "100 0.8 0.3 40 0.2\n" +
            "200 0.9 0.35 45 0.21\n");
        try
        {
            var f = TouchstoneFacts.Describe(TouchstoneIO.ReadFile(path), path);
            Assert.Equal(2, f.Ports);
            Assert.Equal(MatrixType.S, f.Type);
            Assert.Equal(MatrixFormat.DB, f.Format);
            Assert.Equal(FrequencyUnit.MHz, f.FrequencyUnit);
            Assert.Equal(new Complex(75, 0), f.Z0);
            Assert.Equal(3, f.FrequencyCount);
            Assert.Equal(FrequencyGrid.Uniform, f.Grid);
            Assert.Equal(100e6, f.StepHz, 1e-3);
            Assert.Equal(2, f.NoisePoints);
            Assert.Empty(f.Findings(Path.GetFileName(path)));
        }
        finally { File.Delete(path); }
    }
}
