using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private readonly PdfExporter pdf = new();
    private readonly PrintService printer = new();
    private AppPreferences preferences;
    private ProjectState state = new();
    private SourceInfo? source;
    private LayoutPlan? layout;
    private string? enhancedPath, projectPath, completedPdf;
    private RenderSnapshot? completedSnapshot;
    private string? completedPrinter;
    private CancellationTokenSource? job;
    private bool initialized, synchronizing, dirty, closing, compareOriginal, recovering;
    private long revision, recoveredRevision = -1;
    private Task? recoveryTask;
    private readonly CancellationTokenSource recoveryCancellation = new();
    private bool dimensionInvalid, marginInvalid;
    private bool widthTextEdited, heightTextEdited;
    private readonly List<ProjectState> undoStates = [], redoStates = [];
    private ProjectState historyAnchor = new();
    private readonly DispatcherTimer recoveryTimer;
    private readonly string[] arguments;

    public MainWindow(string[] args, string? dataRoot = null)
    {
        images = new(dataRoot == null ? null : Path.Combine(dataRoot, "image-cache"));
        enhancement = new();
        projects = new(dataRoot == null ? null : Path.Combine(dataRoot, "project-cache"));
        settings = new(dataRoot);
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
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, async (_, _) => await ChooseImageAsync()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, async (_, _) => await SaveAsync(false)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, new KeyGesture(Key.S, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Print, new KeyGesture(Key.P, ModifierKeys.Control)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Print, async (_, _) => await PrintAsync()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, async (_, _) => await ApplyHistoryAsync(false)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, async (_, _) => await ApplyHistoryAsync(true)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Undo, new KeyGesture(Key.Z, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Redo, new KeyGesture(Key.Z, ModifierKeys.Control | ModifierKeys.Shift)));
    }

    private async void LoadedAsync(object sender, RoutedEventArgs e)
    {
        App.FitOnScreen(this);
        UpdateRecent();
        if (arguments.Contains("--checks")) return;
        if (File.Exists(settings.RecoveryPath)) ShowNotice(true);
        else if (!preferences.CalibrationPromptDismissed) ShowNotice(false);
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
        NoticeText.Text = Lang.T(recovery ? "Recover" : "CalibrationPrompt");
        NoticeAction.Content = Lang.T(recovery ? "Restore" : "Calibration");
        NoticeBar.Visibility = Visibility.Visible;
    }
    private async void NoticeActionClick(object sender, RoutedEventArgs e)
    {
        if (recovering) { NoticeBar.Visibility = Visibility.Collapsed; await OpenProjectAsync(settings.RecoveryPath, true); }
        else ShowCalibration();
    }
    private void NoticeLaterClick(object sender, RoutedEventArgs e)
    {
        NoticeBar.Visibility = Visibility.Collapsed;
        if (!recovering) { preferences = preferences with { CalibrationPromptDismissed = true }; SavePreferences(); }
    }

    private async void OpenImageClick(object sender, RoutedEventArgs e) => await ChooseImageAsync();
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
            var preview = await images.GetPreviewAsync(imported.NormalizedPath, ct: token);
            token.ThrowIfCancellationRequested();
            source = imported; enhancedPath = projectPath = null;
            dimensionInvalid = marginInvalid = false; ShowValidation();
            state = new ProjectState { Title = Path.GetFileNameWithoutExtension(path), Size = LayoutEngine.Proportional(24, true, imported.Width, imported.Height) };
            if (state.Size.HeightInches > 120) state = state with { Size = LayoutEngine.Proportional(24, false, imported.Width, imported.Height) };
            Preview.Image = preview; Preview.Fit(); compareOriginal = false;
            dirty = true; revision++;
            SyncControls(); ResetHistory(); Completion.Visibility = Visibility.Collapsed; Status.Text = Lang.T("ImageReady");
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
            SourceDetails.Text = source == null ? "—" : $"{source.Width:N0} × {source.Height:N0} px · {source.Format}";
            ArtworkTitle.Text = source == null ? "" : state.Title;
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
            Summary.Text = Lang.T("ReadyEmpty"); Status.Text = Lang.T("Tagline");
            CalibrationStatus.Text = ""; return;
        }
        layout = LayoutEngine.Calculate(state.Size, state.Paper);
        state = state with { ResolvedOrientation = layout.Orientation };
        Preview.Layout = layout; Preview.Composition = state.Composition;
        Preview.SourceWidth = state.Composition.Rotation % 180 == 0 ? source.Width : source.Height;
        Preview.SourceHeight = state.Composition.Rotation % 180 == 0 ? source.Height : source.Width;
        Preview.InvalidateVisual();
        Summary.Text = Lang.F("Summary", state.Size.WidthInches.ToString("0.####", Lang.Culture), state.Size.HeightInches.ToString("0.####", Lang.Culture), layout.PageCount, Lang.T(layout.Orientation.ToString()));
        PaperSummary.Text = Lang.F("SummaryPaper", state.PrinterProfile == "canon-mf210" ? "Canon MF210" : "Epson ET-8500", state.Paper.MarginInches == 0 ? Lang.T("Borderless") : Lang.F("TrimMargins", state.Paper.MarginInches.ToString("0.###", Lang.Culture)))
            + (!state.KeepProportions ? " · " + Lang.T(state.Composition.Mode.ToString()) : "");
        CandidateSummary.Text = Lang.F("Candidates", layout.PortraitCount, layout.LandscapeCount);
        GridSummary.Text = Lang.F("GridInfo", layout.Columns, layout.Rows);
        PrintHint.Text = Lang.T(state.Paper.MarginInches > 0 ? "TrimHint" : "BorderlessHint");
        QualityBadge.Text = Lang.T(compareOriginal ? "Original" : state.Quality.ToString());
        var effectiveWidth = state.Composition.Rotation % 180 == 0 ? source.Width : source.Height;
        var ppi = effectiveWidth / (double)state.Size.WidthInches * (state.Quality == QualityMode.Enhanced ? 2 : 1);
        ResolutionHint.Text = ppi < 120 ? Lang.T("Soft") : "";
        CalibrationStatus.Text = Lang.T(preferences.Calibrations.Any(c => c.Enabled && !c.EdgeClipping && c.Pathway == "direct" && c.PrinterName == state.PrinterName && c.Profile == state.PrinterProfile && c.Orientation == layout.Orientation && !string.IsNullOrWhiteSpace(c.DriverFingerprint)) ? "Calibrated" : "NotMeasured");
        Welcome.Visibility = Visibility.Collapsed; PreviewHeader.Visibility = PreviewTools.Visibility = Visibility.Visible;
        Title = (dirty ? "● " : "") + state.Title + " — Mosaic Template Studio";
    }
    private void RefreshEnabled()
    {
        bool editable = source != null && job == null;
        OrientationBox.IsEnabled = editable; LanguageBox.IsEnabled = job == null;
        EditorRail.IsEnabled = editable;
        PrintButton.IsEnabled = ExportButton.IsEnabled = editable && !dimensionInvalid && !marginInvalid;
        EnhanceButton.IsEnabled = editable;
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
            var imagePath = previous.Quality == QualityMode.Enhanced && enhancedPath != null ? enhancedPath : source.NormalizedPath;
            var bitmap = await images.GetPreviewAsync(imagePath, previous.Composition.Rotation, ct: token);
            to.Add(state); from.RemoveAt(from.Count - 1); state = previous;
            dimensionInvalid = marginInvalid = false; dirty = true; revision++;
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
        if (size.WidthInches > 120 || size.HeightInches > 120) { dimensionInvalid = true; ShowValidation(); return false; }
        if (state.Size == size && state.LastDimension == (width ? "width" : "height")) { dimensionInvalid = false; ShowValidation(); return true; }
        state = state with { Size = size, LastDimension = width ? "width" : "height" };
        dimensionInvalid = false; ShowValidation();
        Changed(); SyncControls(); return true;
    }
    private void DimensionsCommitted(object sender, KeyboardFocusChangedEventArgs e) { if (!synchronizing && initialized) CommitDimension((TextBox)sender); }
    private void DimensionTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || synchronizing) return;
        if (sender == WidthBox) widthTextEdited = true; else heightTextEdited = true;
    }
    private void ShowValidation()
    {
        SizeError.Text = Lang.T(dimensionInvalid ? "InvalidSize" : "InvalidMargin");
        SizeError.Visibility = dimensionInvalid || marginInvalid ? Visibility.Visible : Visibility.Collapsed;
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
        if (dimensionInvalid) return false;
        return CommitMargin();
    }
    private void DimensionKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitDimension((TextBox)sender); e.Handled = true; } }
    private void LockChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || synchronizing || source == null) return;
        state = state with { KeepProportions = LockCheck.IsChecked == true, Composition = state.Composition with { Mode = FitMode.Fit } };
        if (state.KeepProportions)
        {
            int w = state.Composition.Rotation % 180 == 0 ? source.Width : source.Height;
            int h = state.Composition.Rotation % 180 == 0 ? source.Height : source.Width;
            bool width = state.LastDimension != "height";
            state = state with { Size = LayoutEngine.Proportional(width ? state.Size.WidthInches : state.Size.HeightInches, width, w, h) };
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
        state = state with { Paper = state.Paper with { Orientation = (OrientationMode)OrientationBox.SelectedIndex } }; Changed();
    }
    private void MarginCommitted(object sender, KeyboardFocusChangedEventArgs e) => CommitMargin();
    private void MarginKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitMargin(); }
    private bool CommitMargin()
    {
        if (!initialized || synchronizing) return true;
        if (!decimal.TryParse(MarginBox.Text, NumberStyles.AllowDecimalPoint, Lang.Culture, out var value) || value < 0 || value > 2)
        { marginInvalid = true; ShowValidation(); return false; }
        marginInvalid = false; ShowValidation();
        if (state.Paper.MarginInches != value) { state = state with { Paper = state.Paper with { MarginInches = value } }; Changed(); }
        return true;
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || synchronizing) return;
        bool canon = ProfileBox.SelectedIndex == 1;
        marginInvalid = false; ShowValidation();
        state = state with { PrinterProfile = canon ? "canon-mf210" : "epson-et8500", PrinterName = null, Paper = state.Paper with { MarginInches = canon ? .25m : 0 } };
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
            var image = await images.GetPreviewAsync(ActiveImage(), rotation, ct: token);
            state = state with { Composition = state.Composition with { Rotation = rotation } };
            if (state.KeepProportions) state = state with { Size = new(state.Size.HeightInches, state.Size.WidthInches) };
            Preview.Image = image; Preview.Fit(); Changed(); SyncControls();
        });
    }
    private string ActiveImage() => source == null ? "" : state.Quality == QualityMode.Enhanced && enhancedPath != null && !compareOriginal ? enhancedPath : source.NormalizedPath;
    private async void QualityChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || synchronizing || source == null || job != null) return;
        if (EnhancedRadio.IsChecked == true && enhancedPath == null) { await EnhanceAsync(); return; }
        var chosen = EnhancedRadio.IsChecked == true ? QualityMode.Enhanced : QualityMode.Standard;
        await RunJobAsync("Working", async token =>
        {
            var bitmap = await images.GetPreviewAsync(chosen == QualityMode.Enhanced ? enhancedPath! : source.NormalizedPath, state.Composition.Rotation, ct: token);
            state = state with { Quality = chosen }; compareOriginal = false; Preview.Image = bitmap; Changed(); SyncControls();
        });
        SyncControls();
    }
    private async void EnhanceClick(object sender, RoutedEventArgs e) => await EnhanceAsync();
    private async Task EnhanceAsync()
    {
        if (source == null || job != null) return;
        await RunJobAsync("Enhancing", async token =>
        {
            var result = await enhancement.EnhanceAsync(source, JobProgress(), token, CpuCheck.IsChecked == true);
            var image = await images.GetPreviewAsync(result, state.Composition.Rotation, ct: token);
            enhancedPath = result; compareOriginal = false; state = state with { Quality = QualityMode.Enhanced };
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
            var path = await images.CreateDetailComparisonAsync(source, enhancedPath, token);
            var bitmap = await images.GetPreviewAsync(path, maxEdge: 2048, ct: token);
            var grid = new Grid { Margin = new Thickness(25) };
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
            var labels = new Grid(); labels.ColumnDefinitions.Add(new()); labels.ColumnDefinitions.Add(new());
            labels.Children.Add(new TextBlock { Text = Lang.T("Original"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center });
            var right = new TextBlock { Text = Lang.T("Enhanced"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center }; Grid.SetColumn(right, 1); labels.Children.Add(right); grid.Children.Add(labels);
            var image = new Image { Source = bitmap, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 20, 0, 0) }; Grid.SetRow(image, 1); grid.Children.Add(image);
            var window = new Window { Title = Lang.T("Compare"), Style = (Style)Application.Current.Resources[typeof(Window)], Owner = this, Width = 1100, Height = 670, MinWidth = 700, MinHeight = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = grid }; window.Loaded += (_, _) => App.FitOnScreen(window); window.Show();
        });
    }
    private void FitViewClick(object sender, RoutedEventArgs e) => Preview.Fit();
    private async void DetailClick(object sender, RoutedEventArgs e)
    {
        if (source == null || job != null) return;
        await RunJobAsync("Working", async token => { Preview.Image = await images.GetPreviewAsync(ActiveImage(), state.Composition.Rotation, maxEdge: 3600, ct: token); Preview.Detail(); });
    }
    private void GridChanged(object sender, RoutedEventArgs e) { if (Preview == null) return; Preview.ShowGrid = GridCheck.IsChecked == true; Preview.InvalidateVisual(); }

    private async Task<RenderSnapshot> SnapshotAsync(CancellationToken token)
    {
        if (source == null || layout == null) throw new InvalidOperationException();
        if (state.Quality == QualityMode.Enhanced && enhancedPath == null)
            enhancedPath = await enhancement.EnhanceAsync(source, JobProgress(), token, CpuCheck.IsChecked == true);
        var outputPath = await images.GetOutputImageAsync(source, state.Composition.Rotation, state.Quality == QualityMode.Enhanced ? enhancedPath : null, token);
        return new(source, outputPath, state, layout);
    }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (source == null || job != null || !CommitPendingInputs()) return;
        var dialog = new SaveFileDialog { Filter = Lang.T("PdfFilter"), DefaultExt = ".pdf", FileName = SafeFileName(state.Title) + $"-{state.Size.WidthInches.ToString("0.##", CultureInfo.InvariantCulture)}x{state.Size.HeightInches.ToString("0.##", CultureInfo.InvariantCulture)}in.pdf" };
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
            var calibration = preferences.Calibrations.LastOrDefault(c => c.Enabled && c.Pathway == "direct" && c.PrinterName == state.PrinterName && c.Profile == state.PrinterProfile && c.Orientation == layout!.Orientation);
            var result = await printer.PrintAsync(snapshot, this, Lang.Language, calibration, JobProgress(), token);
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
        string summary = Lang.F("Summary", saved.Size.WidthInches.ToString("0.####", Lang.Culture), saved.Size.HeightInches.ToString("0.####", Lang.Culture), completedSnapshot.Layout.PageCount, Lang.T(completedSnapshot.Layout.Orientation.ToString()));
        bool exported = completedPrinter == null && completedPdf != null;
        CompletionTitle.Text = Lang.T(exported ? "ExportedTitle" : "SentTitle");
        CompletionFile.Text = exported ? Path.GetFileName(completedPdf!) : completedPrinter;
        CompletionBody.Text = exported ? summary + $" · {new FileInfo(completedPdf!).Length / 1048576d:0.0} MB\n\n" + Lang.T(saved.Paper.MarginInches > 0 ? "TrimHint" : "ActualSize") + (saved.Composition.Mode == FitMode.Fit && !saved.KeepProportions ? "\n" + Lang.T("FitHint") : "") : Lang.T("SentBody") + "\n\n" + summary;
        OpenPdfButton.Visibility = ShowFolderButton.Visibility = exported ? Visibility.Visible : Visibility.Collapsed;
        Completion.Visibility = Visibility.Visible;
    }
    private IProgress<JobProgress> JobProgress() => new Progress<JobProgress>(p => { JobBar.IsIndeterminate = p.Fraction < 0; if (p.Fraction >= 0) JobBar.Value = Math.Clamp(p.Fraction * 100, 0, 100); });
    private async Task<bool> RunJobAsync(string label, Func<CancellationToken, Task> action)
    {
        if (job != null) return false;
        job = new CancellationTokenSource(); BusyText.Text = Lang.T(label); JobBar.IsIndeterminate = true; BusyBar.Visibility = Visibility.Visible;
        RefreshEnabled(); LocalLog.Write(label);
        try { await action(job.Token); LocalLog.Write(label + "_ok"); return true; }
        catch (OperationCanceledException) { Status.Text = Lang.T("Cancelled"); return false; }
        catch (OutputException ex)
        {
            LocalLog.Write(label + "_failed", ex.Code);
            if (ex.SuggestedMarginInches is decimal margin && margin <= 2 && QuestionWindow.Ask(this, Lang.F("TrimOffer", margin.ToString("0.###", Lang.Culture)), false) == UserChoice.Apply)
            { state = state with { Paper = state.Paper with { MarginInches = margin } }; Changed(); SyncControls(); }
            else MessageBox.Show(this, ex.Message, Lang.T("ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            LocalLog.Write(label + "_failed", ex.GetType().Name);
            string key = ex switch
            {
                ImagePipelineException { Code: "cacheFull" or "diskFull" } => "CacheFull",
                ImagePipelineException => "ImageError",
                EnhancementException { Code: "aiSourceTooLarge" } => "AiLimit",
                EnhancementException { Code: "aiIntegrity" or "aiMissing" } => "AiIntegrity",
                EnhancementException => "AiError",
                ProjectStorageException { Code: "StorageFull" } => "CacheFull",
                ProjectStorageException { Code: "ProjectSaveFailed" } => "SaveError",
                ProjectStorageException { Code: "SourceChanged" or "SourceUnavailable" } => "SourceChanged",
                ProjectStorageException => "ProjectError",
                _ => "Unexpected"
            };
            MessageBox.Show(this, Lang.T(key), Lang.T("ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); return false;
        }
        finally { job.Dispose(); job = null; BusyBar.Visibility = Visibility.Collapsed; RefreshEnabled(); }
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
    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (source == null || job != null || !CommitPendingInputs()) return false;
        var path = saveAs ? null : projectPath;
        if (path == null) { var d = new SaveFileDialog { Filter = Lang.T("ProjectFilter"), DefaultExt = ".mosaic", FileName = SafeFileName(state.Title) + ".mosaic" }; if (d.ShowDialog(this) != true) return false; path = d.FileName; }
        var target = path;
        return await RunJobAsync("Saving", async token =>
        {
            await projects.SaveAsync(target, state, source, token); projectPath = target; dirty = false;
            preferences = settings.AddRecent(target); UpdateRecent(); RefreshLayout(); Status.Text = Lang.T("Saved");
        });
    }
    private async Task OpenProjectAsync(string path, bool recovered = false)
    {
        await RunJobAsync("Opening", async token =>
        {
            var opened = await projects.OpenAsync(path, token);
            var imported = await images.ImportAsync(opened.OriginalPath, token);
            var image = await images.GetPreviewAsync(imported.NormalizedPath, opened.State.Composition.Rotation, ct: token);
            var openedState = opened.State; string? openedEnhanced = null;
            // Derived AI caches are reproducible and remain outside portable projects.
            bool aiFallback = false;
            if (openedState.Quality == QualityMode.Enhanced)
            {
                try { openedEnhanced = await enhancement.EnhanceAsync(imported, JobProgress(), token); image = await images.GetPreviewAsync(openedEnhanced, openedState.Composition.Rotation, ct: token); }
                catch (EnhancementException) { openedState = openedState with { Quality = QualityMode.Standard }; aiFallback = true; }
            }
            token.ThrowIfCancellationRequested();
            source = imported; state = openedState; enhancedPath = openedEnhanced; compareOriginal = false;
            projectPath = recovered ? null : path; dirty = recovered || aiFallback; revision++; Preview.Image = image; Preview.Fit();
            if (!recovered) preferences = settings.AddRecent(path);
            dimensionInvalid = marginInvalid = false; ShowValidation();
            SyncControls(); ResetHistory(); UpdateRecent(); Completion.Visibility = Visibility.Collapsed; Status.Text = Lang.T(aiFallback ? "AiError" : "Ready");
        });
    }
    private async Task<bool> AllowReplaceAsync()
    {
        if (job != null) return false;
        if (!dirty || source == null) return true;
        var answer = QuestionWindow.Ask(this, Lang.T("Dirty"), true);
        return answer == UserChoice.Discard || answer == UserChoice.Save && await SaveAsync(false);
    }
    private async Task RecoverEditsAsync()
    {
        if (source == null || !dirty || revision == recoveredRevision || job != null) return;
        var snapshotRevision = revision; var savedState = state; var savedSource = source;
        try { await projects.SaveAsync(settings.RecoveryPath, savedState, savedSource, recoveryCancellation.Token); recoveredRevision = snapshotRevision; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LocalLog.Write("recovery_failed", ex.GetType().Name); }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (job != null) { job.Cancel(); return; }
        if (!await AllowReplaceAsync()) return;
        closing = true; recoveryTimer.Stop(); recoveryCancellation.Cancel();
        if (recoveryTask != null) await recoveryTask;
        // A normal close has an explicit save/discard decision; recovery is only for interruptions.
        try { File.Delete(settings.RecoveryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Close();
    }
    private void UpdateRecent()
    {
        WelcomeRecent.Children.Clear();
        foreach (var path in preferences.RecentProjects.Take(3))
        {
            var b = new Button { Content = Path.GetFileNameWithoutExtension(path), Margin = new Thickness(0, 4, 0, 0), FontSize = 14, MinHeight = 34, Padding = new Thickness(10, 5, 10, 5) };
            b.Click += async (_, _) => { if (job == null && await AllowReplaceAsync()) await OpenProjectAsync(path); }; WelcomeRecent.Children.Add(b);
        }
    }
    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        Lang.Apply(LanguageBox.SelectedIndex == 1 ? "pl" : "en"); preferences = preferences with { Language = Lang.Language }; SavePreferences();
        SyncControls(); ShowValidation(); Status.Text = Lang.T(source == null ? "Tagline" : "Ready"); UpdateRecent(); if (NoticeBar.Visibility == Visibility.Visible) ShowNotice(recovering);
        if (Completion.Visibility == Visibility.Visible) RefreshCompletion();
    }
    private void SavePreferences() { try { settings.Save(preferences); } catch (Exception ex) { LocalLog.Write("settings_failed", ex.GetType().Name); } }
    private static string SafeFileName(string title) => string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private void CompletionClose(object sender, RoutedEventArgs e) => Completion.Visibility = Visibility.Collapsed;
    private void OpenPdfClick(object sender, RoutedEventArgs e) { if (completedPdf != null) ShellOpen(completedPdf); }
    private void ShowFolderClick(object sender, RoutedEventArgs e) { if (completedPdf != null) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", completedPdf }, UseShellExecute = false }); }
    private static void ShellOpen(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { MessageBox.Show(Lang.T("Unexpected"), Lang.T("ErrorTitle")); } }
    private void CalibrationClick(object sender, RoutedEventArgs e) => ShowCalibration();
    private void ShowCalibration()
    {
        if (job != null) return;
        var calibrationPaper = state.Paper with { Orientation = layout?.Orientation == SheetOrientation.Landscape ? OrientationMode.Landscape : OrientationMode.Portrait };
        var prior = preferences.Calibrations.LastOrDefault(c => c.Profile == state.PrinterProfile && c.Orientation == (layout?.Orientation ?? SheetOrientation.Portrait));
        var dialog = new CalibrationWindow(calibrationPaper, state.PrinterProfile, prior, pdf, printer) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Record != null)
        {
            preferences = preferences with { Calibrations = preferences.Calibrations.Where(c => !(c.PrinterName == dialog.Record.PrinterName && c.Pathway == dialog.Record.Pathway && c.Profile == dialog.Record.Profile && c.Orientation == dialog.Record.Orientation)).Append(dialog.Record).ToList(), CalibrationPromptDismissed = true };
            state = state with { PrinterName = dialog.Record.PrinterName };
            SavePreferences(); NoticeBar.Visibility = Visibility.Collapsed;
            if (dialog.Record.EdgeClipping) { state = state with { Paper = state.Paper with { MarginInches = Math.Max(state.Paper.MarginInches, .25m) } }; Changed(); SyncControls(); }
            RefreshLayout();
        }
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
