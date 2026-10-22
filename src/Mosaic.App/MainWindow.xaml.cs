using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Mosaic.Core;
using Mosaic.Imaging;
using Mosaic.Output;
using Mosaic.Storage;

namespace Mosaic.App;

public partial class MainWindow : Window
{
    private readonly ImageService images;
    private readonly EnhancementService enhancement;
    private readonly ProjectStore projects;
    private readonly PreferencesStore settings;
    private readonly RecoverySession recovery;
    private RecoveryLease? pendingRecovery;
    private bool pendingRecoveryRestored;
    private readonly PdfExporter pdf = new();
    private readonly PrintService printer = new();
    private AppPreferences preferences;
    private ProjectState state = new();
    private SourceInfo? source;
    private IDisposable? sourceLease;
    private LayoutPlan? layout;
    private string? enhancedPath, projectPath, completedPdf;
    private RenderSnapshot? completedSnapshot;
    private string? completedPrinter;
    private CancellationTokenSource? job;
    private TaskCompletionSource? jobCompletion;
    private Task? closeTask;
    private bool closeRequested, resourcesDisposed;
    private bool initialized, synchronizing, dirty, closing, compareOriginal, recovering;
    private long revision, recoveredRevision = -1;
    private Task? recoveryTask;
    private CancellationTokenSource recoveryCancellation = new();
    private bool dimensionInvalid, marginInvalid, layoutInvalid;
    private bool widthTextEdited, heightTextEdited;
    private IInputElement? completionFocus;
    private readonly List<ProjectState> undoStates = [], redoStates = [];
    private ProjectState historyAnchor = new();
    private readonly DispatcherTimer recoveryTimer;
    private readonly string[] arguments;
    internal Action<string>? ErrorPresenter { get; set; }
    internal Func<string, string?>? SavePathChooser { get; set; }

