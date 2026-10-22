# Remove Image and shutdown regression — October 21, 2026

## Confirmed cause

Both warnings came from one lifecycle defect. `WindowClosing` canceled the original WPF close, awaited `AllowReplaceAsync`, disposed recovery/source ownership through `FinishRecovery`, then called `Close()` from the still-active closing event. Empty documents, clean saved documents and Discard can complete all awaits synchronously. WPF rejects the second close; the editor remains visible with `closing = true` and its `RecoverySession` already disposed. A subsequent Remove Image calls `RecoverySession.DeleteOwned` on that disposed session.

The pre-correction Windows WPF reproduction captured both actual exceptions:

```text
System.InvalidOperationException:
Cannot set Visibility to Visible or call Show, ShowDialog, Close,
or WindowInteropHelper.EnsureHandle while a Window is closing.
  System.Windows.Window.VerifyNotClosing()
  System.Windows.Window.InternalClose(...)
  Mosaic.App.MainWindow.WindowClosing(...)

System.ObjectDisposedException: Cannot access a disposed object.
Object name: 'Mosaic.Storage.RecoverySession'.
  Mosaic.Storage.RecoverySession.DeleteOwned()
  Mosaic.App.MainWindow.RemoveImageAsync(...)
  Mosaic.App.MainWindow.RunJobAsync(...)
```

Local baseline evidence: `artifacts/lifecycle-baseline/exception-stacks.txt` retains the complete captured stacks; `baseline-lifecycle-reproduction.txt` records that the window stayed visible after close and removal returned false with the original Unexpected message. Previous local logs also recorded the same exception types. The catch-all message alone was never used as the diagnosis.

The previous editor checks called `Close()` on an empty secondary window but checked only recovery files. The global dispatcher handler displayed and handled the exception, so the harness could still report success. They did not establish that `Closed` occurred.

## Correction

- A close request always yields through the dispatcher before shutdown work, even when Save/Discard checks or checkpoint tasks complete synchronously. A separate request gate coalesces repeated closes and prevents new document jobs/checkpoints.
- Shutdown cancels and awaits the active job, then obtains the normal Save/Discard/Cancel decision. Cancel, canceled save selection, invalid inputs and genuine Save failures leave live document/recovery/source ownership. Pending dimension edits participate in that decision and are committed on Save.
- Remove, Import, Open, successful Save and Close share `ResolveDocumentRecoveryAsync`. It drains the active checkpoint before deleting only the current session's checkpoint and any explicitly restored prior checkpoint. Source ownership stays live until replacement is ready. Unrelated deferred recovery survives.
- Successful Save and document replacement resolve obsolete checkpoints. Otherwise a clean project opened after discarding another document could leave that discarded document's recovery indefinitely. Recovery revision tracking resets after resolution so an aborted transition can checkpoint the retained document again.
- Actual `Closed` releases resources once. A failed recovery deletion leaves the window usable, restarts checkpointing and reports a localized actionable error. Removal clears artwork, layout, enhancement/completion/output references, validation and undo/redo while retaining unrelated preferences and printer/paper choices.
- A destination-directory creation failure now becomes `ProjectSaveFailed` and the existing actionable SaveError. The atomic project writer still preserves prior destination/source bytes and removes incomplete temporary files.
- Failure diagnostics retain operation, exception type, HRESULT, exception chain, disposed object and stack. Arbitrary I/O messages containing artwork filenames/output paths are omitted. Dispatcher exceptions now fail the editor check process rather than being mistaken for success.

This follows product definition §§9–10 and the existing asynchronous processing/recovery contracts. No project schema, geometry/output engine, dependency or architectural recommendation changed.

## Regression evidence

Executed on Windows with the repository's .NET SDK **10.0.402**, Release / win-x64:

| Check | Result | Local evidence |
|---|---|---|
| Focused lifecycle suite | **101 passed**, no unhandled dispatcher exceptions | `artifacts/lifecycle-final/lifecycle-checks.json` |
| Final default editor suite, including those lifecycle checks | **375 passed**, no unhandled dispatcher exceptions | `artifacts/lifecycle-verified/app-checks.txt`, `artifacts/lifecycle-verified/lifecycle/lifecycle-checks.json` |
| Portable storage/recovery suite | **41 passed** | `artifacts/storage-checks/03850019eae94f8eb821bf49cb98597d/` and captured command output |
| Development executable rebuild | **0 warnings / 0 errors** | `artifacts/lifecycle-verified/development-build.log`; executable under `artifacts/development/build/bin/Mosaic.App/release_win-x64/` |
| Repository integrity | **100 local links**, pinned AI hashes and banner XML passed; `git diff --check` passed | `scripts/Check-Repository.ps1` and command output |

[LifecycleChecks.cs](../src/Mosaic.App/LifecycleChecks.cs) uses actual WPF windows and localized question controls. It checks empty/clean/dirty close, null/completed/active writers, actual `Closed`, repeated close/removal, unedited/edited removal, Save/Discard/Cancel, failed Save, invalid and Polish decimal-comma input, actual enhancement/PDF generation, completed-output preservation, close during real import/enhancement/export, controlled background-job cancellation, checkpoint/source-lease ordering, stale-checkpoint resolution, recovery-file deletion denial and successful retry. Original image hashes, portable 60-inch geometry, source-cache protection/release and absence of partial output files are verified. Existing editor checks additionally cover restored/unrelated deferred recovery, preference retention and source-independent portable reopen.

The normal editor/installed acceptance entry includes the lifecycle suite. A focused rerun is available:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $PWD '.tools/packages'
./.tools/dotnet/dotnet.exe run --project tests/Mosaic.App.Checks -c Release -r win-x64 -p:RestoreLockedMode=true -- artifacts/lifecycle-checks --lifecycle
./.tools/dotnet/dotnet.exe run --project tests/Mosaic.App.Checks -c Release -r win-x64 -p:RestoreLockedMode=true -- artifacts/app-checks
./.tools/dotnet/dotnet.exe run --project tests/Mosaic.Storage.Checks -c Release -r win-x64 -p:RestoreLockedMode=true
```

These are live Windows WPF dispatcher/control journeys. Native computer-use initialization failed twice with `helper_unknown_error: setup refresh had errors`; no injected mouse/keyboard or independent manual native-dialog verification is claimed. Save-file-picker cancellation is injected at its selection boundary; question-window choices, storage failures and file locks are real. Deterministic background-job gates supplement real import/enhancement/PDF workers. No physical printer jobs, OS display-scaling changes, installer rebuild or clean-machine installation were performed for this correction.
