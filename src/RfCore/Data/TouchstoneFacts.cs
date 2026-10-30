// ================================================================
//  TouchstoneFacts.cs  —  what a Touchstone file says about itself
//
//  The S-Parameter Viewer's left pane, `circuitrf snp`'s report and
//  `circuitrf check`'s structural findings all read one description of
//  a file: its port count against the one its name claims, the option
//  line, the frequency axis and how it is spaced, the reference
//  impedance and what else the file carries. Nothing is measured about
//  the NETWORK here (TouchstoneHealth does that); this is the file.
//
//  Reported, never repaired: a file whose axis goes backwards is
//  described as one, and plotted as one, so the defect is visible.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Diagnostics;

namespace RfCore.Data
{
    /// <summary>How a file's frequency points are spaced.</summary>
    public enum FrequencyGrid
    {
        /// <summary>One frequency: there is no spacing to describe.</summary>
        SinglePoint,
        /// <summary>A constant step, <see cref="TouchstoneFacts.StepHz"/>.</summary>
        Uniform,
        /// <summary>A constant ratio, <see cref="TouchstoneFacts.PointsPerDecade"/> per decade.</summary>
        Logarithmic,
        /// <summary>Neither — or an axis that is not strictly increasing.</summary>
        Irregular,
    }

    /// <summary>
    /// The facts a Touchstone file states, read off an <see cref="SNP"/> as the reader produced it.
    /// </summary>
    /// <param name="Ports">The port count the data holds.</param>
    /// <param name="ExtensionPorts">The port count the file name claims (<c>.s4p</c> → 4), or null for a
    /// name that claims none.</param>
    /// <param name="Type">S, Y or Z, from the option line.</param>
    /// <param name="Format">RI, MA or DB, from the option line.</param>
    /// <param name="FrequencyUnit">The option line's unit — the unit the file's own numbers are written in.</param>
    /// <param name="FrequencyCount">Number of frequency points.</param>
    /// <param name="FirstFrequencyHz">The first frequency in the file, in hertz.</param>
    /// <param name="LastFrequencyHz">The last frequency in the file, in hertz.</param>
    /// <param name="Grid">How the points are spaced.</param>
    /// <param name="StepHz">The constant step of a <see cref="FrequencyGrid.Uniform"/> grid; NaN otherwise.</param>
    /// <param name="PointsPerDecade">The density of a <see cref="FrequencyGrid.Logarithmic"/> grid; NaN otherwise.</param>
    /// <param name="SmallestStepHz">The smallest step between neighbouring points; NaN for one point.</param>
    /// <param name="LargestStepHz">The largest step between neighbouring points; NaN for one point.</param>
    /// <param name="FirstNonIncreasingIndex">Index of the first point not above its predecessor, or −1.</param>
    /// <param name="Z0">The reference impedance the option line declares.</param>
    /// <param name="Z0PerPort">Per-port references when the file states them, else null.</param>
    /// <param name="NoisePoints">Rows of a two-port noise block; 0 when there is none.</param>
    /// <param name="CommentLines">Comment lines the reader kept.</param>
    /// <param name="HasDcPoint">True when a frequency sample is at 0 Hz.</param>
    public sealed record TouchstoneFacts(
        int           Ports,
        int?          ExtensionPorts,
        MatrixType    Type,
        MatrixFormat  Format,
        FrequencyUnit FrequencyUnit,
        int           FrequencyCount,
        double        FirstFrequencyHz,
        double        LastFrequencyHz,
        FrequencyGrid Grid,
        double        StepHz,
        double        PointsPerDecade,
        double        SmallestStepHz,
        double        LargestStepHz,
        int           FirstNonIncreasingIndex,
        Complex       Z0,
        Complex[]?    Z0PerPort,
        int           NoisePoints,
        int           CommentLines,
        bool          HasDcPoint)
    {
        /// <summary>
        /// Relative tolerance on a step (or a ratio) before a grid stops being called uniform (or
        /// logarithmic).
        ///
        /// <para><b>Looser than <see cref="TouchstoneHealth"/>'s 10⁻⁶ on purpose, because the two
        /// answer different questions.</b> The causality transform needs samples that ARE equally
        /// spaced; this describes how the file was swept. Touchstone writers print frequencies to a
        /// handful of significant digits — a 1 to 3 GHz sweep in 10 MHz steps written as
        /// <c>1.01</c>, <c>1.02</c> … is uniform to the eye and to the analyzer that took it, and
        /// rounding to six digits moves a step by parts in 10⁴. Calling that "irregular" would be a
        /// wrong statement about the file.</para>
        /// </summary>
        public const double SpacingTolerance = 1e-3;

        /// <summary>
        /// The relative precision a frequency is assumed to be PRINTED to — six significant digits.
        /// A rounded frequency is off by a fraction of its own SIZE, not of the step, so a fine step high
        /// in the band needs this floor as well as <see cref="SpacingTolerance"/>: 1 to 3 GHz in 300 steps
        /// written as <c>2.99333</c> moves its last step by a part in 10³ of itself.
        /// </summary>
        public const double PrintPrecision = 1e-5;

        /// <summary>True when the file name claims a port count the data does not have.</summary>
        public bool PortsDisagreeWithName => ExtensionPorts is { } n && n != Ports;

        /// <summary>True when the frequency axis is not strictly increasing.</summary>
        public bool FrequenciesOutOfOrder => FirstNonIncreasingIndex >= 0;

        /// <summary>True when no renormalisation can use the declared reference: a resistance that is
        /// not positive.</summary>
        public bool Z0Unusable => !(Z0.Real > 0.0) || (Z0PerPort?.Any(z => !(z.Real > 0.0)) ?? false);

        /// <summary>
        /// Describes <paramref name="snp"/>. <paramref name="path"/> supplies the name's port claim and
        /// may be null for data that came from no file.
        /// </summary>
        public static TouchstoneFacts Describe(SNP snp, string? path = null)
        {
            ArgumentNullException.ThrowIfNull(snp);
            var f = snp.Frequencies;
            int n = f.Length;

            int firstBad = -1;
            double smallest = double.NaN, largest = double.NaN;
            for (int i = 1; i < n; i++)
            {
                double step = f[i] - f[i - 1];
                if (firstBad < 0 && !(step > 0)) firstBad = i;
                if (double.IsNaN(smallest) || step < smallest) smallest = step;
                if (double.IsNaN(largest)  || step > largest)  largest  = step;
            }

            var (grid, stepHz, perDecade) = Classify(f, firstBad);

            bool dc = false;
            for (int i = 0; i < n && !dc; i++) dc = Math.Abs(f[i]) <= 1e-9;

            return new TouchstoneFacts(
                snp.Ports,
                path is null ? null : TouchstoneIO.ParsePortsFromExtension(path),
                snp.Type,
                snp.Format,
                snp.FreqUnit,
                n,
                n > 0 ? f[0] : double.NaN,
                n > 0 ? f[^1] : double.NaN,
                grid, stepHz, perDecade,
                smallest, largest,
                firstBad,
                snp.Z0,
                snp.Z0PerPort,
                snp.NoiseParameters.Count,
                snp.Comments.Count,
                dc);
        }

        private static (FrequencyGrid Grid, double StepHz, double PerDecade) Classify(double[] f, int firstBad)
        {
            int n = f.Length;
            if (n < 2) return (FrequencyGrid.SinglePoint, double.NaN, double.NaN);
            if (firstBad >= 0) return (FrequencyGrid.Irregular, double.NaN, double.NaN);

            double mean = (f[^1] - f[0]) / (n - 1);
            double tol  = Math.Max(SpacingTolerance * mean, PrintPrecision * Math.Max(Math.Abs(f[0]), Math.Abs(f[^1])));
            bool uniform = true;
            for (int i = 1; i < n && uniform; i++)
                uniform = Math.Abs((f[i] - f[i - 1]) - mean) <= tol;
            if (uniform) return (FrequencyGrid.Uniform, mean, double.NaN);

            // A ratio needs a positive start: a sweep from DC is uniform or nothing.
            if (n >= 3 && f[0] > 0)
            {
                double meanLog = Math.Log(f[^1] / f[0]) / (n - 1);
                double logTol  = Math.Max(SpacingTolerance * meanLog, 2 * PrintPrecision);
                bool log = true;
                for (int i = 1; i < n && log; i++)
                    log = Math.Abs(Math.Log(f[i] / f[i - 1]) - meanLog) <= logTol;
                if (log) return (FrequencyGrid.Logarithmic, double.NaN, Math.Log(10.0) / meanLog);
            }
            return (FrequencyGrid.Irregular, double.NaN, double.NaN);
        }

        /// <summary>
        /// The defects this description shows, as diagnostics naming <paramref name="file"/>: a name
        /// that contradicts the data, a frequency axis that is not sorted, a reference no
        /// renormalisation can use. Empty for a sound file. Passivity, reciprocity and causality are
        /// not here — they are properties of the network, and <see cref="TouchstoneHealth"/>'s.
        /// </summary>
        public IReadOnlyList<Diagnostic> Findings(string file)
        {
            var found = new List<Diagnostic>();
            if (PortsDisagreeWithName)
                found.Add(TouchstoneDiagnostics.PortsDisagreeWithName(file, ExtensionPorts!.Value, Ports));
            if (FrequenciesOutOfOrder)
                found.Add(TouchstoneDiagnostics.FrequenciesOutOfOrder(file, FirstNonIncreasingIndex + 1));
            if (Z0Unusable)
                found.Add(TouchstoneDiagnostics.UnusableReference(file));
            return found;
        }
    }

