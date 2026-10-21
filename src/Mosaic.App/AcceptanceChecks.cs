using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mosaic.Core;
using Mosaic.Storage;

namespace Mosaic.App;

public partial class MainWindow
{
    internal void FinishChecks() { closing = true; dirty = false; recoveryTimer.Stop(); recoveryCancellation.Cancel(); pendingRecovery?.Dispose(); recovery.Dispose(); sourceLease?.Dispose(); }
    // Runs the real editor operations on its dispatcher, with isolated files/settings. No printer jobs.
    internal async Task<int> RunAcceptanceChecksAsync(string directory)
    {
        int count = 0;
        void Check(bool pass, string description) { if (!pass) throw new InvalidOperationException(description); count++; }
        Directory.CreateDirectory(directory);
        void Capture(string name, double dpi = 96)
        {
            base.UpdateLayout();
            var content = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi / 96), (int)Math.Ceiling(content.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
        }
        async Task FinishJobAsync()
        {
            var start = DateTime.UtcNow;
            while (job != null)
            {
                if (DateTime.UtcNow - start > TimeSpan.FromSeconds(45)) throw new TimeoutException("Editor job did not finish");
                await Task.Delay(15);
            }
        }
        Check(PrintButton.IsEnabled == false && ExportButton.IsEnabled == false, "empty output controls");
        ProjectMenuClick(ProjectButton, new RoutedEventArgs());
        Check(ProjectButton.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, Lang.T("About")) && item.IsEnabled), "cache cleanup accessible before image import");
        ProjectButton.ContextMenu.IsOpen = false;
        Capture("arrival-en.png");
        string input = Path.Combine(directory, "downloaded-artwork.png");
        CreateArtwork(input);
        string originalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input)));
        await ImportAsync(input);
        Check(source != null && source.Width == 1000 && source.Height == 750, "real image import");
        Check(source!.Sha256.Equals(originalHash, StringComparison.OrdinalIgnoreCase), "immutable source hash");
        Check(state.Size == new ArtworkSize(24, 18) && layout!.PageCount == 6, "initial24inch");
        Check(Preview.Image!.IsFrozen && Preview.Layout == layout, "frozen preview and same layout instance");
        var actualSource = source; var actualState = state;
        source = source with { Width = 3671, Height = 2839 };
        state = state with { Size = LayoutEngine.Proportional(24, true, 3671, 2839) }; SyncControls();
        var exactAuthority = state.Size;
        CommitDimension(HeightBox);
        Check(state.Size == exactAuthority && state.LastDimension == "width", "focus traversal does not round complementary geometry");
        source = actualSource; state = actualState; SyncControls();
        WidthBox.Text = "25.125"; CommitDimension(WidthBox);
        Check(state.Size == new ArtworkSize(25.125m, 18.84375m), "width exact and proportional");
        await ApplyHistoryAsync(false);
        Check(state.Size == new ArtworkSize(24,18), "undo restores exact physical state");
        await ApplyHistoryAsync(true);
        Check(state.Size == new ArtworkSize(25.125m,18.84375m), "redo restores exact physical state");
        HeightBox.Text = "24"; CommitDimension(HeightBox);
        Check(state.Size == new ArtworkSize(32, 24) && state.LastDimension == "height", "height authority");
        CommitDimension(WidthBox);
        Check(state.LastDimension == "height", "unchanged focus does not replace user authority");
        OrientationBox.SelectedIndex = 2;
        WidthBox.Text = "24"; CommitDimension(WidthBox);
        Check(layout!.Orientation == SheetOrientation.Landscape && state.Paper.Orientation == OrientationMode.Landscape, "manual override survives resize");
        OrientationBox.SelectedIndex = 0;
        Check(layout.Orientation == SheetOrientation.Portrait, "portrait tie restored");
        WidthBox.Text = "invalid"; CommitDimension(WidthBox);
        Check(!ExportButton.IsEnabled && dimensionInvalid && state.Size.WidthInches == 24, "invalid text preserves geometry");
        ProfileBox.SelectedIndex = 1;
        Check(WidthBox.Text == "invalid" && dimensionInvalid, "unrelated preset preserves pending invalid text");
        ProfileBox.SelectedIndex = 0;
        MarginBox.Text = "0.25"; CommitMargin();
        Check(dimensionInvalid && !ExportButton.IsEnabled && !CommitPendingInputs(), "margin cannot erase dimension validation");
        await PrintAsync();
        Check(job == null && dimensionInvalid, "keyboard print cannot bypass invalid text");
        WidthBox.Text = "24"; CommitDimension(WidthBox);
        MarginBox.Text = "0"; CommitMargin();
        Check(ExportButton.IsEnabled, "valid input recovers");
        LockCheck.IsChecked = false;
        HeightBox.Text = "24"; CommitDimension(HeightBox);
        Check(state.Size == new ArtworkSize(24, 24) && state.Composition.Mode == FitMode.Fit, "explicit fit default");
        FitBox.SelectedIndex = 1; CropX.Value = .85; CropY.Value = .3;
        Check(state.Composition.Mode == FitMode.Crop && state.Composition.CropX == .85m, "interactive crop positioning");
        FitBox.SelectedIndex = 2;
        Check(state.Composition.Mode == FitMode.Stretch && FitHelp.Text == Lang.T("StretchHint"), "explicit stretch guidance");
        FitBox.SelectedIndex = 0;
        LockCheck.IsChecked = true;
        Check(state.Size.HeightInches == 24 && state.Size.WidthInches == 32, "relock preserves last edited height");
        LockCheck.IsChecked = false;
        HeightBox.Text = "120"; CommitDimension(HeightBox);
        var validState = state; var validLayout = layout; int validHistory = undoStates.Count;
        LockCheck.IsChecked = true;
        Check(state == validState && ReferenceEquals(layout, validLayout) && LockCheck.IsChecked == false && undoStates.Count == validHistory,
            "invalid height-authority relock preserves state layout and history");
        Check(dimensionInvalid && !ExportButton.IsEnabled && !CommitPendingInputs(), "invalid relock blocks output with inline validation");
        bool snapshotRejected = false;
        try { await SnapshotAsync(CancellationToken.None); } catch (InvalidOperationException) { snapshotRejected = true; }
        Check(snapshotRejected, "snapshot rejects invalid pending size");
        HeightBox.Text = "24"; CommitDimension(HeightBox);
        source = source with { Width = 750, Height = 1000 };
        WidthBox.Text = "120"; CommitDimension(WidthBox);
        validState = state; validLayout = layout;
        LockCheck.IsChecked = true;
        Check(state == validState && ReferenceEquals(layout, validLayout) && !state.KeepProportions,
            "invalid width-authority relock preserves state and layout");
        source = actualSource;
        WidthBox.Text = "24"; CommitDimension(WidthBox);
        LockCheck.IsChecked = true;
        var coherentState = state;
        state = state with { Size = new(25, 18) };
        snapshotRejected = false;
        try { await SnapshotAsync(CancellationToken.None); } catch (InvalidOperationException) { snapshotRejected = true; }
        Check(snapshotRejected, "snapshot refuses stale geometry even without validation flag");
        state = coherentState; SyncControls();
        LockCheck.IsChecked = false;
        WidthBox.Text = "120"; CommitDimension(WidthBox);
        HeightBox.Text = "104"; CommitDimension(HeightBox);
        MarginBox.Text = "1.95"; CommitMargin();
        validState = state; validLayout = layout;
        OrientationBox.SelectedIndex = (int)OrientationMode.Portrait;
        Check(state == validState && ReferenceEquals(layout, validLayout) && layoutInvalid && OrientationBox.SelectedIndex == 0,
            "rejected sheet-limit orientation preserves coherent state and control");
        MarginBox.Text = "2";
        Check(!CommitMargin() && state == validState && ReferenceEquals(layout, validLayout), "sheet-limit margin validates before mutation");
        MarginBox.Text = "0"; CommitMargin();
        Check(!layoutInvalid && ExportButton.IsEnabled, "valid margin recovers sheet-limit validation");
        WidthBox.Text = "24"; CommitDimension(WidthBox);
        HeightBox.Text = "18"; CommitDimension(HeightBox);
        LockCheck.IsChecked = true;
        Lang.Apply("pl"); LanguageBox.SelectedIndex = 1;
        WidthBox.Text = "24,125"; CommitDimension(WidthBox);
        Check(state.Size.WidthInches == 24.125m && HeightBox.Text.Contains(','), "decimal comma journey");
        Check((string)ExportButton.Content == "Eksportuj PDF" && (string)PrintButton.Content == "Drukuj", "Polish actions");
        var question = new QuestionWindow(this, Lang.T("Dirty"), true);
        Check(Equals(question.PrimaryButton.Content, "Zapisz projekt") && Equals(question.DiscardButton!.Content, "Odrzuć zmiany"), "app-owned save choices are Polish independent of Windows language");
        _ = Dispatcher.BeginInvoke(() => question.DiscardButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.Background);
        question.ShowDialog();
        Check(question.Choice == UserChoice.Discard, "localized save protection routes selected choice");
        WidthBox.Text = "3,"; CommitDimension(WidthBox);
        Check(state.Size.WidthInches == 3 && !dimensionInvalid, "partial comma commit");
        WidthBox.Text = "60"; CommitDimension(WidthBox);
        Check(state.Size == new ArtworkSize(60, 45), "60inch sizing");
        ProfileBox.SelectedIndex = 1;
        Check(state.Paper.MarginInches == .25m && state.PrinterProfile == "canon-mf210", "Canon trim profile");
        Check(PaperSummary.Text.Contains("Canon") && PaperSummary.Text.Contains("0,25"), "visible print summary");
        ProfileBox.SelectedIndex = 0;
        GuideCheck.IsChecked = true;
        var snapshot = await SnapshotAsync(CancellationToken.None);
        Check(ReferenceEquals(snapshot.Layout, layout), "output snapshot uses preview layout");
        string largePdf = Path.Combine(directory, "60-inch-pl.pdf");
        var result = await pdf.ExportAsync(snapshot, largePdf, "pl");
        Check(result.PageCount == layout!.PageCount + 1 && File.Exists(largePdf), "actual largePDF with guide");
        string project = Path.Combine(directory, "portable.mosaic");
        await projects.SaveAsync(project, state, source);
        File.Delete(input);
        await OpenProjectAsync(project);
        Check(source != null && state.Size == new ArtworkSize(60, 45) && state.AssemblyGuide && state.Paper.Orientation == OrientationMode.Auto, "portable project editor reopen");
        Check(File.Exists(source!.OriginalPath) && source.Sha256.Equals(originalHash, StringComparison.OrdinalIgnoreCase), "reopened embedded original after download removed");
        WidthBox.Text = "24"; CommitDimension(WidthBox);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Capture("editor-pl-100.png");
        Width = 1150; Height = 780;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Capture("editor-pl-125.png", 120);
        Capture("editor-pl-150.png", 144);
        Check(PrintButton.ActualWidth >= 120 && ExportButton.ActualHeight >= 42, "large output targets");
        Check(Preview.ActualWidth > 500 && Preview.ActualHeight > 300, "artwork remains prominent at compact editor size");
        Lang.Apply("en"); LanguageBox.SelectedIndex = 0;
        Width = 1380; Height = 920;
        Capture("editor-en.png");
        await EnhanceAsync();
        Check(state.Quality == QualityMode.Enhanced && enhancedPath != null && CompareButton.Visibility == Visibility.Visible, "bundled AI integrated into editor");
        var enhancedGeometry = layout; var enhancedPreview = Preview.Image;
        await CompareAsync();
        var comparison = OwnedWindows.Cast<Window>().Single(w => w.Title == Lang.T("Compare"));
        Check(comparison.IsVisible, "original enhanced comparison opens");
        var comparisonContent = (FrameworkElement)comparison.Content; comparison.UpdateLayout();
        var comparisonRender = new RenderTargetBitmap((int)comparisonContent.ActualWidth, (int)comparisonContent.ActualHeight, 96, 96, PixelFormats.Pbgra32); comparisonRender.Render(comparisonContent);
        var comparisonEncoder = new PngBitmapEncoder(); comparisonEncoder.Frames.Add(BitmapFrame.Create(comparisonRender)); using (var output = File.Create(Path.Combine(directory, "comparison-en.png"))) comparisonEncoder.Save(output);
        comparison.Close();
        StandardRadio.IsChecked = true; job?.Cancel(); await FinishJobAsync();
        Check(state.Quality == QualityMode.Enhanced && ReferenceEquals(Preview.Image, enhancedPreview), "cancelled quality preview retains selected recipe and bitmap");
        StandardRadio.IsChecked = true; await FinishJobAsync();
        Check(state.Quality == QualityMode.Standard && layout!.Artwork == enhancedGeometry!.Artwork, "standard reversion preserves physical geometry");
        // Recovery writes the same portable format, once per revision.
        dirty = true; await RecoverEditsAsync();
        Check(File.Exists(recovery.RecoveryPath) && recoveredRevision == revision, "recoverable edits");
        var recovered = await projects.OpenAsync(recovery.RecoveryPath);
        Check(recovered.State.Size == state.Size, "recovery includes current geometry");
        using (var interrupted = new RecoverySession(settings.RootPath))
        {
            await projects.SaveAsync(interrupted.RecoveryPath, state, source);
            interrupted.Dispose();
            pendingRecovery = recovery.ClaimPending(); ShowNotice(true);
            WidthBox.Text = "30"; CommitDimension(WidthBox);
            _ = Dispatcher.BeginInvoke(() => OwnedWindows.OfType<QuestionWindow>().Single().Close(), DispatcherPriority.Background);
            Check(!await RestoreRecoveryAsync() && state.Size.WidthInches == 30 && dirty && File.Exists(interrupted.RecoveryPath),
                "restore Cancel preserves current dirty edits and prior recovery");
            _ = Dispatcher.BeginInvoke(() => OwnedWindows.OfType<QuestionWindow>().Single().DiscardButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.Background);
            Check(await RestoreRecoveryAsync() && state.Size.WidthInches == recovered.State.Size.WidthInches && dirty,
                "restore requires explicit dirty-document replacement choice");
            Check(!File.Exists(interrupted.RecoveryPath) && File.Exists(recovery.RecoveryPath), "restored prior recovery resolves only after owned checkpoint");
        }
        using (var interrupted = new RecoverySession(settings.RootPath))
        {
            await projects.SaveAsync(interrupted.RecoveryPath, state, source);
            interrupted.Dispose();
            var emptyWindow = new MainWindow(["--checks", "--en"], settings.RootPath);
            emptyWindow.Show();
            await emptyWindow.images.ClearCacheAsync(null);
            Check(File.Exists(source.OriginalPath) && File.Exists(source.NormalizedPath), "another window's cache cleanup preserves open artwork");
            emptyWindow.pendingRecovery = emptyWindow.recovery.ClaimPending(); emptyWindow.ShowNotice(true);
            Check(emptyWindow.pendingRecovery?.Path == interrupted.RecoveryPath, "second window claims only interrupted recovery");
            emptyWindow.NoticeLaterClick(emptyWindow, new RoutedEventArgs()); emptyWindow.Close();
            Check(File.Exists(interrupted.RecoveryPath) && File.Exists(recovery.RecoveryPath), "Later and empty close preserve deferred and other live recovery");
            pendingRecovery = recovery.ClaimPending();
            Check(pendingRecovery?.Path == interrupted.RecoveryPath, "deferred recovery is offered to a later session");
            pendingRecovery!.Resolve(); pendingRecovery = null;
        }
        await images.ClearCacheAsync(source.Sha256); await projects.ClearCacheAsync();
        Check(File.Exists(source.OriginalPath) && File.Exists(project), "explicit cache cleanup preserves active original and portable project");
        completedPdf = largePdf; completedSnapshot = snapshot; completedPrinter = null; RefreshCompletion();
        Capture("export-complete-en.png");
        LanguageBox.SelectedIndex = 1;
        Check(CompletionTitle.Text == Lang.T("ExportedTitle") && CompletionBody.Text.Contains("60 × 45"), "completed export re-localizes and retains exported geometry");
        Capture("export-complete-pl.png");
        foreach (string language in new[] { "en", "pl" })
        foreach (decimal margin in new[] { 0m, .25m })
        foreach (bool guide in new[] { false, true })
        {
            LanguageBox.SelectedIndex = language == "pl" ? 1 : 0;
            var saved = snapshot.State with { Paper = snapshot.State.Paper with { MarginInches = margin }, AssemblyGuide = guide };
            completedSnapshot = snapshot with { State = saved, Layout = LayoutEngine.Calculate(saved.Size, saved.Paper) };
            RefreshCompletion();
            Check(CompletionBody.Text.Contains(Lang.T("ActualSize")) && (margin == 0 || CompletionBody.Text.Contains(Lang.T("TrimHint"))),
                $"actual-size completion instruction {language} margin={margin} guide={guide}");
            if (margin > 0 && !guide) Capture($"margin-complete-{language}.png");
        }
        Completion.Visibility = Visibility.Collapsed;
        dirty = false;
        closing = true; recoveryTimer.Stop(); recoveryCancellation.Cancel();
        File.WriteAllText(Path.Combine(directory, "app-checks.txt"), $"PASS: {count} integrated editor checks. No physical print jobs submitted.\n");
        return count;
    }

    private static void CreateArtwork(string path)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(248, 239, 214)), null, new Rect(0, 0, 1000, 750));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(232, 174, 115)), null, new Point(500, 366), 265, 265);
            var stem = new Pen(new SolidColorBrush(Color.FromRgb(54, 93, 69)), 12);
            for (int i = 0; i < 9; i++)
            {
                double x = 260 + i * 60, y = 280 + (i % 3) * 65;
                dc.DrawLine(stem, new Point(500, 700), new Point(x, y));
                dc.PushTransform(new RotateTransform(i * 27 - 100, x, y + 120));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb((byte)(65 + i * 7), (byte)(100 + i * 6), (byte)(72 + i * 4))), null, new Point(x, y + 120), 27, 76);
                dc.Pop();
                for (int petal = 0; petal < 7; petal++)
                {
                    double a = petal * Math.PI * 2 / 7;
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb((byte)(160 + i * 7), (byte)(70 + i * 9), (byte)(65 + i * 7))), null, new Point(x + Math.Cos(a) * 32, y + Math.Sin(a) * 32), 27, 27);
                }
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(236, 198, 92)), null, new Point(x, y), 18, 18);
            }
        }
        var bitmap = new RenderTargetBitmap(1000, 750, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
}
