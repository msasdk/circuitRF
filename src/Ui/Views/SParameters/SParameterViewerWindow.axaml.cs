using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.SParameters;

namespace CircuitRF.Ui.Views.SParameters;

/// <summary>
/// The S-Parameter Viewer's window. A tool window of its own, as the Match Designer and the Signal Designer
/// are — opened from Tools, from a Touchstone file in the Workspace, from a placed SnP component and from a
/// Data Display's dataset list.
/// </summary>
public partial class SParameterViewerWindow : Window, ICrfMenuWindow
{
    /// <summary>One viewer per file: a second request for a file already on show brings that window forward.</summary>
    private static readonly Dictionary<string, SParameterViewerWindow> OpenByPath = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] PlotNames = ["SmithPlotControl", "MagnitudePlotControl", "PhasePlotControl"];

    private readonly System.Collections.ObjectModel.ObservableCollection<MarkerInfoBoxViewModel> _boxes = [];
    private (PlotControl Plot, PlotContainerViewModel Container)[] _hosted = [];
    private string? _registeredPath;

    /// <summary>In the Window menu as in the title bar.</summary>
    public string WindowMenuHeader => Vm?.Title ?? SParameterViewerViewModel.ToolName;

    public SParameterViewerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            SyncPlotTheme();
            if (Vm is { } vm)
            {
                vm.PlotsUpdated += (_, _) => RepaintPlots();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SParameterViewerViewModel.FilePath)) Register(vm.FilePath);
                };
                WireMarkers(vm);
            }
        };
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();
        // A press anywhere but on an info box clears the marker selection, as the Data Display's canvas does.
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        Closed += (_, _) => Register(null);
    }

    private SParameterViewerViewModel? Vm => DataContext as SParameterViewerViewModel;

    // ── opening ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Tools ▸ S-Parameter Viewer: an empty viewer, waiting for a file. Not deduplicated — several may be
    /// open — and shown as the standalone Signal Designer is: unowned, cascaded off the workspace, closed with it.
    /// </summary>
    public static SParameterViewerWindow ShowStandalone(Window? owner)
    {
        var window = Create(owner);
        Present(window, owner);
        return window;
    }

    /// <summary>The viewer on <paramref name="path"/>: the window already showing it, or a new one reading it.</summary>
    public static SParameterViewerWindow ShowForFile(string path, Window? owner)
    {
        string full = Path.GetFullPath(path);
        if (OpenByPath.TryGetValue(full, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var window = Create(owner);
        window.Register(full);
        Present(window, owner);
        _ = window.Vm!.LoadAsync(full);
        return window;
    }

    private static SParameterViewerWindow Create(Window? owner)
    {
        var vm = new SParameterViewerViewModel();
        var window = new SParameterViewerWindow { DataContext = vm };
        vm.PickFileAsync = window.PickTouchstoneAsync;
        vm.PickDisplayPathAsync = window.PickDisplayPathAsync;
        vm.OpenDataDisplay = cdd => WorkspaceLocator.For(owner)?.OpenDataDisplayDocument(cdd);
        return window;
    }

    private static void Present(SParameterViewerWindow window, Window? owner)
    {
        if (owner is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
            return;
        }
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        void CloseWithOwner(object? _, EventArgs __) => window.Close();
        owner.Closed += CloseWithOwner;
        window.Closed += (_, _) => owner.Closed -= CloseWithOwner;
        var at = Match.MatchWindowPlacement.Cascade(owner.Position, owner.RenderScaling, owner.Screens?.ScreenFromWindow(owner)?.WorkingArea);
        window.Position = at;
        window.Show();
        window.Position = at;
        NewWindowFront.Keep(window, owner);
    }

    /// <summary>Keeps <see cref="OpenByPath"/> naming the file this window shows — which changes when Open picks another.</summary>
    private void Register(string? path)
    {
        if (_registeredPath is { } old && OpenByPath.TryGetValue(old, out var w) && ReferenceEquals(w, this))
            OpenByPath.Remove(old);
        _registeredPath = path;
        if (path is not null) OpenByPath.TryAdd(path, this);
    }

    // ── pickers ──────────────────────────────────────────────────────────────

    /// <summary>The Touchstone picker's file types — the SnP component's own filter, so both offer the same files.</summary>
    internal static readonly FilePickerFileType TouchstoneFiles = new("Touchstone (*.sNp)")
    {
        Patterns = ["*.s1p", "*.s2p", "*.s3p", "*.s4p", "*.s5p", "*.s6p", "*.s7p", "*.s8p", "*.s9p", "*.s10p", "*.s11p", "*.s12p", "*.snp",
                    "*.S1P", "*.S2P", "*.S3P", "*.S4P", "*.S5P", "*.S6P", "*.S7P", "*.S8P", "*.S9P", "*.S10P", "*.S11P", "*.S12P", "*.SNP"],
    };

    private async Task<string?> PickTouchstoneAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Touchstone File",
            AllowMultiple = false,
            FileTypeFilter = [TouchstoneFiles, FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickDisplayPathAsync(string suggestedName)
    {
        var start = Vm?.FilePath is { } p && Path.GetDirectoryName(p) is { } dir
            ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as Data Display",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            DefaultExtension = "cdd",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Data Display (*.cdd)") { Patterns = ["*.cdd"] }],
        });
        return file?.TryGetLocalPath();
    }

    // ── the plots ────────────────────────────────────────────────────────────

    private void SyncPlotTheme()
    {
        if (Vm is null) return;
        Vm.PlotHost.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
        foreach (var name in PlotNames)
            this.FindControl<PlotControl>(name)?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
    }

    /// <summary>The plots are refilled in place (same Plot, new traces), and a PlotControl repaints by itself only
    /// when its Plot is replaced or the user touches it — so every refill repaints all three here.</summary>
    private void RepaintPlots()
    {
        foreach (var name in PlotNames) this.FindControl<PlotControl>(name)?.InvalidateVisual();
    }

    /// <summary>
    /// The plots are bare PlotControls, so this window is their host, as the Signal Designer is for its own: it
    /// hands each one its container's marker providers and events, and gives the info boxes somewhere to be drawn.
    /// Without this a marker is added with no info box and cannot be selected or deleted.
    /// </summary>
    private void WireMarkers(SParameterViewerViewModel vm)
    {
        var layer = this.FindControl<ItemsControl>("InfoBoxLayer")!;
        layer.ItemsSource = _boxes;
        _hosted =
        [
            (this.FindControl<PlotControl>("SmithPlotControl")!, vm.SmithContainer),
            (this.FindControl<PlotControl>("MagnitudePlotControl")!, vm.MagnitudeContainer),
            (this.FindControl<PlotControl>("PhasePlotControl")!, vm.PhaseContainer),
        ];
        foreach (var (plot, container) in _hosted)
        {
            plot.NextMarkerIndexProvider     = container.GetNextMarkerIndex;
            plot.FindMarkerInfoBoxVmProvider = container.FindMarkerInfoBoxVm;
            plot.ContainerProvider           = () => container;
            plot.SelectedMarkersProvider     = container.GetSelectedMarkers;
            plot.StepSelectedMarkersHandler  = container.StepSelectedMarkers;
            plot.DoubleTapped += (_, args) => { plot.HandleDoubleTapAt(args.GetPosition(plot)); args.Handled = true; };
            plot.PlotChanged += container.OnPlotChanged;
            plot.MarkerMoved += (_, _) => container.OnMarkerMoved();
            plot.MarkerAdded += container.OnMarkerAdded;
            container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();
        }
        vm.PlotHost.MarkerInfoBoxes.CollectionChanged += (_, _) => RefillBoxes();
        RefillBoxes();
        // LayoutUpdated rather than SizeChanged: a plot can move without resizing, and its boxes must follow.
        LayoutUpdated += (_, _) => SyncPlotContainers();
    }

    private void RefillBoxes()
    {
        if (Vm is null) return;
        _boxes.Clear();
        foreach (var box in Vm.PlotHost.MarkerInfoBoxes) _boxes.Add(box);
    }

    private void SyncPlotContainers()
    {
        if (this.FindControl<ItemsControl>("InfoBoxLayer") is not { } layer) return;
        foreach (var (plot, container) in _hosted)
        {
            if (plot.Bounds.Width < 1 || plot.Bounds.Height < 1) continue;
            if (plot.TranslatePoint(default, layer) is not { } origin) continue;
            if (Math.Abs(container.Left - origin.X) < 0.5 && Math.Abs(container.Top - origin.Y) < 0.5
                && Math.Abs(container.Width - plot.Bounds.Width) < 0.5 && Math.Abs(container.Height - plot.Bounds.Height) < 0.5)
                continue;   // LayoutUpdated fires constantly; only a real move is worth notifying
            container.Left = origin.X;
            container.Top = origin.Y;
            container.Width = plot.Bounds.Width;
            container.Height = plot.Bounds.Height;
            container.NotifyViewProperties();
        }
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
        if (e.Source is Visual v && v.FindAncestorOfType<DataDisplay.MarkerInfoBoxView>(includeSelf: true) is not null)
            return;
        Vm.PlotHost.SelectOnly((MarkerInfoBoxViewModel?)null);
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => DocLauncher.Open("reference/sparameter-viewer.html");

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