    /// <summary>
    /// What is said about a Touchstone file's structure. Reword a template freely; an id is the durable part.
    /// </summary>
    public static class TouchstoneDiagnostics
    {
        /// <summary>The file could not be read at all.</summary>
        public static Diagnostic Unreadable(string file, string why) => Diagnostic.Create(
            "touchstone.unreadable", DiagnosticSeverity.Error,
            "'{file}' cannot be read as a Touchstone file: {why}",
            ("file", file), ("why", why));

        /// <summary>The name claims one port count and the data holds another.</summary>
        public static Diagnostic PortsDisagreeWithName(string file, int declared, int actual) => Diagnostic.Create(
            "touchstone.port-mismatch", DiagnosticSeverity.Error,
            "'{file}': the file name declares {declared} ports and the data holds {actual}, so its matrices may have been assembled the wrong shape.",
            ("file", file), ("declared", declared), ("actual", actual));

        /// <summary>A frequency does not exceed the one before it.</summary>
        public static Diagnostic FrequenciesOutOfOrder(string file, int point) => Diagnostic.Create(
            "touchstone.frequency-order", DiagnosticSeverity.Error,
            "'{file}': the frequencies are not strictly increasing from point {point}; the plots draw them in file order.",
            ("file", file), ("point", point));

        /// <summary>A reference resistance that is not positive.</summary>
        public static Diagnostic UnusableReference(string file) => Diagnostic.Create(
            "touchstone.reference-unusable", DiagnosticSeverity.Error,
            "'{file}': the reference impedance has a resistance that is not positive, so the data cannot be renormalised or read as impedance.",
            ("file", file));

        /// <summary>A parameter named for display that the file does not have.</summary>
        public static Diagnostic NoSuchParameter(string text, int ports) => Diagnostic.Create(
            "touchstone.no-such-parameter", DiagnosticSeverity.Error,
            "'{text}' is not a parameter of a {ports}-port file. Name them as S11, S21 … or S(1,1), S(2,1) …, each port from 1 to {ports}.",
            ("text", text), ("ports", ports));
    }
}
