# Contributing

Mosaic favors dependable physical output and a clear artist-facing workflow over feature count. Small reproducible fixes, printer measurements, Polish wording improvements and representative artwork-quality reports are especially useful.

Before substantial changes, read the relevant [product requirements](docs/POSTER_STUDIO_PRODUCT_DEFINITION.md), [implementation guide](docs/IMPLEMENTATION.md) and [AGENTS.md](AGENTS.md). Discuss scope through an issue when a proposal changes output geometry, dependencies, the project format or product boundaries.

## Development

Use Windows x64 and the SDK pinned in [global.json](global.json). Clone the repository, then:

```powershell
dotnet run --project src/Mosaic.App/Mosaic.App.csproj -c Release -r win-x64 -p:RestoreLockedMode=true
```

NuGet restore needs Internet access initially. Native AI workers and model weights are included under `vendor/ai/`; rebuilding the CPU worker additionally requires C++ Build Tools. See [AI provenance](docs/AI.md). Inno Setup is needed only to build an installer.

## Focused checks

The tests are executable check harnesses; run them with `dotnet run`, not only `dotnet test`.

```powershell
.\scripts\Check-Repository.ps1
dotnet run --project tests/Mosaic.Core.Checks/Mosaic.Core.Checks.csproj -c Release -r win-x64 -p:RestoreLockedMode=true
dotnet run --project tests/Mosaic.Storage.Checks/Mosaic.Storage.Checks.csproj -c Release -r win-x64 -p:RestoreLockedMode=true
dotnet run --project tests/Mosaic.Output.Smoke/Mosaic.Output.Smoke.csproj -c Release -r win-x64 -p:RestoreLockedMode=true
dotnet run --project tests/Mosaic.Imaging.Checks/Mosaic.Imaging.Checks.csproj -c Release -r win-x64 -p:RestoreLockedMode=true
dotnet run --project tests/Mosaic.App.Checks/Mosaic.App.Checks.csproj -c Release -r win-x64 -p:RestoreLockedMode=true
```

Run affected harnesses after a cohesive change. Imaging and app checks execute native AI and may depend on local graphics capabilities; output checks prepare PDF/XPS and inspect installed printer capabilities. Hosted CI runs repository, Core, Storage and Output checks plus an app build. Native inference in the output check may use the bundled CPU fallback. CI does not establish target-GPU, physical printing, hands-on UI or clean offline installation acceptance.

Full packaging and development-host installer checks are described in [DISTRIBUTION.md](docs/DISTRIBUTION.md). `Verify-Installer.ps1` performs a real per-user installation, shortcut/association checks and uninstall; run it deliberately on a host without a registered Mosaic installation.

## Pull requests and reports

Explain the triggering problem, resulting behavior and checks actually run. Keep original image bytes immutable, derive output from the shared layout contract, and preserve existing files on cancellation/failure. Synchronize English and Polish strings and check decimal-comma entry when dimensions change.

For print reports, include printer/submodel, driver, orientation, borderless/margins, output pathway and measured ruler/seam results. A screenshot or successful spool handoff cannot establish physical accuracy. Use artwork you have permission to share; remove personal paths and documents from attachments.

Original contributions are provided under the project's [MIT License](LICENSE). Third-party additions must retain compatible licenses and provenance records; imported artwork and model weights retain their respective terms.
