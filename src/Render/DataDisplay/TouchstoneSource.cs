// ================================================================
//  TouchstoneSource.cs  —  one Touchstone file, as the S-Parameter
//  Viewer's only data source
//
//  The viewer window and `circuitrf snp` read a file through this and
//  nothing else, so the picture a build machine writes is resolved by
//  the same code the window draws with.
//
//  A Y- or Z-parameter file is converted to S HERE. A network trace
//  reads its source's matrices as S whatever the option line said, so
//  handing it the file as read would draw admittances on a Smith chart.
// ================================================================

using System;
using System.IO;
using CircuitRF.Diagnostics;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

/// <summary>One Touchstone file, read once, as an <see cref="IPlotDataSources"/>.</summary>
public sealed class TouchstoneSource : IPlotDataSources
{
    private DataSet? _data;

    private TouchstoneSource(string path, SNP asRead, SNP sParameters)
    {
        Path        = path;
        AsRead      = asRead;
        SParameters = sParameters;
        Facts       = TouchstoneFacts.Describe(asRead, path);
    }

    /// <summary>The file's absolute path — also the reference every trace the preset writes names.</summary>
    public string Path { get; }

    /// <summary>The network exactly as the file states it.</summary>
    public SNP AsRead { get; }

    /// <summary>The same network as S-parameters at the file's own reference: what is plotted.</summary>
    public SNP SParameters { get; }

    /// <summary>What the file says about itself.</summary>
    public TouchstoneFacts Facts { get; }

    /// <summary>The file's structural defects, as diagnostics naming it.</summary>
    public System.Collections.Generic.IReadOnlyList<Diagnostic> Findings
        => Facts.Findings(System.IO.Path.GetFileName(Path));

    /// <summary>
    /// Reads <paramref name="path"/>. A file that cannot be read comes back as null with the
    /// reason, never as an exception: the window shows the reason and the verb reports it.
    /// </summary>
    public static TouchstoneSource? Read(string path, out Diagnostic? refusal)
    {
        refusal = null;
        string full = System.IO.Path.GetFullPath(path);
        SNP snp;
        try { snp = TouchstoneIO.ReadFile(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                      or ArgumentException or InvalidOperationException)
        {
            refusal = TouchstoneDiagnostics.Unreadable(System.IO.Path.GetFileName(full), ex.Message);
            return null;
        }
        snp.FilePath = full;
        return new TouchstoneSource(full, snp, AsS(snp));
    }

    /// <summary>Wraps a network already in memory under <paramref name="path"/>.</summary>
    public static TouchstoneSource FromNetwork(string path, SNP snp)
        => new(System.IO.Path.GetFullPath(path), snp, AsS(snp));

    private static SNP AsS(SNP snp)
    {
        var s = snp.Type switch
        {
            MatrixType.Z => RFNetwork.ZToS(snp),
            MatrixType.Y => RFNetwork.YToS(snp),
            _            => snp,
        };
        if (!ReferenceEquals(s, snp))
        {
            s.Z0PerPort = snp.Z0PerPort;
            s.FilePath  = snp.FilePath;
        }
        return s;
    }

    // ── IPlotDataSources ──────────────────────────────────────────────────────

    public string? ResolveAbs(string? sourceRef) => Path;

    public bool Contains(string absPath) => string.Equals(absPath, Path, StringComparison.OrdinalIgnoreCase);

    public SNP? NetworkFor(string absPath) => Contains(absPath) ? SParameters : null;

    public DataSet? DataFor(string absPath) => Contains(absPath) ? _data ??= DataSetBuilder.FromSnp(SParameters) : null;

    public string? AliasFor(string absPath) => null;

    public string? DisplayNameFor(string absPath) => System.IO.Path.GetFileName(absPath);

    public bool HasMultipleSources => false;

    public DataSet? SelectedData => DataFor(Path);
}
