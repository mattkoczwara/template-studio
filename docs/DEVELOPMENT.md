# Developing from a Windows checkout

Use the .NET SDK selected by [global.json](../global.json). This checkout can use `.tools/dotnet/dotnet.exe`; otherwise the launcher uses `dotnet.exe` on PATH. Installed users do not need these development tools.

## Latest Build desktop shortcut

From the repository root, run once:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-DevelopmentShortcut.ps1
```

**Mosaic Studio — Latest Build** uses the application icon and the checkout working directory. Rerun the command after moving the checkout. It updates only this developer shortcut; the application's installer shortcuts and other desktop files remain separate. `-DesktopPath` selects an existing alternative folder for verification.

Each click builds the current sources through the ordinary incremental **Release / win-x64** pipeline with locked package restore, then opens the exact executable reported by MSBuild. Build helpers stay hidden; the editor opens normally. There is no clean, full regression run, publish, installer build or application-data reset. Unchanged sources reuse their binary; changed sources rebuild. The first package restore may need Internet access; later launches can reuse the local SDK and package cache.

If the same development executable is open, save your work and close its window before clicking again. The launcher refuses to rebuild or replace it and never terminates it. Editors from other executable locations can remain open. Failed builds leave the earlier binary on disk but never launch it as a fallback.

## Terminal use and troubleshooting

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Launch-Development.ps1
```

`-DotnetPath` selects an SDK executable. `-NoDialogs` writes failures to stderr and returns a nonzero exit code for automation. An SDK incompatible with `global.json`, compiler errors or an editor still holding its build files must be resolved before retrying.

Logs live under ignored `artifacts/development/`:

- `launcher.log` records SDK commands, build diagnostics, the exact executable and titled editor process, or an actionable failure.
- `sdk.stdout.log` and `sdk.stderr.log` contain the latest SDK operation.
- `startup.stdout.log` and `startup.stderr.log` contain output from the launched application.
- `build/` contains this launcher's MSBuild outputs, separate from normal project `bin/` directories.

The launcher does not clear personal projects or `%LocalAppData%/MosaicTemplateStudio` settings, calibration, recovery or image caches. Normal application activity can append operational logs. A startup timeout leaves the application available for inspection; save and close any editor that appears before retrying.

## Focused verification

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-DevelopmentLauncher.ps1
```

The check uses a disposable WPF project in a checkout path containing spaces. It verifies first launch, unchanged-source incremental reuse, a source revision rebuild, refusal to rebuild an open editor with save-and-close guidance, compiler and early-startup failures, missing SDK handling, and preservation of project/settings/calibration/recovery/cache sentinels. It also verifies shortcut target, quoted arguments, hidden build helper, working directory, icon, idempotent update and unrelated Desktop-file preservation. Only its own clean fixture windows receive graceful close requests.

The October 22, 2026 run passed **39 checks**. The same source checkout launched the real development editor, refused a second launch without disturbing that process, and closed that clean smoke-test editor normally. Existing non-log user-data files remained byte-identical. The real Desktop shortcut was updated and its properties verified; seven other shortcuts retained their hashes. Evidence is in ignored `artifacts/development-launcher-checks/d7f8c53262694676b98748894fb22af0/` and `artifacts/development-launcher-real/`.

This establishes launcher behavior and titled native editor-window readiness. A fresh visual inspection of that launched window was blocked by the computer-use sandbox helper failing to initialize; it does not establish new rendered-UI acceptance. Release packaging and clean-machine offline installation have separate checks in [DISTRIBUTION.md](DISTRIBUTION.md).
