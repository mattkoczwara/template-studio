using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Mosaic.App;

public partial class MainWindow
{
    // Real Windows WPF lifecycle with isolated synthetic artwork and local data.
    internal async Task<int> RunLifecycleChecksAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var evidence = new List<string>();
        var probes = new List<MainWindow>();
        var writerGates = new List<TaskCompletionSource>();
        string input = Path.Combine(directory, "lifecycle-artwork.png");
        CreateArtwork(input);
        string inputHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(input)));
        void Check(bool result, string description)
        {
            if (!result) throw new InvalidOperationException(description);
            evidence.Add(description);
        }
        MainWindow NewWindow(string name, string language = "en")
        {
            var window = new MainWindow(["--checks", "--" + language], Path.Combine(directory, name));
            window.ErrorPresenter = message => throw new InvalidOperationException("Unexpected editor error: " + message);
            probes.Add(window); window.Show();
            return window;
        }
        void Closed(MainWindow window, string scenario)
        {
            Check(!window.IsVisible && window.resourcesDisposed && window.sourceLease == null && window.job == null &&
                !File.Exists(window.recovery.RecoveryPath) &&
                !Directory.EnumerateFiles(window.settings.RootPath, "*.mosaic.lock", SearchOption.AllDirectories).Any(), scenario);
        }
        void Empty(MainWindow window, string scenario)
        {
            Check(window.source == null && window.sourceLease == null && window.layout == null &&
                window.Preview.Image == null && window.Preview.Layout == null && window.enhancedPath == null &&
                window.projectPath == null && window.completedPdf == null && window.completedSnapshot == null &&
                !window.dirty && !window.undoStates.Any() && !window.redoStates.Any() &&
                !File.Exists(window.recovery.RecoveryPath) && window.WelcomeScroll.IsVisible &&
                !window.PrintButton.IsEnabled && !window.ExportButton.IsEnabled, scenario);
        }
        try
        {
            foreach (string language in new[] { "en", "pl" })
            {
                foreach (bool completedWriter in new[] { false, true })
                {
                    var empty = NewWindow($"empty-{language}-{completedWriter}", language);
                    bool closedEvent = false; empty.Closed += (_, _) => closedEvent = true;
                    if (completedWriter) empty.recoveryTask = Task.CompletedTask;
                    await CheckCloseAsync(empty);
                    Closed(empty, $"{language}: empty close, completed writer={completedWriter}");
                    Check(closedEvent, "Actual Closed event fired");
                }
                var window = NewWindow("document-" + language, language);
                var errors = new List<string>(); window.ErrorPresenter = errors.Add;
                await window.ImportAsync(input);
                var imported = window.source; var preview = window.Preview.Image;
                window.recoveryTask = window.RecoverEditsAsync(); await window.recoveryTask;
                var answer = AnswerLifecycleQuestionAsync(window, UserChoice.Cancel);
                Check(!await window.RemoveImageAsync(), "Removal Cancel is declined"); await answer;
                Check(ReferenceEquals(window.source, imported) && ReferenceEquals(window.Preview.Image, preview) &&
                    window.dirty && File.Exists(window.recovery.RecoveryPath), "Cancel preserves source, preview, dirty state and checkpoint");
                answer = AnswerLifecycleQuestionAsync(window, UserChoice.Cancel);
                await CheckCloseAsync(window); await answer;
                Check(window.IsVisible && !window.closing && !window.closeRequested && !window.resourcesDisposed &&
                    ReferenceEquals(window.source, imported), "Close Cancel retains usable editor and live ownership");
                await window.images.ClearCacheAsync(null);
                Check(File.Exists(imported!.OriginalPath), "Canceled close retains source cache lease");
                window.recovery.DeleteOwned();
                window.recoveryTask = window.RecoverEditsAsync(); await window.recoveryTask;
                Check(File.Exists(window.recovery.RecoveryPath), "Canceled close permits recovery to run again");

                // Inject only native file-selection cancellation; the WPF Save choice is real.
                window.SavePathChooser = _ => null;
                answer = AnswerLifecycleQuestionAsync(window, UserChoice.Save);
                Check(!await window.RemoveImageAsync(), "Canceled save destination prevents removal"); await answer;
                answer = AnswerLifecycleQuestionAsync(window, UserChoice.Save);
                await CheckCloseAsync(window); await answer;
                Check(window.IsVisible && window.dirty && File.Exists(window.recovery.RecoveryPath), "Canceled save destination prevents close and retains recovery");
                window.SavePathChooser = null;

                string blockedParent = Path.Combine(window.settings.RootPath, "file-instead-of-folder");
                await File.WriteAllTextAsync(blockedParent, "sentinel");
                window.projectPath = Path.Combine(blockedParent, "cannot-save.mosaic");
                answer = AnswerLifecycleQuestionAsync(window, UserChoice.Save);
                Check(!await window.RemoveImageAsync(), "Failed Save prevents removal"); await answer;
                Check(errors.Last() == Lang.T("SaveError") && window.dirty && window.sourceLease != null &&
                    File.Exists(window.recovery.RecoveryPath), "Removal save failure reports SaveError and preserves document");
                answer = AnswerLifecycleQuestionAsync(window, UserChoice.Save);
                await CheckCloseAsync(window); await answer;
                Check(window.IsVisible && errors.Last() == Lang.T("SaveError") && !window.resourcesDisposed,
                    "Close save failure reports SaveError and retains live resources");
                Check(await File.ReadAllTextAsync(blockedParent) == "sentinel", "Failed saves preserve existing destination bytes");
                window.WidthBox.Text = "invalid";
                answer = AnswerLifecycleQuestionAsync(window, UserChoice.Save);
                await CheckCloseAsync(window); await answer;
                Check(window.IsVisible && window.dimensionInvalid && window.source != null, "Invalid pending dimensions cannot be saved and closed");
                window.WidthBox.Text = language == "pl" ? "24,125" : "24.125";
                window.CommitDimension(window.WidthBox);
                Check(window.state.Size.WidthInches == 24.125m, "Decimal-comma or decimal-point input survives aborted close");
                string savedProject = Path.Combine(directory, "saved-removal-" + language + ".mosaic");
                window.projectPath = savedProject;
                answer = AnswerLifecycleQuestionAsync(window, UserChoice.Save);
                Check(await window.RemoveImageAsync(), "Save/removal succeeds after prior close cancellation and failures"); await answer;
                Empty(window, "Save removal returns completely clean initial state");
                Check((await projects.OpenAsync(savedProject)).State.Size.WidthInches == 24.125m, "Save/removal preserves exact dimensions in portable project");
                Check(!await window.RemoveImageAsync(), "Repeated removal from empty state is harmless");
                await window.ImportAsync(input);
                answer = AnswerLifecycleQuestionAsync(window, UserChoice.Discard);
                Check(await window.RemoveImageAsync(), "Unedited import Discard removes successfully"); await answer;
                Empty(window, "Unedited removal clears dependent state");
                await window.OpenProjectAsync(savedProject);
                Check(await window.RemoveImageAsync(), "Clean saved image removes without a question");
                await window.OpenProjectAsync(savedProject);
                window.WidthBox.Text = "30"; window.CommitDimension(window.WidthBox);
                answer = AnswerLifecycleQuestionAsync(window, UserChoice.Discard);
                Check(await window.RemoveImageAsync(), "Edited image Discard removes successfully"); await answer;
                Empty(window, "Edited removal clears validation and history");

                await window.ImportAsync(input);
                await window.EnhanceAsync();
                Check(window.state.Quality == Mosaic.Core.QualityMode.Enhanced && File.Exists(window.enhancedPath), "Real enhancement completes");
                string pdfPath = Path.Combine(directory, "enhanced-" + language + ".pdf");
                var snapshot = await window.SnapshotAsync(CancellationToken.None);
                await window.RunJobAsync("Exporting", async token =>
                {
                    await window.pdf.ExportAsync(snapshot, pdfPath, language, null, token);
                    window.completedPdf = pdfPath; window.completedSnapshot = snapshot; window.RefreshCompletion();
                });
                window.CompletionClose(window, new RoutedEventArgs());
                var preferencesBefore = window.preferences; var paperBefore = window.state.Paper;
                answer = AnswerLifecycleQuestionAsync(window, UserChoice.Discard);
                Check(await window.RemoveImageAsync(), "Removal after enhancement/export succeeds"); await answer;
                Empty(window, "Enhanced and output references are cleared");
                Check(File.Exists(pdfPath) && window.preferences == preferencesBefore && window.state.Paper == paperBefore,
                    "Removal preserves exported file, language, preferences and paper");
                await CheckCloseAsync(window); Closed(window, "Close after removal completes cleanly");

                var savedClose = NewWindow("save-close-" + language, language);
                await savedClose.ImportAsync(input);
                savedClose.WidthBox.Text = "60"; savedClose.CommitDimension(savedClose.WidthBox);
                string closeProject = Path.Combine(directory, "saved-on-close-" + language + ".mosaic");
                savedClose.projectPath = closeProject;
                answer = AnswerLifecycleQuestionAsync(savedClose, UserChoice.Save);
                await CheckCloseAsync(savedClose); await answer;
                Closed(savedClose, "Dirty 60-inch Save close completes");
                var reopened = await projects.OpenAsync(closeProject);
                Check(reopened.State.Size.WidthInches == 60 && reopened.State.SourceHash.Equals(inputHash, StringComparison.OrdinalIgnoreCase),
                    "Save on close preserves source and 60-inch editable geometry");
                var cleanClose = NewWindow("clean-close-" + language, language);
                await cleanClose.OpenProjectAsync(closeProject); cleanClose.recoveryTask = Task.CompletedTask;
                await CheckCloseAsync(cleanClose); Closed(cleanClose, "Clean populated close with completed writer reaches Closed");
                var discardClose = NewWindow("discard-close-" + language, language);
                await discardClose.ImportAsync(input);
                answer = AnswerLifecycleQuestionAsync(discardClose, UserChoice.Discard);
                await CheckCloseAsync(discardClose); await answer;
                Closed(discardClose, "Dirty Discard close without pending writer reaches Closed");
            }

            var replacement = NewWindow("replacement-checkpoints");
            await replacement.ImportAsync(input);
            replacement.recoveryTask = replacement.RecoverEditsAsync(); await replacement.recoveryTask;
            string portable = Path.Combine(directory, "checkpoint-save.mosaic"); replacement.projectPath = portable;
            Check(await replacement.SaveAsync(false) && !File.Exists(replacement.recovery.RecoveryPath),
                "Durable Save resolves obsolete checkpoint");
            replacement.WidthBox.Text = "31"; replacement.CommitDimension(replacement.WidthBox);
            replacement.recoveryTask = replacement.RecoverEditsAsync(); await replacement.recoveryTask;
            Check(await replacement.OpenProjectAsync(portable) && !File.Exists(replacement.recovery.RecoveryPath) && !replacement.dirty,
                "Clean project replacement resolves discarded document checkpoint");
            replacement.WidthBox.Text = "32"; replacement.CommitDimension(replacement.WidthBox);
            var replaceGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            writerGates.Add(replaceGate);
            var priorState = replacement.state; var priorSource = replacement.source;
            replacement.recoveryTask = Task.Run(async () =>
            {
                await replaceGate.Task;
                await replacement.projects.SaveAsync(replacement.recovery.RecoveryPath, priorState, priorSource!);
            });
            var importingReplacement = replacement.ImportAsync(input);
            await Task.Delay(40);
            Check(!importingReplacement.IsCompleted && ReferenceEquals(replacement.source, priorSource) && replacement.sourceLease != null,
                "Import replacement holds old document/source lease until checkpoint drains");
            replaceGate.SetResult(); await importingReplacement;
            Check(!File.Exists(replacement.recovery.RecoveryPath) && replacement.dirty,
                "Replacement import leaves no stale old-document checkpoint");
            var replaceAnswer = AnswerLifecycleQuestionAsync(replacement, UserChoice.Discard);
            await CheckCloseAsync(replacement); await replaceAnswer;
            Closed(replacement, "Close after checkpointed replacements completes");

            // Use real workers as well as deterministic job gates below.
            var realImport = NewWindow("close-real-import");
            var importWork = realImport.ImportAsync(input);
            await CheckCloseAsync(realImport); await importWork;
            Closed(realImport, "Close during real image decode drains import without partial document");
            var realEnhance = NewWindow("close-real-enhance");
            await realEnhance.ImportAsync(input);
            var enhanceWork = realEnhance.EnhanceAsync();
            var enhanceAnswer = AnswerLifecycleQuestionAsync(realEnhance, UserChoice.Discard);
            await CheckCloseAsync(realEnhance); await enhanceWork; await enhanceAnswer;
            Closed(realEnhance, "Close during real enhancement drains worker before source release");

            var finishedOutput = NewWindow("close-enhanced-output");
            await finishedOutput.ImportAsync(input); await finishedOutput.EnhanceAsync();
            Check(finishedOutput.state.Quality == Mosaic.Core.QualityMode.Enhanced, "Second real enhancement completes before output close");
            var outputSnapshot = await finishedOutput.SnapshotAsync(CancellationToken.None);
            string finishedPdf = Path.Combine(directory, "close-enhanced-output.pdf");
            await finishedOutput.RunJobAsync("Exporting", async token =>
                await finishedOutput.pdf.ExportAsync(outputSnapshot, finishedPdf, "en", null, token));
            var outputAnswer = AnswerLifecycleQuestionAsync(finishedOutput, UserChoice.Discard);
            await CheckCloseAsync(finishedOutput); await outputAnswer;
            Closed(finishedOutput, "Direct close after real enhancement and export completes");
            Check(File.Exists(finishedPdf), "Close preserves completed export file");

            var realExport = NewWindow("close-real-export");
            await realExport.ImportAsync(input);
            var exportSnapshot = await realExport.SnapshotAsync(CancellationToken.None);
            string atomicPdf = Path.Combine(directory, "atomic-close-export.pdf");
            await realExport.pdf.ExportAsync(exportSnapshot, atomicPdf);
            byte[] priorPdf = await File.ReadAllBytesAsync(atomicPdf);
            var exportWork = realExport.RunJobAsync("Exporting", async token =>
                await realExport.pdf.ExportAsync(exportSnapshot, atomicPdf, "pl", null, token));
            var exportAnswer = AnswerLifecycleQuestionAsync(realExport, UserChoice.Discard);
            await CheckCloseAsync(realExport); bool exportCompleted = await exportWork; await exportAnswer;
            Closed(realExport, "Close during real PDF generation drains output job");
            Check(exportCompleted || (await File.ReadAllBytesAsync(atomicPdf)).SequenceEqual(priorPdf),
                "Canceled real export preserves prior PDF destination");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Canceled real output leaves no partial temporary file");

            var racing = NewWindow("writer-race");
            await racing.ImportAsync(input);
            var ownedSource = racing.source!;
            var writerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            writerGates.Add(writerGate);
            racing.recoveryTask = Task.Run(async () =>
            {
                await writerGate.Task;
                await racing.projects.SaveAsync(racing.recovery.RecoveryPath, racing.state, ownedSource);
            });
            var response = AnswerLifecycleQuestionAsync(racing, UserChoice.Discard);
            racing.Close(); var firstClose = racing.closeTask; racing.Close();
            await Task.Delay(30);
            Check(ReferenceEquals(firstClose, racing.closeTask) && racing.IsVisible && !racing.resourcesDisposed && racing.sourceLease != null,
                "Repeated close coalesces and drains writer while holding source lease");
            Check(!await racing.RemoveImageAsync() && !await racing.RunJobAsync("Working", _ => Task.CompletedTask),
                "Close transition blocks competing removal and jobs");
            writerGate.SetResult(); await firstClose!; await response;
            Closed(racing, "Close drains writer before recovery deletion and source release");
            await new Mosaic.Imaging.ImageService(Path.Combine(racing.settings.RootPath, "image-cache")).ClearCacheAsync(null);
            Check(!File.Exists(ownedSource.OriginalPath), "Closed source lease releases for explicit cache cleanup");

            // Deterministic job gates exercise cancellation races independently of worker speed.
            foreach (string stage in new[] { "Importing", "Enhancing", "Exporting", "Saving" })
            {
                var active = NewWindow("active-" + stage);
                await active.ImportAsync(input);
                bool workerFinished = false;
                var work = active.RunJobAsync(stage, async token =>
                {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { await Task.Delay(25); workerFinished = true; }
                });
                var responseTask = AnswerLifecycleQuestionAsync(active, UserChoice.Cancel);
                await CheckCloseAsync(active); await responseTask;
                Check(!await work && workerFinished && active.IsVisible && active.sourceLease != null && !active.closeRequested,
                    "Close drains " + stage + "; Cancel preserves document");
                responseTask = AnswerLifecycleQuestionAsync(active, UserChoice.Discard);
                await CheckCloseAsync(active); await responseTask;
                Closed(active, "Close after canceled " + stage + " completes");
            }

            var denied = NewWindow("recovery-denied");
            var cleanupErrors = new List<string>(); denied.ErrorPresenter = cleanupErrors.Add;
            await denied.ImportAsync(input);
            denied.recoveryTask = denied.RecoverEditsAsync(); await denied.recoveryTask;
            using (var locked = new FileStream(denied.recovery.RecoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var responseTask = AnswerLifecycleQuestionAsync(denied, UserChoice.Discard);
                await CheckCloseAsync(denied); await responseTask;
                Check(denied.IsVisible && !denied.resourcesDisposed && denied.sourceLease != null &&
                    cleanupErrors.Last() == Lang.T("CloseError") && File.Exists(denied.recovery.RecoveryPath),
                    "Recovery deletion failure leaves document live with actionable close error");
                responseTask = AnswerLifecycleQuestionAsync(denied, UserChoice.Discard);
                Check(!await denied.RemoveImageAsync(), "Locked recovery prevents partial removal"); await responseTask;
                Check(denied.source != null && cleanupErrors.Last() == Lang.T("RecoveryError"), "Removal recovery failure preserves document with precise error");
            }
            var retry = AnswerLifecycleQuestionAsync(denied, UserChoice.Discard);
            Check(await denied.RemoveImageAsync(), "Removal succeeds after failed close when recovery lock clears"); await retry;
            await CheckCloseAsync(denied); Closed(denied, "Retry closes without disposed recovery regression");
            Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(input))) == inputHash,
                "All journeys preserve original artwork bytes");
            File.WriteAllText(Path.Combine(directory, "lifecycle-checks.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                Passed = true, Checks = evidence.Count, Evidence = evidence,
                Method = "Real Windows WPF windows, Closing/Closed, localized choices, real storage/AI/PDF and controlled concurrency gates",
                NativeInputInjected = false, SaveDialogCancellation = "Injected at file-selection boundary", PhysicalPrintVerified = false
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return evidence.Count;
        }
        finally
        {
            foreach (var gate in writerGates) gate.TrySetResult();
            foreach (var window in probes.Where(w => !w.resourcesDisposed)) await window.FinishChecksAsync();
        }
    }

    private static async Task AnswerLifecycleQuestionAsync(MainWindow window, UserChoice choice)
    {
        for (int i = 0; i < 500; i++)
        {
            if (window.OwnedWindows.OfType<QuestionWindow>().FirstOrDefault() is { } question)
            {
                if (choice == UserChoice.Cancel) question.Close();
                else (choice == UserChoice.Save ? question.PrimaryButton : question.DiscardButton!)
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("Unsaved-change question did not appear");
    }
    private static async Task CheckCloseAsync(MainWindow window)
    {
        window.Close();
        if (window.closeTask != null) await window.closeTask;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