    public MainWindow(string[] args, string? dataRoot = null)
    {
        images = new(dataRoot == null ? null : Path.Combine(dataRoot, "image-cache"));
        enhancement = new();
        projects = new(dataRoot == null ? null : Path.Combine(dataRoot, "project-cache"));
        settings = new(dataRoot);
        recovery = new(settings.RootPath);
        arguments = args;
        preferences = settings.Load();
        var lang = args.Contains("--pl") ? "pl" : args.Contains("--en") ? "en" : preferences.Language;
        Lang.Apply(lang);
        InitializeComponent();
        LanguageBox.SelectedIndex = lang == "pl" ? 1 : 0;
        FitBox.SelectedIndex = ProfileBox.SelectedIndex = OrientationBox.SelectedIndex = CopiesBox.SelectedIndex = 0;
        initialized = true;
        SyncControls();
        recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        recoveryTimer.Tick += async (_, _) => { if (recoveryTask == null || recoveryTask.IsCompleted) { recoveryTask = RecoverEditsAsync(); await recoveryTask; } };
        recoveryTimer.Start();
        Loaded += LoadedAsync;
        Closed += (_, _) => DisposeResources();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, async (_, _) => await ChooseImageAsync(), (_, e) => e.CanExecute = job == null && Completion.Visibility != Visibility.Visible));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, async (_, _) => await SaveAsync(false), (_, e) => e.CanExecute = source != null && job == null && Completion.Visibility != Visibility.Visible));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, new KeyGesture(Key.S, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Print, new KeyGesture(Key.P, ModifierKeys.Control)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Print, async (_, _) => await PrintAsync(), (_, e) => e.CanExecute = PrintButton.IsEnabled));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, async (_, _) => await ApplyHistoryAsync(false), (_, e) => e.CanExecute = source != null && job == null && Completion.Visibility != Visibility.Visible && undoStates.Count > 0));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, async (_, _) => await ApplyHistoryAsync(true), (_, e) => e.CanExecute = source != null && job == null && Completion.Visibility != Visibility.Visible && redoStates.Count > 0));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Undo, new KeyGesture(Key.Z, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Redo, new KeyGesture(Key.Z, ModifierKeys.Control | ModifierKeys.Shift)));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Completion.Visibility == Visibility.Visible)
            { Completion.Visibility = Visibility.Collapsed; e.Handled = true; }
        };
    }

    private async void LoadedAsync(object sender, RoutedEventArgs e)
    {
        App.FitOnScreen(this);
        UpdateRecent();
        if (arguments.Contains("--checks")) return;
        pendingRecovery = recovery.ClaimPending();
        if (pendingRecovery != null) ShowNotice(true);
        var path = arguments.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        if (path != null)
        {
            if (Path.GetExtension(path).Equals(".mosaic", StringComparison.OrdinalIgnoreCase)) await OpenProjectAsync(path);
            else await ImportAsync(path);
        }
    }
    private void ShowNotice(bool recovery)
    {
        recovering = recovery;
        if (!recovery) { RefreshPrintGuidance(); return; }
        NoticeText.Text = Lang.T(recovery ? "Recover" : "CalibrationPrompt");
        NoticeAction.Content = Lang.T(recovery ? "Restore" : "Calibration");
        NoticeBar.Visibility = Visibility.Visible;
    }
    private async void NoticeActionClick(object sender, RoutedEventArgs e)
    {
        if (recovering) await RestoreRecoveryAsync();
        else ShowCalibration();
    }
    private async Task<bool> RestoreRecoveryAsync()
    {
        if (pendingRecovery == null || !await AllowReplaceAsync()) return false;
        if (recoveryTask != null) await recoveryTask;
        if (!await OpenProjectAsync(pendingRecovery.Path, true)) return false;
        pendingRecoveryRestored = true;
        NoticeBar.Visibility = Visibility.Collapsed;
        // Preserve the prior snapshot until its replacement is safely checkpointed.
        recoveryTask = RecoverEditsAsync();
        await recoveryTask;
        return true;
    }
    private void NoticeLaterClick(object sender, RoutedEventArgs e)
    {
        NoticeBar.Visibility = Visibility.Collapsed;
        if (!recovering) { preferences = preferences with { CalibrationPromptDismissed = true }; SavePreferences(); }
    }
    private void CalibrationLaterClick(object sender, RoutedEventArgs e)
    {
        preferences = preferences with { CalibrationPromptDismissed = true };
        SavePreferences(); RefreshPrintGuidance();
    }

    private async void OpenImageClick(object sender, RoutedEventArgs e) => await ChooseImageAsync();
    private async void RemoveImageClick(object sender, RoutedEventArgs e) => await RemoveImageAsync();
    private ProjectState EmptyProject() => new()
    {
        Paper = state.Paper, PrinterProfile = state.PrinterProfile, PrinterName = state.PrinterName,
        Grayscale = state.Grayscale, Copies = state.Copies, AssemblyGuide = state.AssemblyGuide
    };
    private async Task<bool> RemoveImageAsync()
    {
        if (source == null || Completion.IsVisible || !await AllowReplaceAsync()) return false;
        bool removed = await RunJobAsync("Removing", async token =>
        {
            // Let an in-flight checkpoint finish before resolving this document's recovery.
            // A deferred recovery belonging to an unrelated session is deliberately retained.
            await ResolveDocumentRecoveryAsync(token);
            state = EmptyProject();
            source = null; sourceLease?.Dispose(); sourceLease = null;
            layout = null; enhancedPath = projectPath = null; compareOriginal = false;
            completedPdf = completedPrinter = null; completedSnapshot = null; completionFocus = null;
            Completion.Visibility = Visibility.Collapsed;
            CompletionFile.Text = CompletionDetails.Text = CompletionBody.Text = CompletionTitle.Text = "";
            Preview.Image = null; Preview.Layout = null; Preview.Composition = new();
            Preview.SourceWidth = Preview.SourceHeight = 0; Preview.Fit();
            dimensionInvalid = marginInvalid = layoutInvalid = false;
            dirty = false; revision++; recoveredRevision = -1;
            QualityBadge.Text = ResolutionHint.Text = CandidateSummary.Text = GridSummary.Text = PaperSummary.Text = PrintHint.Text = "";
            Summary.ToolTip = GridSummary.ToolTip = CompletionDetails.ToolTip = null;
            ResolutionHint.Visibility = Visibility.Collapsed;
            SyncControls(); ResetHistory(); ShowValidation();
            Title = Lang.T("AppName");
        });
        if (removed) { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); WelcomeOpenButton.Focus(); }
        return removed;
    }
    private async Task ChooseImageAsync()
    {
        if (job != null || !await AllowReplaceAsync()) return;
        var dialog = new OpenFileDialog { Filter = Lang.T("ImageFilter"), Title = Lang.T("OpenImage") };
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) dialog.InitialDirectory = downloads;
        if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileName);
    }
    private async Task ImportAsync(string path)
    {
        await RunJobAsync("Importing", async token =>
        {
            var imported = await images.ImportAsync(path, token);
            IDisposable? importedLease = await images.PreserveSourceAsync(imported.Sha256, token);
            try
            {
                var preview = await images.GetPreviewAsync(await images.GetOutputImageAsync(imported, ct: token), ct: token);
                await ResolveDocumentRecoveryAsync(token);
                token.ThrowIfCancellationRequested();
                sourceLease?.Dispose(); sourceLease = importedLease; importedLease = null;
                source = imported; enhancedPath = projectPath = null;
                dimensionInvalid = marginInvalid = layoutInvalid = false; ShowValidation();
                state = EmptyProject() with { Title = Path.GetFileNameWithoutExtension(path), Size = LayoutEngine.Proportional(24, true, imported.Width, imported.Height) };
                if (state.Size.HeightInches > 120) state = state with { Size = LayoutEngine.Proportional(24, false, imported.Width, imported.Height) };
                Preview.Image = preview; Preview.Fit(); compareOriginal = false;
                dirty = true; revision++;
                SyncControls(); ResetHistory(); Completion.Visibility = Visibility.Collapsed; Status.Text = Lang.T("ImageReady");
            }
            finally { importedLease?.Dispose(); }
        });
    }
    private async void FileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1 || job != null || !await AllowReplaceAsync()) return;
        if (Path.GetExtension(files[0]).Equals(".mosaic", StringComparison.OrdinalIgnoreCase)) await OpenProjectAsync(files[0]);
        else await ImportAsync(files[0]);
    }
    private void FileDragOver(object sender, DragEventArgs e) { e.Effects = job == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }

    private void SyncControls()
    {
        synchronizing = true;
        try
        {
            if (!dimensionInvalid)
            {
                WidthBox.Text = state.Size.WidthInches.ToString("0.####", Lang.Culture);
                HeightBox.Text = state.Size.HeightInches.ToString("0.####", Lang.Culture);
                widthTextEdited = heightTextEdited = false;
            }
            LockCheck.IsChecked = state.KeepProportions;
            FitBox.SelectedIndex = (int)state.Composition.Mode;
            CropX.Value = (double)state.Composition.CropX; CropY.Value = (double)state.Composition.CropY;
            OrientationBox.SelectedIndex = (int)state.Paper.Orientation;
            if (!marginInvalid) MarginBox.Text = state.Paper.MarginInches.ToString("0.####", Lang.Culture);
            ProfileBox.SelectedIndex = state.PrinterProfile == "canon-mf210" ? 1 : 0;
            CopiesBox.SelectedIndex = Math.Clamp(state.Copies - 1, 0, 4);
            GuideCheck.IsChecked = state.AssemblyGuide; GrayscaleCheck.IsChecked = state.Grayscale;
            StandardRadio.IsChecked = state.Quality == QualityMode.Standard;
            EnhancedRadio.IsChecked = state.Quality == QualityMode.Enhanced;
            CompareButton.Visibility = enhancedPath != null ? Visibility.Visible : Visibility.Collapsed;
            SourceDetails.Text = source == null ? "" : $"{source.Width:N0} × {source.Height:N0} px · {source.Format}";
            ArtworkTitle.Text = source == null ? "" : state.Title;
            SizeHelp.Text = Lang.T(!state.KeepProportions ? "IndependentSize" : state.LastDimension == "height" ? "WidthAuto" : "HeightAuto");
            AutomationProperties.SetHelpText(WidthBox, SizeHelp.Text);
            AutomationProperties.SetHelpText(HeightBox, SizeHelp.Text);
            CompositionPanel.Visibility = state.KeepProportions ? Visibility.Collapsed : Visibility.Visible;
            UpdateCompositionHelp();
            RefreshLayout();
        }
        finally { synchronizing = false; }
        RefreshEnabled();
    }
    private void RefreshLayout()
    {
        if (source == null)
        {
            Summary.Text = Lang.T("ReadyEmpty"); Status.Text = "";
            EditorColumn.Width = new GridLength(0); EditorSidebar.Visibility = Visibility.Collapsed;
            Preview.Visibility = PreviewHeader.Visibility = PreviewTools.Visibility = OpenImageButton.Visibility = Visibility.Collapsed;
            WelcomeScroll.Visibility = Visibility.Visible;
            PaperSummary.Visibility = Status.Visibility = Visibility.Collapsed;
            RefreshPrintGuidance(); return;
        }
        layout = LayoutEngine.Calculate(state.Size, state.Paper);
        state = state with { ResolvedOrientation = layout.Orientation };
        Preview.Layout = layout; Preview.Composition = state.Composition;
        Preview.SourceWidth = state.Composition.Rotation % 180 == 0 ? source.Width : source.Height;
        Preview.SourceHeight = state.Composition.Rotation % 180 == 0 ? source.Height : source.Width;
        Preview.InvalidateVisual();
        Summary.Text = Lang.F("TargetSummary", Lang.F("Summary", DisplayInches(state.Size.WidthInches), DisplayInches(state.Size.HeightInches), layout.PageCount, Lang.T(layout.Orientation.ToString())));
        Summary.ToolTip = ExactSize(state.Size);
        PaperSummary.Text = Lang.F("SummaryPaper", state.PrinterProfile == "canon-mf210" ? "Canon MF210" : "Epson ET-8500", state.Paper.MarginInches == 0 ? Lang.T("Borderless") : Lang.F("TrimMargins", state.Paper.MarginInches.ToString("0.###", Lang.Culture)))
            + (!state.KeepProportions ? " · " + Lang.T(state.Composition.Mode.ToString()) : "");
        CandidateSummary.Text = Lang.F("Candidates", layout.PortraitCount, layout.LandscapeCount);
        GridSummary.Text = Lang.F("PreviewPaperSummary", layout.Columns, layout.Rows);
        GridSummary.ToolTip = Lang.F("GridInfo", layout.Columns, layout.Rows);
        PrintHint.Text = Lang.T(state.Paper.MarginInches > 0 ? "TrimHint" : "BorderlessHint");
        QualityBadge.Text = Lang.T(compareOriginal ? "Original" : state.Quality.ToString());
        var effectiveWidth = state.Composition.Rotation % 180 == 0 ? source.Width : source.Height;
        // Interpolated/inferred pixels are not measured source detail.
        var ppi = effectiveWidth / (double)state.Size.WidthInches;
        ResolutionHint.Text = ppi < 120 ? Lang.T("Soft") : "";
        ResolutionHint.Visibility = ppi < 120 ? Visibility.Visible : Visibility.Collapsed;
        EditorColumn.Width = new GridLength(360); EditorSidebar.Visibility = Visibility.Visible;
        WelcomeScroll.Visibility = Visibility.Collapsed;
        Preview.Visibility = PreviewHeader.Visibility = PreviewTools.Visibility = OpenImageButton.Visibility = Visibility.Visible;
        PaperSummary.Visibility = Status.Visibility = Visibility.Visible;
        RefreshPrintGuidance();
        Title = (dirty ? "● " : "") + state.Title + " — Mosaic Template Studio";
    }
    private void RefreshPrintGuidance()
    {
        PrintGuidance.Visibility = source != null ? Visibility.Visible : Visibility.Collapsed;
        bool measured = layout != null && preferences.Calibrations.Any(c => c.Enabled && !c.EdgeClipping && c.Pathway == "direct" && c.PrinterName == state.PrinterName && c.Profile == state.PrinterProfile && c.Orientation == layout.Orientation && !string.IsNullOrWhiteSpace(c.DriverFingerprint));
        CalibrationStatus.Text = Lang.T(measured ? "Calibrated" : "NotMeasured");
        CalibrationPromptText.Visibility = CalibrationLaterButton.Visibility = source != null && !measured && !preferences.CalibrationPromptDismissed ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshEnabled()
    {
        bool completion = Completion.Visibility == Visibility.Visible;
        bool idle = job == null && !closeRequested && !closing;
        bool editable = source != null && idle && !completion;
        OrientationBox.IsEnabled = editable; LanguageBox.IsEnabled = idle;
        EditorRail.IsEnabled = editable;
        OpenImageButton.IsEnabled = WelcomeOpenButton.IsEnabled = ProjectButton.IsEnabled = idle && !completion;
        NoticeBar.IsEnabled = idle;
        Preview.IsEnabled = PreviewHeader.IsEnabled = PreviewTools.IsEnabled = editable;
        CalibrationButton.IsEnabled = CalibrationLaterButton.IsEnabled = editable;
        PrintButton.IsEnabled = ExportButton.IsEnabled = editable && !dimensionInvalid && !marginInvalid && !layoutInvalid;
    }
    private void Changed()
    {
        if (!initialized || synchronizing) return;
        if (source != null && state != historyAnchor)
        {
            undoStates.Add(historyAnchor);
            if (undoStates.Count > 50) undoStates.RemoveAt(0);
            redoStates.Clear();
        }
        dirty = true; revision++; Completion.Visibility = Visibility.Collapsed;
        RefreshLayout(); historyAnchor = state; RefreshEnabled(); Status.Text = Lang.T("SizeReady");
    }
    private void ResetHistory() { undoStates.Clear(); redoStates.Clear(); historyAnchor = state; }
    private async Task ApplyHistoryAsync(bool redo)
    {
        var from = redo ? redoStates : undoStates;
        var to = redo ? undoStates : redoStates;
        if (job != null || source == null || from.Count == 0) return;
        var previous = from[^1];
        await RunJobAsync("Working", async token =>
        {
            var imagePath = await images.GetOutputImageAsync(source, previous.Composition.Rotation, previous.Quality == QualityMode.Enhanced ? enhancedPath : null, token);
            var bitmap = await images.GetPreviewAsync(imagePath, ct: token);
            to.Add(state); from.RemoveAt(from.Count - 1); state = previous;
            dimensionInvalid = marginInvalid = layoutInvalid = false; dirty = true; revision++;
            Preview.Image = bitmap; SyncControls(); historyAnchor = state; ShowValidation();
            Completion.Visibility = Visibility.Collapsed;
        });
    }
    private bool CommitDimension(TextBox box)
    {
        if (source == null) return true;
        if (box == WidthBox ? !widthTextEdited : !heightTextEdited) return !dimensionInvalid;
        if (!LayoutEngine.TryInches(box.Text, Lang.Culture, out var value))
        { dimensionInvalid = true; ShowValidation(); return false; }
        bool width = box == WidthBox;
        int sw = state.Composition.Rotation % 180 == 0 ? source.Width : source.Height;
        int sh = state.Composition.Rotation % 180 == 0 ? source.Height : source.Width;
        var size = state.KeepProportions ? LayoutEngine.Proportional(value, width, sw, sh)
            : width ? state.Size with { WidthInches = value } : state.Size with { HeightInches = value };
        if (state.Size == size && state.LastDimension == (width ? "width" : "height")) { dimensionInvalid = false; ShowValidation(); return true; }
        if (!TryCommitLayout(state with { Size = size, LastDimension = width ? "width" : "height" })) return false;
        Changed(); SyncControls(); return true;
    }
    private bool TryCommitLayout(ProjectState candidate, bool sizeChange = true)
    {
        LayoutPlan candidateLayout;
        try { candidateLayout = LayoutEngine.Calculate(candidate.Size, candidate.Paper); }
        catch (ArgumentOutOfRangeException)
        {
            if (sizeChange) dimensionInvalid = true; else layoutInvalid = true;
            ShowValidation(); return false;
        }
        state = candidate with { ResolvedOrientation = candidateLayout.Orientation };
        layout = candidateLayout;
        if (sizeChange) dimensionInvalid = false;
        layoutInvalid = false; ShowValidation();
        return true;
    }
    private void DimensionsCommitted(object sender, KeyboardFocusChangedEventArgs e) { if (!synchronizing && initialized) CommitDimension((TextBox)sender); }
    private void DimensionTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || synchronizing) return;
        if (sender == WidthBox) widthTextEdited = true; else heightTextEdited = true;
    }
    private void ShowValidation()
    {
        SizeError.Text = Lang.T(layoutInvalid ? "InvalidLayout" : dimensionInvalid ? "InvalidSize" : "InvalidMargin");
        SizeError.Visibility = dimensionInvalid || marginInvalid || layoutInvalid ? Visibility.Visible : Visibility.Collapsed;
        if (SizeError.Visibility == Visibility.Visible) Status.Text = Lang.T("ReviewInputs");
        else if (source != null && Status.Text == Lang.T("ReviewInputs")) Status.Text = Lang.T("ReviewReady");
        RefreshEnabled();
    }
    private bool CommitPendingInputs()
    {
        if (source == null) return false;
        bool widthValid = LayoutEngine.TryInches(WidthBox.Text, Lang.Culture, out _);
        bool heightValid = LayoutEngine.TryInches(HeightBox.Text, Lang.Culture, out _);
        if (!widthValid || !heightValid) { dimensionInvalid = true; ShowValidation(); return false; }
        if (Keyboard.FocusedElement == WidthBox) { if (!CommitDimension(WidthBox)) return false; }
        else if (Keyboard.FocusedElement == HeightBox) { if (!CommitDimension(HeightBox)) return false; }
        else if (widthTextEdited) { if (!CommitDimension(WidthBox)) return false; }
        else if (heightTextEdited) { if (!CommitDimension(HeightBox)) return false; }
        if (dimensionInvalid) return false;
        return CommitMargin() && !layoutInvalid;
    }
    private void DimensionKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitDimension((TextBox)sender); e.Handled = true; } }
    private void LockChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || synchronizing || source == null) return;
        var candidate = state with { KeepProportions = LockCheck.IsChecked == true, Composition = state.Composition with { Mode = FitMode.Fit } };
        if (candidate.KeepProportions)
        {
            int w = state.Composition.Rotation % 180 == 0 ? source.Width : source.Height;
            int h = state.Composition.Rotation % 180 == 0 ? source.Height : source.Width;
            bool width = state.LastDimension != "height";
            candidate = candidate with { Size = LayoutEngine.Proportional(width ? state.Size.WidthInches : state.Size.HeightInches, width, w, h) };
        }
        if (!TryCommitLayout(candidate))
        {
            synchronizing = true;
            try { LockCheck.IsChecked = state.KeepProportions; }
            finally { synchronizing = false; }
            return;
        }
        Changed(); SyncControls();
    }
    private void CompositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || synchronizing || FitBox.SelectedIndex < 0) return;
        state = state with { Composition = state.Composition with { Mode = (FitMode)FitBox.SelectedIndex } };
        UpdateCompositionHelp(); Changed();
    }
    private void UpdateCompositionHelp() { FitHelp.Text = Lang.T(state.Composition.Mode + "Hint"); CropPanel.Visibility = state.Composition.Mode == FitMode.Crop ? Visibility.Visible : Visibility.Collapsed; }
    private void CropChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized || synchronizing) return;
        state = state with { Composition = state.Composition with { CropX = (decimal)CropX.Value, CropY = (decimal)CropY.Value } }; Changed();
    }
    private void PaperChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || synchronizing || OrientationBox.SelectedIndex < 0) return;
        if (!TryCommitLayout(state with { Paper = state.Paper with { Orientation = (OrientationMode)OrientationBox.SelectedIndex } }, false))
        {
            synchronizing = true;
            try { OrientationBox.SelectedIndex = (int)state.Paper.Orientation; }
            finally { synchronizing = false; }
            return;
        }
        Changed();
    }
    private void MarginCommitted(object sender, KeyboardFocusChangedEventArgs e) => CommitMargin();
    private void MarginKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitMargin(); }
    private bool CommitMargin()
    {
        if (!initialized || synchronizing) return true;
        if (!decimal.TryParse(MarginBox.Text, NumberStyles.AllowDecimalPoint, Lang.Culture, out var value) || value < 0 || value > 2)
        { marginInvalid = true; ShowValidation(); return false; }
        marginInvalid = false; ShowValidation();
        if (state.Paper.MarginInches != value)
        {
            if (!TryCommitLayout(state with { Paper = state.Paper with { MarginInches = value } }, false)) return false;
            Changed();
        }
        return true;
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || synchronizing) return;
        bool canon = ProfileBox.SelectedIndex == 1;
        marginInvalid = false; ShowValidation();
        if (!TryCommitLayout(state with { PrinterProfile = canon ? "canon-mf210" : "epson-et8500", PrinterName = null, Paper = state.Paper with { MarginInches = canon ? .25m : 0 } }, false)) return;
        Changed(); SyncControls();
    }
    private void OptionChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || synchronizing) return;
        state = state with { Grayscale = GrayscaleCheck.IsChecked == true, AssemblyGuide = GuideCheck.IsChecked == true }; Changed();
    }
    private void CopiesChanged(object sender, SelectionChangedEventArgs e) { if (!initialized || synchronizing) return; state = state with { Copies = CopiesBox.SelectedIndex + 1 }; Changed(); }
    private async void RotateClick(object sender, RoutedEventArgs e)
    {
        if (source == null || job != null) return;
        await RunJobAsync("Working", async token =>
        {
            int rotation = (state.Composition.Rotation + 90) % 360;
            var image = await OutputPreviewAsync(state.Quality, rotation, token);
            var candidate = state with { Composition = state.Composition with { Rotation = rotation } };
            if (state.KeepProportions) candidate = candidate with { Size = new(state.Size.HeightInches, state.Size.WidthInches) };
            if (!TryCommitLayout(candidate, false)) return;
            Preview.Image = image; Preview.Fit(); Changed(); SyncControls();
        });
    }
    private async Task<System.Windows.Media.Imaging.BitmapSource> OutputPreviewAsync(QualityMode quality, int rotation, CancellationToken token, int maxEdge = 1800)
    {
        if (source == null) throw new InvalidOperationException();
        var path = await images.GetOutputImageAsync(source, rotation, quality == QualityMode.Enhanced ? enhancedPath : null, token);
        return await images.GetPreviewAsync(path, maxEdge: maxEdge, ct: token);
    }
    private async void QualityChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || synchronizing || source == null || job != null) return;
        if (EnhancedRadio.IsChecked == true && enhancedPath == null) { await EnhanceAsync(); return; }
        var chosen = EnhancedRadio.IsChecked == true ? QualityMode.Enhanced : QualityMode.Standard;
        await RunJobAsync("Working", async token =>
        {
            var bitmap = await OutputPreviewAsync(chosen, state.Composition.Rotation, token);
            state = state with { Quality = chosen, EnhancementModel = chosen == QualityMode.Enhanced ? EnhancementService.ModelId : state.EnhancementModel }; compareOriginal = false; Preview.Image = bitmap; Changed(); SyncControls();
        });
        SyncControls();
    }
    private async Task EnhanceAsync()
    {
        if (source == null || job != null) return;
        await RunJobAsync("Enhancing", async token =>
        {
            var result = await enhancement.EnhanceAsync(source, JobProgress(), token, CpuCheck.IsChecked == true);
            var output = await images.GetOutputImageAsync(source, state.Composition.Rotation, result, token);
            var image = await images.GetPreviewAsync(output, ct: token);
            enhancedPath = result; compareOriginal = false; state = state with { Quality = QualityMode.Enhanced, EnhancementModel = EnhancementService.ModelId };
            Preview.Image = image; Changed(); SyncControls(); Status.Text = Lang.T("EnhancedReady") + " · " + Lang.T(enhancement.LastBackend == "CPU" ? "BackendCpu" : enhancement.LastBackend == "cache" ? "BackendCache" : "BackendGpu");
        });
        if (enhancedPath == null) { state = state with { Quality = QualityMode.Standard }; SyncControls(); }
    }
    private async void CompareClick(object sender, RoutedEventArgs e) => await CompareAsync();
    private async Task CompareAsync()
    {
        if (enhancedPath == null || source == null || job != null) return;
        await RunJobAsync("Working", async token =>
        {
            // Capture both actual output assets and retain their source even if the
            // editor later opens another document while this inspection is visible.
            string standard = await images.GetOutputImageAsync(source, state.Composition.Rotation, ct: token);
            string enhanced = await images.GetOutputImageAsync(source, state.Composition.Rotation, enhancedPath, token);
            var originalView = await images.GetPreviewAsync(standard, ct: token);
            var enhancedView = await images.GetPreviewAsync(enhanced, ct: token);
            int nativeWidth = await Task.Run(() => { using var input = File.OpenRead(enhanced); return checked((int)new ImageMagick.MagickImageInfo(input).Width); }, token);
            var lease = await images.PreserveSourceAsync(source.Sha256, token);
            try
            {
                var window = new ComparisonWindow(this, originalView, enhancedView, Lang.Language == "pl",
                    async (region, ct) => (await images.GetInspectionRegionAsync(standard, region, ct: ct), await images.GetInspectionRegionAsync(enhanced, region, ct: ct)), nativeWidth) { Title = Lang.T("Compare") };
                window.Closed += (_, _) => lease.Dispose();
                window.Show();
            }
            catch { lease.Dispose(); throw; }
        });
    }
    private void FitViewClick(object sender, RoutedEventArgs e) => Preview.Fit();
    private async void DetailClick(object sender, RoutedEventArgs e)
    {
        if (source == null || job != null) return;
        await RunJobAsync("Working", async token => { Preview.Image = await OutputPreviewAsync(state.Quality, state.Composition.Rotation, token, 3600); Preview.Detail(); });
    }
    private void GridChanged(object sender, RoutedEventArgs e) { if (Preview == null) return; Preview.ShowGrid = GridCheck.IsChecked == true; Preview.InvalidateVisual(); }

    private async Task<RenderSnapshot> SnapshotAsync(CancellationToken token)
    {
        if (source == null || layout == null) throw new InvalidOperationException();
        if (dimensionInvalid || marginInvalid || layoutInvalid || layout.Artwork != state.Size || layout.Paper != state.Paper || layout.Orientation != state.ResolvedOrientation)
            throw new InvalidOperationException(Lang.T("InvalidSize"));
        if (state.Quality == QualityMode.Enhanced && enhancedPath == null)
            enhancedPath = await enhancement.EnhanceAsync(source, JobProgress(), token, CpuCheck.IsChecked == true);
        if (state.Quality == QualityMode.Enhanced && state.EnhancementModel != EnhancementService.ModelId)
        { state = state with { EnhancementModel = EnhancementService.ModelId }; Changed(); }
        var outputPath = await images.GetOutputImageAsync(source, state.Composition.Rotation, state.Quality == QualityMode.Enhanced ? enhancedPath : null, token);
        return new(source, outputPath, state, layout);
    }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (source == null || job != null || !CommitPendingInputs()) return;
        var dialog = new SaveFileDialog { Filter = Lang.T("PdfFilter"), DefaultExt = ".pdf", FileName = DefaultPdfName(state) };
        if (dialog.ShowDialog(this) != true) return;
        await RunJobAsync("Exporting", async token =>
        {
            var snapshot = await SnapshotAsync(token);
            var result = await pdf.ExportAsync(snapshot, dialog.FileName, Lang.Language, JobProgress(), token);
            completedPdf = dialog.FileName; completedSnapshot = snapshot; completedPrinter = null;
            RefreshCompletion();
        });
    }
    private async void PrintClick(object sender, RoutedEventArgs e) => await PrintAsync();
    private async Task PrintAsync()
    {
        if (source == null || job != null || !CommitPendingInputs()) return;
        await RunJobAsync("Printing", async token =>
        {
            var snapshot = await SnapshotAsync(token);
            var result = await printer.PrintAsync(snapshot, this, Lang.Language, preferences.Calibrations, JobProgress(), token);
            if (!result.Submitted) return;
            state = state with { PrinterName = result.PrinterName }; Changed();
            completedSnapshot = snapshot; completedPrinter = result.PrinterName;
            RefreshCompletion();
        });
    }
    private void RefreshCompletion()
    {
        if (completedSnapshot == null) return;
        var saved = completedSnapshot.State;
        string summary = Lang.F("Summary", DisplayInches(saved.Size.WidthInches), DisplayInches(saved.Size.HeightInches), completedSnapshot.Layout.PageCount, Lang.T(completedSnapshot.Layout.Orientation.ToString()));
        bool exported = completedPrinter == null && completedPdf != null;
        CompletionTitle.Text = Lang.T(exported ? "ExportedTitle" : "SentTitle");
        CompletionFile.Text = exported ? Path.GetFileName(completedPdf!) : completedPrinter;
        CompletionDetails.Text = summary + (exported ? " · " + (new FileInfo(completedPdf!).Length / 1048576d).ToString("0.0", Lang.Culture) + " MB" : "");
        CompletionDetails.ToolTip = ExactSize(saved.Size);
        CompletionBody.Text = exported ? Lang.T("ActualSize") + (saved.Paper.MarginInches > 0 ? "\n" + Lang.T("TrimHint") : "") + (saved.Composition.Mode == FitMode.Fit && !saved.KeepProportions ? "\n" + Lang.T("FitHint") : "") : Lang.T("SentBody");
        OpenPdfButton.Visibility = ShowFolderButton.Visibility = exported ? Visibility.Visible : Visibility.Collapsed;
        Completion.Visibility = Visibility.Visible;
    }
    private IProgress<JobProgress> JobProgress() => new Progress<JobProgress>(p => { JobBar.IsIndeterminate = p.Fraction < 0 && SystemParameters.ClientAreaAnimation; JobBar.Value = p.Fraction >= 0 ? Math.Clamp(p.Fraction * 100, 0, 100) : 0; });
    private async Task<bool> RunJobAsync(string label, Func<CancellationToken, Task> action, bool duringClose = false)
    {
        if (job != null || closing || closeRequested && !duringClose) return false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        jobCompletion = completion;
        job = new CancellationTokenSource(); BusyText.Text = Lang.T(label); JobBar.Value = 0; JobBar.IsIndeterminate = SystemParameters.ClientAreaAnimation; BusyBar.Visibility = Visibility.Visible;
        RefreshEnabled(); LocalLog.Write(label);
        try { await action(job.Token); LocalLog.Write(label + "_ok"); return true; }
        catch (OperationCanceledException) { Status.Text = Lang.T("Cancelled"); return false; }
        catch (OutputException ex)
        {
            LocalLog.Write(label + "_failed", ex.Code);
            if (ex.SuggestedMarginInches is decimal margin && margin <= 2 && QuestionWindow.Ask(this, Lang.F("TrimOffer", margin.ToString("0.###", Lang.Culture)), false) == UserChoice.Apply)
            { if (TryCommitLayout(state with { Paper = state.Paper with { MarginInches = margin } }, false)) { Changed(); SyncControls(); } }
            else MessageBox.Show(this, ex.Message, Lang.T("ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            LocalLog.Failure(label + "_failed", ex);
            string key = ex switch
            {
                ImagePipelineException { Code: "cacheFull" or "diskFull" } => "CacheFull",
                ImagePipelineException => "ImageError",
                EnhancementException { Code: "cacheFull" or "diskFull" } => "CacheFull",
                EnhancementException { Code: "aiSourceTooLarge" } => "AiLimit",
                EnhancementException { Code: "aiIntegrity" or "aiMissing" } => "AiIntegrity",
                EnhancementException => "AiError",
                ProjectStorageException { Code: "StorageFull" } => "CacheFull",
                ProjectStorageException { Code: "ProjectSaveFailed" } => "SaveError",
                ProjectStorageException { Code: "RecoveryCleanupFailed" } => "RecoveryError",
                ProjectStorageException { Code: "SourceChanged" or "SourceUnavailable" } => "SourceChanged",
                ProjectStorageException => "ProjectError",
                _ => "Unexpected"
            };
            if (ErrorPresenter != null) ErrorPresenter(Lang.T(key));
            else MessageBox.Show(this, Lang.T(key), Lang.T("ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            job.Dispose(); job = null; BusyBar.Visibility = Visibility.Collapsed; RefreshEnabled();
            jobCompletion = null; completion.SetResult();
        }
    }
    private void CancelClick(object sender, RoutedEventArgs e) => job?.Cancel();

    private void ProjectMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Add(string key, Func<Task> action, bool enabled = true) { var item = new MenuItem { Header = Lang.T(key), IsEnabled = enabled }; item.Click += async (_, _) => await action(); menu.Items.Add(item); }
        Add("OpenProject", async () => { if (job != null || !await AllowReplaceAsync()) return; var d = new OpenFileDialog { Filter = Lang.T("ProjectFilter") }; if (d.ShowDialog(this) == true) await OpenProjectAsync(d.FileName); }, job == null);
        Add("Save", () => SaveAsync(false), source != null && job == null); Add("SaveAs", () => SaveAsync(true), source != null && job == null);
        menu.Items.Add(new Separator());
        Add("Undo", () => ApplyHistoryAsync(false), source != null && job == null && undoStates.Count > 0);
        Add("Redo", () => ApplyHistoryAsync(true), source != null && job == null && redoStates.Count > 0);
        menu.Items.Add(new Separator());
        foreach (var path in preferences.RecentProjects)
        {
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(path), ToolTip = path, IsEnabled = job == null };
            item.Click += async (_, _) => { if (await AllowReplaceAsync()) await OpenProjectAsync(path); }; menu.Items.Add(item);
        }
        if (preferences.RecentProjects.Count == 0) menu.Items.Add(new MenuItem { Header = Lang.T("NoRecent"), IsEnabled = false });
        menu.Items.Add(new Separator());
        Add("About", () => { AboutClick(this, new RoutedEventArgs()); return Task.CompletedTask; }, job == null);
        ProjectButton.ContextMenu = menu; menu.PlacementTarget = ProjectButton; menu.IsOpen = true;
    }
    private async Task<bool> SaveAsync(bool saveAs, bool duringClose = false)
    {
        if (source == null || job != null || closing || closeRequested && !duringClose || !CommitPendingInputs()) return false;
        var path = saveAs ? null : projectPath;
        if (path == null)
        {
            string name = SafeFileName(state.Title) + ".mosaic";
            if (SavePathChooser != null) path = SavePathChooser(name);
            else
            {
                var d = new SaveFileDialog { Filter = Lang.T("ProjectFilter"), DefaultExt = ".mosaic", FileName = name };
                if (d.ShowDialog(this) == true) path = d.FileName;
            }
            if (path == null) return false;
        }
        var target = path;
        return await RunJobAsync("Saving", async token =>
        {
            await projects.SaveAsync(target, state, source, token);
            await ResolveDocumentRecoveryAsync(token);
            projectPath = target; dirty = false;
            preferences = settings.AddRecent(target); UpdateRecent(); RefreshLayout(); Status.Text = Lang.T("Saved");
        }, duringClose);
    }
    private async Task<bool> OpenProjectAsync(string path, bool recovered = false)
    {
        return await RunJobAsync("Opening", async token =>
        {
            var opened = await projects.OpenAsync(path, token);
            var imported = await images.ImportAsync(opened.OriginalPath, token);
            IDisposable? importedLease = await images.PreserveSourceAsync(imported.Sha256, token);
            try
            {
                var image = await images.GetPreviewAsync(await images.GetOutputImageAsync(imported, opened.State.Composition.Rotation, ct: token), ct: token);
                var openedState = opened.State; string? openedEnhanced = null;
                // Derived AI caches are reproducible and remain outside portable projects.
                bool aiFallback = false, aiUpdated = false;
                if (openedState.Quality == QualityMode.Enhanced)
                {
                    try
                    {
                        openedEnhanced = await enhancement.EnhanceAsync(imported, JobProgress(), token);
                        image = await images.GetPreviewAsync(await images.GetOutputImageAsync(imported, openedState.Composition.Rotation, openedEnhanced, token), ct: token);
                        aiUpdated = openedState.EnhancementModel != EnhancementService.ModelId;
                        openedState = openedState with { EnhancementModel = EnhancementService.ModelId };
                    }
                    catch (EnhancementException) { openedState = openedState with { Quality = QualityMode.Standard }; aiFallback = true; }
                }
                await ResolveDocumentRecoveryAsync(token);
                token.ThrowIfCancellationRequested();
                sourceLease?.Dispose(); sourceLease = importedLease; importedLease = null;
                source = imported; state = openedState; enhancedPath = openedEnhanced; compareOriginal = false;
                projectPath = recovered ? null : path; dirty = recovered || aiFallback || aiUpdated; revision++; Preview.Image = image; Preview.Fit();
                if (!recovered) preferences = settings.AddRecent(path);
                dimensionInvalid = marginInvalid = layoutInvalid = false; ShowValidation();
                SyncControls(); ResetHistory(); UpdateRecent(); Completion.Visibility = Visibility.Collapsed; Status.Text = Lang.T(aiFallback ? "AiError" : aiUpdated ? "AiRecipeUpdated" : "Ready");
            }
            finally { importedLease?.Dispose(); }
        });
    }
    private async Task<bool> AllowReplaceAsync(bool duringClose = false)
    {
        if (job != null || closing || closeRequested && !duringClose) return false;
        bool pendingInputs = widthTextEdited || heightTextEdited || dimensionInvalid || marginInvalid || layoutInvalid ||
            !decimal.TryParse(MarginBox.Text, NumberStyles.AllowDecimalPoint, Lang.Culture, out var margin) || margin != state.Paper.MarginInches;
        if (source == null || !dirty && !pendingInputs) return true;
        var answer = QuestionWindow.Ask(this, Lang.T("Dirty"), true);
        return answer == UserChoice.Discard || answer == UserChoice.Save && await SaveAsync(false, duringClose);
    }
    private async Task RecoverEditsAsync()
    {
        if (source == null || !dirty || revision == recoveredRevision || job != null || closeRequested || closing) return;
        var snapshotRevision = revision; var savedState = state; var savedSource = source;
        try
        {
            await projects.SaveAsync(recovery.RecoveryPath, savedState, savedSource, recoveryCancellation.Token);
            recoveredRevision = snapshotRevision;
            if (pendingRecoveryRestored) { pendingRecovery?.Resolve(); pendingRecovery = null; pendingRecoveryRestored = false; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LocalLog.Failure("recovery_failed", ex); }
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (closeRequested) return;
        closeRequested = true;
        closeTask = CloseWhenReadyAsync();
    }
    private async Task CloseWhenReadyAsync()
    {
        // Always leave WPF's Closing event before trying Close again, even when
        // the document is clean and every awaited operation completes inline.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        try
        {
            RefreshEnabled();
            var activeJob = jobCompletion?.Task;
            job?.Cancel();
            if (activeJob != null) await activeJob;
            if (!await AllowReplaceAsync(duringClose: true)) return;
            recoveryTimer.Stop(); recoveryCancellation.Cancel();
            await ResolveDocumentRecoveryAsync(CancellationToken.None);
            closing = true;
            Close(); // Resources are released only by the actual Closed event.
        }
        catch (Exception ex)
        {
            closing = false;
            LocalLog.Failure("closing_failed", ex);
            if (ErrorPresenter != null) ErrorPresenter(Lang.T("CloseError"));
            else MessageBox.Show(this, Lang.T("CloseError"), Lang.T("ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            closeRequested = false;
            if (!closing)
            {
                if (recoveryCancellation.IsCancellationRequested)
                { recoveryCancellation.Dispose(); recoveryCancellation = new(); }
                recoveredRevision = -1;
                recoveryTimer.Start(); RefreshEnabled();
            }
        }
    }
    private async Task ResolveDocumentRecoveryAsync(CancellationToken token)
    {
        if (recoveryTask != null) await recoveryTask;
        token.ThrowIfCancellationRequested();
        var restored = pendingRecoveryRestored ? pendingRecovery : null;
        try
        {
            await Task.Run(() => { recovery.DeleteOwned(); restored?.Resolve(); }, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { recoveredRevision = -1; throw new ProjectStorageException("RecoveryCleanupFailed", ex); }
        recoveredRevision = -1;
        if (restored != null)
        {
            pendingRecovery = null; pendingRecoveryRestored = false;
            NoticeBar.Visibility = Visibility.Collapsed; recovering = false;
        }
    }
    private void DisposeResources()
    {
        if (resourcesDisposed) return;
        resourcesDisposed = true;
        recoveryTimer.Stop(); recoveryCancellation.Dispose();
        pendingRecovery?.Dispose(); pendingRecovery = null;
        recovery.Dispose(); sourceLease?.Dispose(); sourceLease = null;
    }
    private void UpdateRecent()
    {
        WelcomeRecent.Children.Clear();
        WelcomeRecent.Visibility = preferences.RecentProjects.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (preferences.RecentProjects.Count > 0)
            WelcomeRecent.Children.Add(new TextBlock { Text = Lang.T("Recent"), FontSize = 15, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 6) });
        foreach (var path in preferences.RecentProjects.Take(3))
        {
            var b = new Button { Content = new TextBlock { Text = Path.GetFileNameWithoutExtension(path), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = path, Margin = new Thickness(0, 4, 0, 0), FontSize = 16, Padding = new Thickness(12, 8, 12, 8), HorizontalContentAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(b, Path.GetFileNameWithoutExtension(path));
            b.Click += async (_, _) => { if (job == null && await AllowReplaceAsync()) await OpenProjectAsync(path); }; WelcomeRecent.Children.Add(b);
        }
    }
    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        Lang.Apply(LanguageBox.SelectedIndex == 1 ? "pl" : "en"); preferences = preferences with { Language = Lang.Language }; SavePreferences();
        SyncControls(); ShowValidation(); Status.Text = Lang.T(source == null ? "ReadyEmpty" : dimensionInvalid || marginInvalid || layoutInvalid ? "ReviewInputs" : "ReviewReady"); UpdateRecent(); if (NoticeBar.Visibility == Visibility.Visible) ShowNotice(recovering);
        if (Completion.Visibility == Visibility.Visible) RefreshCompletion();
    }
    private void SavePreferences() { try { settings.Save(preferences); } catch (Exception ex) { LocalLog.Write("settings_failed", ex.GetType().Name); } }
    private static string SafeFileName(string title) => string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string DisplayInches(decimal value) => (decimal.Round(value, 4) == value ? "" : "≈") + value.ToString("0.####", Lang.Culture);
    private const string ExactInchFormat = "0.############################";
    private static string ExactSize(ArtworkSize size) => Lang.F("ExactSize", size.WidthInches.ToString(ExactInchFormat, Lang.Culture), size.HeightInches.ToString(ExactInchFormat, Lang.Culture));
    private static string DefaultPdfName(ProjectState project) => SafeFileName(project.Title) + $"-{project.Size.WidthInches.ToString(ExactInchFormat, CultureInfo.InvariantCulture)}x{project.Size.HeightInches.ToString(ExactInchFormat, CultureInfo.InvariantCulture)}in.pdf";
    private void CompletionClose(object sender, RoutedEventArgs e) => Completion.Visibility = Visibility.Collapsed;
    private void CompletionVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!initialized) return;
        if (Completion.IsVisible) completionFocus = Keyboard.FocusedElement;
        RefreshEnabled();
        Dispatcher.BeginInvoke(() =>
        {
            if (Completion.IsVisible) Keyboard.Focus(OpenPdfButton.IsVisible ? OpenPdfButton : BackToEditingButton);
            else if (job == null && completionFocus is UIElement { IsVisible: true, IsEnabled: true } previous) Keyboard.Focus(previous);
        }, DispatcherPriority.Input);
    }
    private void OpenPdfClick(object sender, RoutedEventArgs e) { if (completedPdf != null) ShellOpen(completedPdf); }
    private void ShowFolderClick(object sender, RoutedEventArgs e) { if (completedPdf != null) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", completedPdf }, UseShellExecute = false }); }
    private static void ShellOpen(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { MessageBox.Show(Lang.T("Unexpected"), Lang.T("ErrorTitle")); } }
    private void CalibrationClick(object sender, RoutedEventArgs e) => ShowCalibration();
    private void ShowCalibration()
    {
        if (job != null) return;
        var calibrationPaper = state.Paper with { Orientation = layout?.Orientation == SheetOrientation.Landscape ? OrientationMode.Landscape : OrientationMode.Portrait };
        var prior = preferences.Calibrations.LastOrDefault(c => c.Profile == state.PrinterProfile && c.Orientation == (layout?.Orientation ?? SheetOrientation.Portrait));
        var dialog = new CalibrationWindow(calibrationPaper, state.PrinterProfile, prior, pdf, printer, state.Grayscale) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Record != null) ApplyCalibrationRecord(dialog.Record);
    }
    private void ApplyCalibrationRecord(CalibrationRecord record)
    {
        preferences = preferences with { Calibrations = preferences.Calibrations.Where(c => !(c.PrinterName == record.PrinterName && c.Pathway == record.Pathway && c.Profile == record.Profile && c.Orientation == record.Orientation)).Append(record).ToList(), CalibrationPromptDismissed = true };
        var priorState = state;
        state = state with { PrinterName = record.PrinterName };
        SavePreferences(); NoticeBar.Visibility = Visibility.Collapsed;
        if (record.EdgeClipping) TryCommitLayout(state with { Paper = state.Paper with { MarginInches = Math.Max(state.Paper.MarginInches, .25m) } }, false);
        if (state != priorState) { Changed(); SyncControls(); }
        else RefreshLayout();
    }
    private void AboutClick(object sender, RoutedEventArgs e)
    {
        var body = new StackPanel { Margin = new Thickness(30) };
        body.Children.Add(new TextBlock { Text = Lang.T("About"), FontFamily = new System.Windows.Media.FontFamily("Georgia"), FontSize = 28, Margin = new Thickness(0, 0, 0, 20) });
        body.Children.Add(new TextBlock { Text = Lang.T("AboutBody"), LineHeight = 25 });
        var logButton = new Button { Content = Lang.T("Logs"), Margin = new Thickness(0, 20, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
        logButton.Click += (_, _) => { var logs = Path.Combine(settings.RootPath, "logs"); Directory.CreateDirectory(logs); ShellOpen(logs); }; body.Children.Add(logButton);
        body.Children.Add(new TextBlock { Text = Lang.T("CacheHelp"), FontSize = 14, LineHeight = 22, Margin = new Thickness(0, 15, 0, 10) });
        var clear = new Button { Content = Lang.T("ClearCache"), HorizontalAlignment = HorizontalAlignment.Left };
        var feedback = new TextBlock { Margin = new Thickness(0, 15, 0, 0) };
        clear.Click += async (_, _) =>
        {
            clear.IsEnabled = false;
            bool success = await RunJobAsync("Working", async token => { await images.ClearCacheAsync(source?.Sha256, token); await projects.ClearCacheAsync(token); });
            if (success) feedback.Text = Lang.T("CacheCleared"); clear.IsEnabled = true;
        };
        body.Children.Add(clear); body.Children.Add(feedback);
        var window = new Window { Title = Lang.T("About"), Style = (Style)Application.Current.Resources[typeof(Window)], Owner = this, Width = 610, Height = 690, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        window.Loaded += (_, _) => App.FitOnScreen(window);
        window.ShowDialog();
    }
}
