// ================================================================
//  `circuitrf snp` — the S-Parameter Viewer with no display.
//
//  The report and --json are driven as a PROCESS, as every verb's are,
//  because that is the surface a build machine or an agent reads. The
//  written outputs are compared against the in-process calls the window
//  makes: `-o x.cdd` must be byte for byte what Save as Data Display
//  writes, because both are SParameterViewerPreset.Build over one source.
// ================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Render.DataDisplay;
using NumFlat;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.SParameters;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class SnpCliVerbTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-snp-cli-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Write(int ports, int points, string? name = null)
    {
        var freqs = Enumerable.Range(0, points).Select(i => 1e9 + i * 0.1e9).ToArray();
        var mats = freqs.Select((_, k) =>
        {
            var m = new Mat<Complex>(ports, ports);
            for (int i = 0; i < ports; i++)
                for (int j = 0; j < ports; j++)
                    m[i, j] = Complex.FromPolarCoordinates(0.2 + 0.1 * i + 0.05 * j, 0.2 * k);
            return m;
        }).ToArray();
        string path = Path.Combine(_dir, name ?? $"part.s{ports}p");
        TouchstoneIO.WriteFile(new SNP(freqs, mats, MatrixType.S, MatrixFormat.MA, new Complex(50, 0)), path);
        return path;
    }

    [Fact]
    public void TheReportIsTheFilePane_AndASoundFileExitsZero()
    {
        string path = Write(2, 41);
        var (exit, stdout, _) = RunCli("snp", path);
        Assert.Equal(0, exit);
        var facts = TouchstoneSource.Read(path, out _)!.Facts;
        foreach (var (label, value) in SParameterViewerPreset.FileFacts(facts))
            Assert.Contains($"{label,-12}  {value}", stdout);
        Assert.Contains("S11, S12, S21, S22", stdout);
    }

    [Fact]
    public void JsonCarriesTheFactsAndThePlottedTerms()
    {
        string path = Write(4, 11);
        var (exit, stdout, _) = RunCli("snp", path, "--params", "reflections,S(2,1)", "--json");
        Assert.Equal(0, exit);
        var snp = JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetProperty("snp");
        Assert.Equal(4, snp.GetProperty("ports").GetInt32());
        Assert.Equal(11, snp.GetProperty("points").GetInt32());
        Assert.False(snp.GetProperty("hasDcPoint").GetBoolean());
        Assert.Equal("uniform", snp.GetProperty("grid").GetString());
        Assert.Equal(1e8, snp.GetProperty("stepHz").GetDouble(), 1e-3);
        Assert.Equal(["S11", "S21", "S22", "S33", "S44"],
                     snp.GetProperty("plotted").EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public void ADefectiveFileIsReportedInTheViewersWords_AndExitsOne()
    {
        string path = Write(2, 5, "mislabelled.s3p");
        var (exit, _, stderr) = RunCli("snp", path, "--json");
        Assert.Equal(1, exit);
        var expected = TouchstoneSource.Read(path, out _)!.Findings.Single().Render();
        Assert.Contains(expected, stderr);
    }

    [Theory]
    [InlineData("--params", "S31")]
    [InlineData("-o", "picture.jpg")]
    public void ARefusalExitsOneAndWritesNothing(string option, string value)
    {
        string path = Write(2, 5);
        var (exit, _, _) = RunCli("snp", path, option, Path.IsPathRooted(value) || option == "--params" ? value : Path.Combine(_dir, value));
        Assert.Equal(1, exit);
        Assert.False(File.Exists(Path.Combine(_dir, "picture.jpg")));
    }

    [Fact]
    public void ADocumentOfAnotherKindIsRefusedByItsKind()
    {
        string csch = Path.Combine(_dir, "amp.csch");
        File.WriteAllText(csch, "{}");
        var (exit, _, stderr) = RunCli("snp", csch);
        Assert.Equal(1, exit);
        Assert.Contains("not a Touchstone file", stderr);
    }

    /// <summary>
    /// <c>-o x.cdd</c> is what the viewer's Save as Data Display writes for the same file and selection —
    /// byte for byte, because both are <see cref="SParameterViewerPreset.Build"/> over one source.
    /// </summary>
    [Fact]
    public void TheDisplayWrittenIsTheOneTheWindowSaves()
    {
        string path = Write(2, 21);
        string cdd = Path.Combine(_dir, "out.cdd");
        Assert.Equal(0, InProcessCli("snp", path, "-o", cdd));

        var source = TouchstoneSource.Read(path, out _)!;
        string expected = JsonSerializer.Serialize(
            SParameterViewerPreset.Build(source, SParameterViewerPreset.DefaultSelection(2)),
            DataDisplayJson.Options);
        Assert.Equal(expected, File.ReadAllText(cdd));
    }

    [Fact]
    public void APictureIsDrawnThroughTheOneComposer()
    {
        string path = Write(2, 21);
        string svg = Path.Combine(_dir, "out.svg");
        Assert.Equal(0, InProcessCli("snp", path, "-o", svg));
        string text = File.ReadAllText(svg);
        Assert.StartsWith("<?xml", text.TrimStart());
        Assert.Contains("<svg", text);
    }

    // ── driving the verb ─────────────────────────────────────────────────────

    private static int InProcessCli(params string[] args)
    {
        JsonRun.Reset();
        return CliEntry.Run(args);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(SnpCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }
}
