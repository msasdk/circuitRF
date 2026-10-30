using System;
using Avalonia;
using Avalonia.Controls;
using CircuitRF.Ui.Controls;

namespace CircuitRF.Ui.Views.SParameters;

/// <summary>
/// Lays the viewer's three plots out for whichever of them are on.
/// One plot is centred in the pane. The Smith chart with one rectangular plot stacks them, with a gap.
/// All three put the Smith chart across the top and the two rectangular plots side by side beneath it.
/// </summary>
public sealed class SParameterPlotLayout : Panel
{
    /// <summary>Empty band between two plots, in pixels.</summary>
    public const double Gap = 22;

    /// <summary>Width divided by height for a rectangular plot that has the pane to itself.</summary>
    public const double LoneRectRatio = 1.7;

    public static readonly StyledProperty<bool> ShowSmithProperty =
        AvaloniaProperty.Register<SParameterPlotLayout, bool>(nameof(ShowSmith));

    public static readonly StyledProperty<bool> ShowMagnitudeProperty =
        AvaloniaProperty.Register<SParameterPlotLayout, bool>(nameof(ShowMagnitude));

    public static readonly StyledProperty<bool> ShowPhaseProperty =
        AvaloniaProperty.Register<SParameterPlotLayout, bool>(nameof(ShowPhase));

    static SParameterPlotLayout()
    {
        AffectsMeasure<SParameterPlotLayout>(ShowSmithProperty, ShowMagnitudeProperty, ShowPhaseProperty);
        AffectsArrange<SParameterPlotLayout>(ShowSmithProperty, ShowMagnitudeProperty, ShowPhaseProperty);
    }

    public bool ShowSmith
    {
        get => GetValue(ShowSmithProperty);
        set => SetValue(ShowSmithProperty, value);
    }

    public bool ShowMagnitude
    {
        get => GetValue(ShowMagnitudeProperty);
        set => SetValue(ShowMagnitudeProperty, value);
    }

    public bool ShowPhase
    {
        get => GetValue(ShowPhaseProperty);
        set => SetValue(ShowPhaseProperty, value);
    }

    /// <summary>Where each plot goes. A default rect is a plot that is off.</summary>
    public readonly record struct Slots(Rect Smith, Rect Magnitude, Rect Phase);

    /// <summary>The rectangles the three hosts occupy in a pane of <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static Slots SlotsFor(bool smith, bool magnitude, bool phase, double width, double height)
    {
        double w = Math.Max(0, width), h = Math.Max(0, height);
        Rect none = default;

        if (smith && magnitude && phase)
        {
            double row = Math.Max(0, (h - Gap) / 2);
            double col = Math.Max(0, (w - Gap) / 2);
            return new Slots(
                new Rect(0, 0, w, row),
                new Rect(0, row + Gap, col, row),
                new Rect(col + Gap, row + Gap, col, row));
        }

        if (smith && magnitude)
        {
            double row = Math.Max(0, (h - Gap) / 2);
            return new Slots(new Rect(0, 0, w, row), new Rect(0, row + Gap, w, row), none);
        }

        if (smith && phase)
        {
            double row = Math.Max(0, (h - Gap) / 2);
            return new Slots(new Rect(0, 0, w, row), none, new Rect(0, row + Gap, w, row));
        }

        if (magnitude && phase)
        {
            // Each rectangular plot spans the pane, one above the other, so neither is squeezed
            // into half the width.
            double row = Math.Max(0, (h - Gap) / 2);
            return new Slots(none, new Rect(0, 0, w, row), new Rect(0, row + Gap, w, row));
        }

        if (smith)
        {
            // The names sit just outside the disc. A square host centres the disc and leaves the
            // side band outside the host, so the names fall onto the circle. The host is wide
            // enough that the centred disc keeps a strip on each side.
            const double names = 120;
            double disc = Math.Min(w, h);
            if ((w - disc) / 2 >= names || w <= names + 160)
                return new Slots(new Rect(0, 0, w, h), none, none);
            double d = Math.Max(160, Math.Min(h, w - 2 * names));
            return new Slots(new Rect(0, Math.Max(0, (h - d) / 2), w, d), none, none);
        }

        if (magnitude || phase)
        {
            var size = AspectRatioPanel.Fit(new Size(w, h), LoneRectRatio);
            var rect = new Rect((w - size.Width) / 2, (h - size.Height) / 2, size.Width, size.Height);
            return magnitude ? new Slots(none, rect, none) : new Slots(none, none, rect);
        }

        return new Slots(none, none, none);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size available)
    {
        double w = Finite(available.Width);
        double h = Finite(available.Height);
        if (w <= 0 || h <= 0) return new Size(w, h);
        var slots = SlotsFor(ShowSmith, ShowMagnitude, ShowPhase, w, h);
        foreach (var child in Children)
        {
            if (child is not Control c) continue;
            var slot = SlotOf(c, slots);
            c.Measure(slot is { } r ? r.Size : new Size(w, h));
        }
        return new Size(w, h);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var slots = SlotsFor(ShowSmith, ShowMagnitude, ShowPhase, finalSize.Width, finalSize.Height);
        var full = new Rect(finalSize);
        foreach (var child in Children)
        {
            if (child is not Control c) continue;
            c.Arrange(SlotOf(c, slots) ?? full);
        }
        return finalSize;
    }

    private static Rect? SlotOf(Control child, Slots slots) => child.Name switch
    {
        "SmithHost"     => slots.Smith.Width > 0 ? slots.Smith : new Rect(0, 0, 0, 0),
        "MagnitudeHost" => slots.Magnitude.Width > 0 ? slots.Magnitude : new Rect(0, 0, 0, 0),
        "PhaseHost"     => slots.Phase.Width > 0 ? slots.Phase : new Rect(0, 0, 0, 0),
        _               => null,
    };

    private static double Finite(double v) => double.IsFinite(v) && v > 0 ? v : 0;
}
