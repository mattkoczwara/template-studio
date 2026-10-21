# Windows distribution

The release is a self-contained Windows x64 .NET 10 folder wrapped by a per-user Inno Setup 7.1 installer. It requires Windows 10 build 19041 or later and installs under `%LocalAppData%\Programs\Mosaic Template Studio`. End users do not install .NET, Python, CUDA or model packages. NVIDIA acceleration uses the existing Vulkan-capable graphics driver; Standard remains usable if enhancement is unavailable.

The English/Polish installer provides a Start-menu shortcut, a checked-by-default desktop shortcut, optional `.mosaic` Open With association, and normal Windows uninstall. It requests no administrator rights. Reinstall uses a stable application ID. The uninstaller removes installed application files and its association/shortcuts; it has no deletion directives for personal projects, settings or recovery.

## Building

Use the checked-in exact package versions and lockfiles. The build host needs the .NET 10 SDK and Inno Setup 7.1; these are build tools, never end-user prerequisites. The current workspace has SDK `.tools\dotnet\dotnet.exe` and compiler `.tools\inno\compiler\ISCC.exe`.

```powershell
.\scripts\Build-Release.ps1
```

The script runs the Core, Storage, Output, Imaging and App console checks sequentially, publishes `win-x64` with `SelfContained=true`, `PublishTrimmed=false`, `PublishAot=false`, `PublishSingleFile=false`, checks the offline runtime/AI payload and full license notices, hashes every published file, then compiles the installer. SDK 10.0.402 and runtime 10.0.13 are pinned. Publication uses a fresh staging folder so stale dependencies cannot enter a package; a previous published folder is retained as a `.previous-*` build artifact. `-NoRestore` uses previously restored packages. `-SkipTests` is intended only when the same unchanged sources already passed their checks during the current release validation. Explicit tool paths can be supplied with `-DotnetPath` and `-IsccPath`.

Artifacts:

- `artifacts/publish/win-x64/` — runnable offline application folder.
- `artifacts/installer/MosaicTemplateStudio-1.0.0-win-x64-Setup.exe` — complete installer.
- Adjacent `.sha256` — SHA-256 of the installer.
- `artifacts/verification/published-files.json` — each packaged path, byte count and SHA-256.
- `artifacts/verification/release-package.json` — measured package sizes, hash, version and signing status.

The app project includes `vendor/ai/**` under `ai/**`, `vendor/licenses/**` under `licenses/**`, and root third-party notices. See [AI.md](AI.md) for worker/model provenance and [PRINTING.md](PRINTING.md) for the physical validation boundary. The build requires both GPU and CPU workers, all four application-local Microsoft runtime DLLs, model weights/parameters and license records; it verifies every file declared by `ai/assets.json` before creating the installer.

## Compiler provenance

Inno Setup 7.1.0 x64 was acquired from the [upstream release](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_0), linked by the [official download page](https://jrsoftware.org/isdl.php). The downloaded `innosetup-7.1.0-x64.exe` SHA-256 is `0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f`. Its Authenticode signature verified as valid, publisher Pyrsys B.V., certificate thumbprint `E0AB19C8D38CBF9C44709925122A7A02F8C70CB7`.

The compiler was installed into `.tools/inno/compiler` in per-user mode without icons or optional association tasks. It is not shipped inside the application. Inno Setup's license in that build-tool directory permits use for any purpose, including commercial applications, under its attribution and redistribution conditions; the upstream website also requests purchase for commercial use. Original project code is MIT-licensed; the root LICENSE is included in new application builds. Third-party payloads retain their own terms.

## Release evidence boundary

Installer compilation, payload checks and an install/uninstall smoke test on the development host establish only those observed results. A separate clean Windows machine with networking disabled is required to establish the clean-offline acceptance criterion; the development host already has graphics drivers and tools. Likewise a software print preparation check cannot prove Epson seam registration or Canon printable-area behavior.

`scripts/Verify-Installer.ps1` installs into a new workspace test directory, checks every installed file against the publication hash manifest, verifies desktop/Start shortcuts and project association, then launches the installed executable with `--verify-install` and isolated workspace test data. That entry exercises the real installed runtime journey and produces `runtime-check/installed-runtime.json`; exit 0 and `passed: true` are required. The script then runs the normal uninstaller and confirms an unknown personal-project sentinel survives. It refuses to overwrite an already registered app installation. `-Language polish` exercises the Polish installer resources. These checks do not simulate a clean offline Windows machine or establish physical printer behavior.

The release package is unsigned unless `release-package.json` reports a valid signature. No signing certificate was supplied. Signing/reputation review is a distribution consideration and cannot be manufactured by a local build. Windows may display an unknown-publisher reputation prompt for an unsigned installer.

## Measured final package

The final 1.0.0 installer is **63,337,792 bytes** (about 60.4 MiB), SHA-256 `d0032f09ed2ad86b08f9a6c0bdffe2e3cb561948e3f2d14a05b93efb55a5608a`. Its self-contained payload contains **456 files totaling 204,121,067 bytes**. Exact records are `artifacts/verification/release-package.json`, `published-files.json` and `final-summary.json`; the complete installer remains at `artifacts/installer/MosaicTemplateStudio-1.0.0-win-x64-Setup.exe` with its adjacent `.sha256` file.

The same final hash passed both English and Polish per-user installation/runtime/uninstallation cycles. All payload hashes matched; Desktop/Start shortcuts and project association pointed to the actual installation; the installed executable passed 45 integrated editor checks on its bundled runtime 10.0.13 with isolated test data; normal uninstall removed registered files/shortcuts/association and preserved an unknown personal-project sentinel. English evidence is `artifacts/installer-checks/4952a1792ca64dcf82ee9a1233d0df68/`; Polish evidence is `artifacts/installer-checks/b42a8f3c6652429b80d39d0b1b242eb5/`.

These actual installed-host journeys do not establish clean-machine offline behavior, signing reputation, physical printer measurements, fluent Polish review or independent artist usability. See [ACCEPTANCE.md](ACCEPTANCE.md) for the complete requirement map and remaining external acceptance gates.

