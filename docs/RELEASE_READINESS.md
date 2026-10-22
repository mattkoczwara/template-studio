# v1.0.0 release readiness

**Decision: Blocked — production release withheld.** Date: October 22, 2026.

The final engineering pass preserves the existing WPF product and shared physical layout. No new dependencies or departures from the [architecture recommendation](MOSAIC_TEMPLATE_STUDIO_ARCHITECTURE.md) were introduced. The [binding product definition](POSTER_STUDIO_PRODUCT_DEFINITION.md), especially §15 and §18, requires real target-device and artist acceptance before production completion. Software checks cannot satisfy those requirements.

## Corrections

| Defect | Correction and regression evidence |
|---|---|
| Calibration could select a different printer without marking a saved project changed. Closing could silently lose the selection. | Printer changes now participate in dirty state, undo/redo, recovery and portable project saves. EN/PL checks include reopening and margin-and-trim fallback. |
| Standard output could reuse or regenerate artwork after a valid replacement PNG altered the normalized cache. | Verify normalized dimensions and checksum before both cache reuse and new rotations, matching the source-integrity contract already used by enhancement. |
| Canceling import during shutdown could invalidate a native image before subsequent metadata access, reporting an editor error instead of cancellation. | Check cancellation before atomic-write metadata access and translate native abort/disposal exceptions to cancellation only when the token is canceled. The real import/shutdown regression passes. |
| Diagnostic exception messages and object names could contain private artwork paths. | Keep exception types, numeric results, vetted storage error codes and stack traces; omit arbitrary messages/object names. A synthetic private filename regression covers the exception chain. |
| Failed installer payload/runtime verification left the disposable installation registered. | Run the normal uninstaller in `finally`, preserve unknown personal files and retain the original failure evidence. |

Core geometry and storage review found no substantive defect requiring a change. README, screenshots, About description, topics, MIT licensing and dependency notices were inspected and retained. No obvious credential patterns or tracked generated/private-file candidates were found by the targeted repository scan; this is not a forensic security certification.

## Current verification

Evidence is retained locally under `artifacts/verification/`, `artifacts/installer-checks/`, `artifacts/v1-app-final/`, `artifacts/output-smoke/` and `artifacts/ai-smoke/`. Older counts elsewhere in the documentation describe their dated builds.

| Check actually run | Result |
|---|---|
| Core physical geometry/orientation/seams | 394,009 assertions across 1,500 deterministic randomized layouts passed. |
| Portable projects, malformed archives, recovery and source preservation | 41 checks passed. |
| PDF, XPS and direct-print paginator | 462 checks passed, including independent image extraction and 24/60-inch dimensions; no physical jobs submitted. |
| Imaging | 24 selection/inspection checks, genuine Vulkan/forced-CPU inference, cache corruption/budget/concurrency, cancellation and a 352-character Windows cache-path journey passed. |
| Large illustration | 1200 × 900 → 4800 × 3600 pixels; GPU 3.41 s, CPU 52.85 s; worker peaks about 235/121 MiB. GPU/CPU RMSE 0.00314; boundary RMSE 0.00284. The 60 × 45-inch PDF has 36 landscape Letter pages. |
| Integrated WPF editor | 463 checks passed with no unhandled dispatcher exceptions, including EN/PL dimensions, comparison, editable projects, recovery, shutdown and the new calibration/logging regressions. |
| Development desktop launcher | 39 checks passed: shortcut, spaces, fresh/incremental source builds, failed-build protection, running-instance guidance and data preservation. |

The first coordinated run correctly stopped at the native import-cancellation defect. After correction, all 101 focused lifecycle checks passed, then the full affected Imaging (including `--large`) and App suites passed. Geometry, Storage and Output had already passed and their implementations were unchanged. The self-contained candidate was then built from fresh staging with `Build-Release.ps1 -SkipTests`; the same source had passed the applicable suites during this run. Logs: `v1-release-build.log` (original failure), `v1-imaging-final.log`, `v1-app-final.log`, `v1-launcher-checks.log` and `v1-package-final.log` under `artifacts/verification/`.

Fresh English/Polish editor and compact completion renders were visually reviewed. Renders at 120/144 DPI do not establish actual Windows display scaling. The visible Print/Export actions and readable completion layout were retained without redesign.

## Candidate package

The final unsigned self-contained installer is **65,925,901 bytes**, SHA-256 `e62fa73037bdee3fe5a714b5a8de838af408d5bf0c9894df98cbfe45b015801e`. Its **459 payload files total 207,129,125 bytes**, including .NET 10.0.13, both native AI workers, selected models and license notices. The executable's product version is `1.0.0+7be5808151ef4f196cec80a30505d07a87015cf8`, identifying the exact committed application source. It was rebuilt after the QA commit solely to align that provenance (`artifacts/verification/v1-committed-package.log`). Build and pinned payload verification passed; the complete file manifest is `artifacts/verification/published-files.json` and the installer identity is `release-package.json` beside it. Installer: `artifacts/installer/MosaicTemplateStudio-1.0.0-win-x64-Setup.exe`, with adjacent `.sha256`.

