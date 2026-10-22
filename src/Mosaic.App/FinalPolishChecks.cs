using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Mosaic.Core;
using Mosaic.Storage;

namespace Mosaic.App;

public partial class MainWindow
{
    private async Task<int> CheckFinalPolishAsync(string directory, string portableProject)
    {
        int count = 0;
        var fixtures = new (ArtworkSize Size, PaperSpec Paper, RectInches Extent, string Name)[]
        {
            (new(24, 18), new(), new(0, 0, 25.5m, 22), "borderless"),
            (new(24, 18), new(MarginInches: .25m), new(-.25m, -.25m, 24.5m, 21.5m), "margins"),
            (new(24, 18), new(Orientation: OrientationMode.Landscape), new(0, 0, 33, 25.5m), "landscape"),
            (new(22, 17), new(), new(0, 0, 22, 17), "exact-fit"),
            (new(60.125m, 45.09375m), new(12, 18, .375m, OrientationMode.Portrait), new(-.375m, -.375m, 68.25m, 52.5m), "large-custom")
        };
        foreach (var fixture in fixtures)
        {
            var plan = LayoutEngine.Calculate(fixture.Size, fixture.Paper);
            UiCheck(PreviewSurface.PaperExtent(plan) == fixture.Extent, "exact full-paper extent: " + fixture.Name, ref count);
            foreach (var tile in plan.Pages)
            {
                var sheet = PreviewSurface.SheetInArtwork(plan, tile);
                UiCheck(sheet.X + tile.PaperBounds.X == tile.ArtworkBounds.X && sheet.Y + tile.PaperBounds.Y == tile.ArtworkBounds.Y &&
                    sheet.Width == plan.PageWidth && sheet.Height == plan.PageHeight,
                    "preview sheet inverts the output tile transform: " + fixture.Name, ref count);
            }
            state = state with { Size = fixture.Size, Paper = fixture.Paper, KeepProportions = false, Composition = new() };
            SyncControls(); Preview.Fit(); await UiSettledAsync();
            var snapshot = await SnapshotAsync(CancellationToken.None);
            UiCheck(ReferenceEquals(Preview.Layout, snapshot.Layout) && snapshot.State.Size == fixture.Size,
                "full-paper view preserves authoritative output geometry: " + fixture.Name, ref count);
            CaptureUi((FrameworkElement)Content, directory, "paper-" + fixture.Name + ".png", 96);
            if (fixture.Name == "borderless")
            {
                GridCheck.IsChecked = false; await UiSettledAsync();
                CaptureUi((FrameworkElement)Content, directory, "paper-grid-hidden.png", 96);
                GridCheck.IsChecked = true;
            }
        }

        // Export a real fractional-size PDF. The default filename must not round its dimensions.
        state = state with { Size = new(24.125m, 18.09375m), Paper = new(), KeepProportions = true, Composition = new(), AssemblyGuide = false };
        SyncControls(); ResetHistory();
        string output = Path.Combine(directory, DefaultPdfName(state));
        var exported = await SnapshotAsync(CancellationToken.None);
        await pdf.ExportAsync(exported, output, "en");
        UiCheck(Path.GetFileName(output).Contains("24.125x18.09375in"), "fractional PDF filename retains exact model dimensions", ref count);
        UiCheck(DisplayInches(18.09375m).StartsWith('≈') && !DisplayInches(24.125m).StartsWith('≈'),
            "compact summaries mark only rounded dimensions", ref count);
        completedPdf = output; completedSnapshot = exported; completedPrinter = null;
        foreach (int language in new[] { 0, 1 })
        {
            LanguageBox.SelectedIndex = language;
            foreach (var (width, height, dpi) in UiViewports)
            {
                Width = width; Height = height;
                Completion.Visibility = Visibility.Collapsed;
                await UiSettledAsync(); ExportButton.Focus();
                RefreshCompletion(); await UiSettledAsync();
                UiCheck(IsInside(CompletionTitle, Completion) && IsInside(OpenPdfButton, Completion) &&
                    IsInside(ShowFolderButton, Completion) && IsInside(BackToEditingButton, Completion),
                    "completion heading and all actions fit: " + Lang.Language, ref count);
                var primary = OpenPdfButton.TranslatePoint(new Point(), CompletionActions);
                var secondary = ShowFolderButton.TranslatePoint(new Point(), CompletionActions);
                var back = BackToEditingButton.TranslatePoint(new Point(), CompletionActions);
                UiCheck(Math.Abs(primary.Y - secondary.Y) < 1 && Math.Abs(primary.Y - back.Y) < 1,
                    "completion actions share one row: " + Lang.Language, ref count);
                UiCheck(Completion.ActualHeight < 380 && CompletionDetails.Text.Contains("≈") &&
                    ((string)CompletionDetails.ToolTip).Contains(18.09375m.ToString(CultureInfo.GetCultureInfo(language == 0 ? "en-US" : "pl-PL"))),
                    "compact completion preserves precise size in localized detail: " + Lang.Language, ref count);
                UiCheck(CompletionDetails.Text.Contains((new FileInfo(output).Length / 1048576d).ToString("0.0", Lang.Culture) + " MB"),
                    "completion file size uses the selected language's decimal separator", ref count);
                UiCheck(OpenPdfButton.IsKeyboardFocused, "completion focuses primary action", ref count);
                OpenPdfButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                UiCheck(ShowFolderButton.IsKeyboardFocused, "Tab reaches show-in-folder", ref count);
                ShowFolderButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                UiCheck(BackToEditingButton.IsKeyboardFocused, "Tab reaches back-to-editing", ref count);
                BackToEditingButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                UiCheck(OpenPdfButton.IsKeyboardFocused, "completion Tab wraps within the action group", ref count);
                OpenPdfButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                UiCheck(BackToEditingButton.IsKeyboardFocused, "completion Shift-Tab wraps backwards", ref count);
                CaptureUi((FrameworkElement)Content, directory, $"final-completion-{Lang.Language}-{width}-{dpi}.png", dpi);
                ((IInvokeProvider)new ButtonAutomationPeer(BackToEditingButton).GetPattern(PatternInterface.Invoke)!).Invoke();
                await UiSettledAsync();
                UiCheck(!Completion.IsVisible && ExportButton.IsKeyboardFocused && PrintButton.IsEnabled,
                    "back-to-editing restores focus and output actions", ref count);
            }
        }
        Width = 1380; Height = 920; await UiSettledAsync();

        // Cancel retains the complete dirty document and its owned recovery checkpoint.
        dirty = true; recoveryTask = RecoverEditsAsync(); await recoveryTask;
        var retainedSource = source; var retainedState = state; var retainedPreview = Preview.Image;
        _ = Dispatcher.BeginInvoke(() => OwnedWindows.OfType<QuestionWindow>().Single().Close(), DispatcherPriority.Background);
        UiCheck(!await RemoveImageAsync() && ReferenceEquals(source, retainedSource) && state == retainedState &&
            ReferenceEquals(Preview.Image, retainedPreview) && File.Exists(recovery.RecoveryPath),
            "removal Cancel retains artwork, edits, preview and recovery", ref count);
        WidthBox.Text = "invalid"; CommitDimension(WidthBox);
        _ = Dispatcher.BeginInvoke(() => OwnedWindows.OfType<QuestionWindow>().Single().PrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.Background);
        UiCheck(!await RemoveImageAsync() && ReferenceEquals(source, retainedSource) && dirty && dimensionInvalid &&
            File.Exists(recovery.RecoveryPath), "removal Save with invalid pending inputs retains the document", ref count);
        WidthBox.Text = retainedState.Size.WidthInches.ToString(Lang.Culture); CommitDimension(WidthBox);

        // Save uses the existing project path, without an extra file chooser or confirmation.
        projectPath = Path.Combine(directory, "saved-before-removal.mosaic");
        string savedProject = projectPath;
        _ = Dispatcher.BeginInvoke(() => OwnedWindows.OfType<QuestionWindow>().Single().PrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.Background);
        UiCheck(await RemoveImageAsync(), "removal Save completes through existing persistence flow", ref count);
        var saved = await projects.OpenAsync(savedProject);
        UiCheck(saved.State.Size == retainedState.Size && saved.State.SourceHash == retainedSource!.Sha256,
            "removal Save preserves original and exact edits in portable project", ref count);
        await UiSettledAsync();
        UiCheck(source == null && sourceLease == null && layout == null && Preview.Image == null && Preview.Layout == null &&
            enhancedPath == null && projectPath == null && completedSnapshot == null && completedPdf == null && completedPrinter == null,
            "removal clears every image and output reference", ref count);
        UiCheck(!dirty && undoStates.Count == 0 && redoStates.Count == 0 && !File.Exists(recovery.RecoveryPath) &&
            WelcomeOpenButton.IsKeyboardFocused && !PrintButton.IsEnabled && !ExportButton.IsEnabled && !EditorSidebar.IsVisible,
            "removal returns clean import state with focus and no stale history or recovery", ref count);
        UiCheck(ArtworkTitle.Text == "" && SourceDetails.Text == "" && GridSummary.Text == "" && PaperSummary.Text == "" &&
            CompletionFile.Text == "" && CompletionDetails.Text == "" && CompletionBody.Text == "",
            "removal clears image-specific presentation", ref count);

        // A clean saved project clears directly. Reopening still works without the download.
        await OpenProjectAsync(savedProject);
        UiCheck(await RemoveImageAsync(), "saved clean artwork removes without a dirty-document question", ref count);
        await OpenProjectAsync(portableProject);
        UiCheck(source != null && state.Size == new ArtworkSize(60, 45), "portable project reopens after image removal", ref count);

        // A restored snapshot can remain leased when its replacement checkpoint failed.
        using (var interrupted = new RecoverySession(settings.RootPath))
        {
            await projects.SaveAsync(interrupted.RecoveryPath, state, source!);
            interrupted.Dispose();
            pendingRecovery = recovery.ClaimPending(); pendingRecoveryRestored = true;
            UiCheck(await RemoveImageAsync() && !File.Exists(interrupted.RecoveryPath) && pendingRecovery == null && !pendingRecoveryRestored,
                "removal explicitly resolves a restored recovery awaiting a replacement checkpoint", ref count);
        }
        await OpenProjectAsync(portableProject);

        // A genuinely pending writer must finish before deletion. Unrelated deferred recovery survives.
        ProfileBox.SelectedIndex = 1; CopiesBox.SelectedIndex = 1;
        state = state with { PrinterName = "Preserved verification printer" }; Changed();
        var printerState = state;
        var preservedPreferences = preferences;
        string cachedOriginal = source!.OriginalPath;
        using (var interrupted = new RecoverySession(settings.RootPath))
        {
            await projects.SaveAsync(interrupted.RecoveryPath, state, source);
            interrupted.Dispose();
            pendingRecovery = recovery.ClaimPending(); ShowNotice(true);
            UiCheck(pendingRecovery?.Path == interrupted.RecoveryPath, "unrelated recovery is available before removal", ref count);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checkpointState = state; var checkpointSource = source;
            recoveryTask = Task.Run(async () => { await gate.Task; await projects.SaveAsync(recovery.RecoveryPath, checkpointState, checkpointSource); });
            _ = Dispatcher.BeginInvoke(() => OwnedWindows.OfType<QuestionWindow>().Single().DiscardButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.Background);
            var removal = RemoveImageAsync();
            UiCheck(!removal.IsCompleted && ReferenceEquals(source, checkpointSource), "removal waits for an in-flight checkpoint before releasing source", ref count);
            gate.SetResult();
            UiCheck(await removal && !File.Exists(recovery.RecoveryPath) && File.Exists(interrupted.RecoveryPath) && pendingRecovery != null,
                "removal resolves its checkpoint and preserves unrelated deferred recovery", ref count);
            UiCheck(preferences == preservedPreferences && Lang.Language == "pl" && state.Paper == printerState.Paper &&
                state.PrinterProfile == printerState.PrinterProfile && state.PrinterName == printerState.PrinterName && state.Copies == printerState.Copies,
                "removal retains language, printer configuration and application preferences", ref count);
            pendingRecovery!.Resolve(); pendingRecovery = null; NoticeBar.Visibility = Visibility.Collapsed;
        }
        UiCheck(!await RemoveImageAsync(), "repeated removal from empty state is a harmless no-op", ref count);
        foreach (int language in new[] { 0, 1 })
        {
            LanguageBox.SelectedIndex = language;
            foreach (var (width, height, dpi) in UiViewports)
            {
                Width = width; Height = height; await UiSettledAsync();
                UiCheck(WelcomeScroll.IsVisible && !PreviewHeader.IsVisible && !PrintGuidance.IsVisible &&
                    IsInside(WelcomeOpenButton, WelcomeScroll), "removed empty state remains usable: " + Lang.Language, ref count);
                CaptureUi((FrameworkElement)Content, directory, $"removed-{Lang.Language}-{width}-{dpi}.png", dpi);
            }
        }
        await ImportAsync(cachedOriginal);
        UiCheck(source != null && state.Paper == printerState.Paper && state.PrinterName == printerState.PrinterName &&
            state.PrinterProfile == printerState.PrinterProfile && undoStates.Count == 0,
            "new import retains printer choices and starts fresh artwork history", ref count);
        state = state with { Title = printerState.Title }; SyncControls(); ResetHistory();
        foreach (int language in new[] { 0, 1 })
        {
            LanguageBox.SelectedIndex = language;
            foreach (var (width, height, dpi) in UiViewports)
            {
                Width = width; Height = height; await UiSettledAsync();
                UiCheck(IsInside(RemoveImageButton, PreviewHeader) && new ButtonAutomationPeer(RemoveImageButton).GetName() == Lang.T("RemoveImage"),
                    "localized removal action stays visible and accessible: " + Lang.Language, ref count);
                CaptureUi((FrameworkElement)Content, directory, $"final-editor-{Lang.Language}-{width}-{dpi}.png", dpi);
            }
        }
        var blocked = RunJobAsync("Working", async token => await Task.Delay(Timeout.Infinite, token));
        UiCheck(!RemoveImageButton.IsEnabled && !await RemoveImageAsync(), "removal is blocked during image/output jobs", ref count);
        CancelClick(this, new RoutedEventArgs()); await blocked;
        Width = 1380; Height = 920;
        File.WriteAllText(Path.Combine(directory, "final-polish-verification.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            Checks = count, GeometryFixtures = fixtures.Select(f => f.Name),
            WindowsDisplayScalingChanged = false, NativeInputInjected = false, PhysicalPrintVerified = false,
            Method = "Real WPF controls, automation peers, focus traversal, portable projects and PDF export; software renders at 96/120/144 DPI"
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return count;
    }
}
