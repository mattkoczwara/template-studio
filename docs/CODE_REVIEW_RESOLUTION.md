# Code review resolution — 21 October 2026

Validated all six findings in [the independent report](CODE_REVIEW.md) against current source, the binding product definition and the implemented architecture. All six warranted correction. The original report remains unchanged as historical evidence.

| Finding | Validation and implemented resolution |
|---|---|
| F1 — Recovery loss | Confirmed unguarded Restore, deferred deletion and shared-slot ownership. Restore now uses the regular save/discard/cancel guard. Each editor owns a separate portable recovery with an exclusive lifetime lease; interrupted and legacy snapshots are claimed exclusively. Later preserves the claim/file. A restored prior file is removed only after a successful owned checkpoint or explicit close resolution. Empty/concurrent closes cannot delete another session's work. Product §9. |
| F2 — First-print calibration | Confirmed import/profile changes clear the saved queue before the old calibration lookup. Pass saved records to the output adapter and resolve only after the native dialog validates the actual queue, driver, profile, orientation and ticket. Exact matching records win; stale selected-printer settings remain errors. Product §§6/8. |
| F3 — Invalid state/stale layout | Confirmed proportional relock can exceed 120 inches. Shared candidate-layout validation now precedes geometry-changing commits; invalid changes preserve state/layout/history and show inline validation. Added the same protection for margins/orientation/rotation/profile, including sheet-limit combinations. Snapshot preparation rejects invalid or inconsistent state/layout pairs. Product §§5/8/12. |
| F4 — Canon ticket mismatch | Confirmed calibration previously requested color while Canon artwork requested monochrome. Both use one profile-aware ticket builder and the current explicit grayscale setting. Installed-driver checks also exposed copy count embedded in opaque IPP data; driver revalidation at one copy normalizes fingerprints while retaining media/expansion settings. Clipping, driver, fingerprint and printable-bounds checks remain enforced. Product §§6/8. |
| F5 — Missing scale guidance | Confirmed margin completion replaced the scale instruction. Export completion always includes Actual size / 100%, then margin trim/join guidance. Covered both languages, zero/positive margins and guide on/off. Product §§3/8. |
| F6 — Incomplete cache budget | Confirmed derived and AI writes bypassed import admission. Shared 4 GiB accounting now reserves conservative PNG/raw/AI staging headroom and serializes writers across processes. Verified reuse does not request allocation capacity. Explicit cleanup preserves pinned documents across windows, immutable active originals and portable projects. Cancellation removes staged writes and releases admissions. Small-budget tests reproduce pressure without filling the host disk. Product §§9/12. |

## Architecture decisions

Retained the existing WPF modular monolith, decimal layout engine, PDF/XPS adapters and isolated native AI workers. Added small file-lease ownership/accounting contracts rather than a single-instance forwarding service, MVVM rewrite, eviction manager or new dependency. Per-session recovery permits simultaneous windows; explicit cleanup plus conservative cache admission avoids deleting originals to make room. This extends the implemented design without a material technology departure from the architecture recommendation.

Conservative reservations can reject new processing before actual stored bytes reach 4 GiB. Verified cached assets remain reusable. Native pixel-cache/free-disk limits remain separate from persistent image-cache accounting. Zero-byte source-lock sidecars persist to preserve cross-process lock identity; recovery sidecars disappear when their exclusive handles close.

## Fresh validation

| Check | Result |
|---|---|
| Core harness | **394,009 passed**, including 1,500 randomized layouts. |
| Storage harness | **39 passed**, including per-session ownership, exclusive orphan claims, deferred/legacy recovery and competing closes. |
| Output harness with `--printers` | **238 passed**: PDF dimensions/translation, WPF/XPS, calibration selection/safety and real Epson OEM/IPP plus Canon ticket parity in both orientations, color policies and differing copies. No jobs submitted. |
| Imaging harness with `--large` | Passed formats, source/cache/model integrity, small-budget pressure/reuse, competing writers, active-source leases, cleanup, GPU/CPU inference and cancellation. Final 1,200 × 900 → 2,400 × 1,800 run: **1.54 s GPU / 11.58 s CPU**. Synthetic GPU/CPU RMSE **.00196**, boundary-strip RMSE **.00179**. Enhanced 60 × 45-inch export: **36 pages**, **1,006,347 bytes**; independent PDF inspection passed. |
| WPF editor harness | **68 passed**: dirty Restore Cancel/Discard, successful checkpoint before prior resolution, Later/empty close, other-window cleanup, invalid proportional changes in both directions, stale snapshot refusal, sheet-limit margin/orientation rejection, EN/PL decimal input/completion, portable reopen and existing AI/output journeys. |
| Windows Release build | **0 warnings, 0 errors**, `win-x64 --no-restore`. |
| Repository checks | Local documentation links, pinned AI hashes, banner XML and `git diff --check` passed. |

Commands use `.tools/dotnet/dotnet.exe`: `run --project tests/<harness> -c Release --no-restore`, adding `-- --large` for imaging, `-- <artifact-directory> --printers` for output, or `-- <artifact-directory>` for app checks. Run `build src/Mosaic.App/Mosaic.App.csproj -c Release -r win-x64 --no-restore` and `scripts/Check-Repository.ps1`. These are executable harnesses, not `dotnet test` projects. Repeated app/installed-host checks now use unique local-state directories to avoid consuming prior recovery evidence.

Fresh editor/PDF/XPS artifacts are under `artifacts/review-fixes/final-app-checks/` and `artifacts/review-fixes/final-output/`; imaging results are under `artifacts/ai-smoke/`. EN/PL margin-completion renders were inspected for legibility and complete scale/trim guidance. DPI-rendered editor images are software evidence, not actual Windows scaling or native file-picker interaction.

## Remaining acceptance work

No actionable F1–F6 software defect remains identified by these checks. No physical printer jobs, calibration measurements or clean offline installation were performed. Before release acceptance, measure Epson direct/PDF scale, crop, seams and feed repeatability; validate Canon and a measured margin-and-trim alternative. Complete clean offline install/use/uninstall, actual Windows display-scaling and native-dialog journeys, target-PC/maximum-source resource checks, representative artist artwork, fluent Polish review and independent artist use. The existing installer was not rebuilt by this remediation; rebuild and revalidate it before distribution. See [the acceptance map](ACCEPTANCE.md).