**Both final English and Polish install/runtime/uninstall cycles passed on this development host.** Each matched all 459 file hashes, verified Desktop/Start shortcuts and the project association, then passed **463 integrated editor checks on bundled .NET 10.0.13**. Installation, installed runtime and normal uninstall all returned 0. Unknown personal-project sentinels survived; application registrations and shortcuts were removed. Exact final evidence: `artifacts/installer-checks/c9b82911b22f4304bf29814be67bb446/result.json` (English) and `artifacts/installer-checks/caa1ea4bb2e948f58d9a09beb3ee9676/result.json` (Polish), with `runtime-check/installed-runtime.json` in each directory. Both identify the final installer hash above. These are development-host checks with the network available, not clean offline-machine acceptance.

An intentionally incorrect payload checksum also failed as expected; the normal uninstaller removed the disposable app/registration and preserved its personal-file sentinel. Evidence: `artifacts/verification/v1-installer-failure-recovery.json` and `artifacts/installer-checks/15f870980a12432aae2caa72d92738a8/`. Final documentation/asset validation passed **134 local links**, pinned AI hashes and banner XML; whitespace and PowerShell syntax checks passed. The consolidated local record is `artifacts/verification/v1-final-summary.json`.

The current NuGet advisory query completed against the official feed on October 22, 2026 and reported no known vulnerable application packages, including transitive packages (`artifacts/verification/v1-dependency-audit.json`). Native worker/model payloads are separately hash-pinned and their redistribution notices are included.

## Required external acceptance

These are existing first-release requirements, not deferred feature development:

- Measure the Epson ET-8500 four-sheet fixture via direct print and PDF at Actual size / 100%, in both orientations. Record X/Y scale, clipping, seams and repeatability. Validate the margin-and-trim alternative if borderless expansion prevents butt joins.
- Verify Canon MF210 hard margins and physical trim assembly on the actual device.
- Install on separate clean Windows with networking disabled; import, run bundled AI, export, save/reopen after deleting the download, and uninstall while preserving personal data.
- Exercise representative 24-inch and 60-inch artwork on the artist's NVIDIA PC; inspect inferred detail with the actual artwork.
- Check real Windows 125%/150% display scaling, fluent Polish wording and the artist independently completing the intended workflow.

Native computer-use initialization failed twice with `trusted Node process exited unexpectedly; kernel reset, rerun your request`. Automated WPF interaction/rendering is recorded separately and does not establish OS-injected input, real display scaling or independent hands-on usability. No physical printer jobs were submitted during this pass.

Signing remains a documented distribution limitation: no signing certificate was supplied. An unsigned installer can be evaluated without inventing a signature or a reputation claim.

## Publication boundary

Integrate passing engineering changes through a pull request. Keep `v1.0.0` and the production GitHub Release unpublished until the required external acceptance has actual evidence. Release notes are prepared in [the changelog](../CHANGELOG.md). Do not label this candidate production accepted, or treat a Windows spool handoff as a physical measurement.

## Continuation and manual PR handoff

The application fixes are committed as `7be5808151ef4f196cec80a30505d07a87015cf8` on `codex/final-release-qa`, based on main `3d98db93516b8495e6c98a95cd5c787f1614bc7e`. Subsequent release-report changes do not alter application sources or packaged inputs. The branch is pushed to the existing public repository.

The GitHub connector rejected issue/PR writes with HTTP 403; the GitHub CLI was unauthenticated and browser automation could not initialize. The user explicitly chose a **manual PR handoff**. No PR, merge, tag or GitHub Release was created by this pass. Hosted CI for this change remains pending PR creation; its new Output check is included in the workflow. Existing main is preserved.

[Open the release QA pull-request comparison](https://github.com/mattkoczwara/template-studio/compare/main...codex/final-release-qa?expand=1). Suggested title: **Harden v1.0 candidate and record production acceptance gates**. Describe the five corrections above and link this report as validation evidence; state that production remains Blocked.

A paste-ready description is retained locally as `artifacts/verification/v1-pr-description.md`. All implementation, local verification, packaging and installer checks for this pass are complete. Remaining work is the manual PR/hosted-check workflow and the external acceptance gates; no uncommitted application changes or additional routine feature development are intended.

Next: create the PR and require hosted checks before integration; complete the external acceptance checklist above and record actual observations. Do not repeat passing local suites unless application changes or a new failure warrants it. After all required gates pass, build from the final release commit, verify the candidate, create the annotated `v1.0.0` tag and publish the installer/checksum with accurate notes; verify the remotely downloaded artifact. Current software evidence must not be substituted for physical or human acceptance.
