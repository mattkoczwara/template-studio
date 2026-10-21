# Mosaic Template Studio — Independent Code & Quality Review

**Reviewed:** 21 October 2026. **Scope:** assessment only; no fixes implemented.

## 1. Executive summary

Mosaic has a sound foundation: one deterministic decimal layout, faithful source preservation, separate PDF/XPS adapters, isolated offline AI, defensive portable storage, and a coherent bilingual editor. All five existing check harnesses passed in this review. The final Windows app build produced zero warnings and errors.

**Do not declare first-release acceptance yet.** Two high-priority integration defects undermine recovery and calibrated first prints. Three medium-priority defects affect dimension-state consistency, Canon calibration, and actual-size guidance. A lower-priority cache-budget risk remains source-supported. No P0 issue was established.

Physical Epson/Canon output, clean offline installation, actual Windows display scaling, representative artist artwork, fluent Polish review, and independent artist usability remain unverified. These include mandatory acceptance gates in product §§15/18; passing software checks cannot close them.

### Baseline and method

- Initial checkout: `main`, HEAD **`0c005189e0d23d5c0df2af2c385a5a088195945d`**, exactly the [requested baseline](https://github.com/mattkoczwara/template-studio/commit/0c005189e0d23d5c0df2af2c385a5a088195945d). Initial working tree was clean; no implementation differences from that commit.
- Read `AGENTS.md`, the binding [product definition](POSTER_STUDIO_PRODUCT_DEFINITION.md), architecture recommendation, implementation/acceptance documentation, and the connected source/test paths. Independently reviewed geometry/output, imaging/AI, and storage/distribution in parallel; integrated findings against the editor's actual behavior.
- Mapped import → immutable image/cache → editor composition/physical layout → output snapshot → PDF/Windows print, plus project/recovery, localization, native workers, and installer boundaries.
- Used existing tests first, then isolated diagnostic probes for specific uncovered hypotheses. No physical print job, installation, implementation edit, or refactor was performed. Ignored evidence is retained under `artifacts/`; generated lockfile drift was restored. The only intended repository change is this report.

## 2. Prioritized findings

### P1 High

#### F1 — Recovery actions can silently discard current or previous work

**Confidence:** Confirmed by editor-path probes. **Effort:** Medium. **Requirements:** product §§9/12, recoverable edits and dirty-state protection.

**Evidence:** [MainWindow.xaml.cs:98](../src/Mosaic.App/MainWindow.xaml.cs#L98) restores directly through `OpenProjectAsync`, bypassing `AllowReplaceAsync` at lines 535–540. Import at lines 116–129 leaves the recovery notice active. A probe changed current width from 24 to 30 with `dirty=true`, invoked Restore, and observed width return to 24 without a save/discard dialog.

Two related loss paths share the same recovery lifecycle defect:

- “Do this later” only hides the notice ([lines 101–104](../src/Mosaic.App/MainWindow.xaml.cs#L101)); an empty normal close then unconditionally deletes the deferred recovery at [line 559](../src/Mosaic.App/MainWindow.xaml.cs#L559). The probe's pre-existing recovery file disappeared.
- All windows/processes use the same [PreferencesStore.cs:19](../src/Mosaic.Storage/PreferencesStore.cs#L19) `recovery.mosaic`. There is no application single-instance guard. Window A created recovery; closing an empty window B using the same data root deleted A's file. Subsequent autosaves can also overwrite the shared slot.

**Impact:** A recovery feature can discard unsaved editing choices without the intended save/discard decision, or remove the only portable recovery snapshot of a prior session. Original image bytes surviving in a cache do not preserve those choices.

**Recommendation:** Guard Restore with the normal dirty-document replacement decision. Track unresolved prior recovery separately from the current session; “Later” must preserve it. Delete only a recovery snapshot owned and explicitly resolved by the closing session. Use per-session ownership, or deliberately enforce one instance with file-open forwarding. Cover Restore over dirty work, deferred recovery plus empty close, and concurrent-window close/write sequences.

#### F2 — New artwork's first print silently skips saved calibration

**Confidence:** Confirmed source path and calibration-selection probe; no physical print. **Effort:** Medium. **Requirements:** product §§6/8, printer-specific correction and accurate output.

**Evidence:** Every import creates a fresh state with `PrinterName=null` ([MainWindow.xaml.cs:125](../src/Mosaic.App/MainWindow.xaml.cs#L125)); profile changes also clear it at line 322. Calibration selection at [line 422](../src/Mosaic.App/MainWindow.xaml.cs#L422) requires an exact match against that name **before** the printer dialog resolves a queue. The probe retained one enabled Epson record and observed `state.PrinterName=null`, selected calibration `null` after import.

[PrintService.cs:48–55](../src/Mosaic.Output/PrintService.cs#L48) subsequently resolves and validates the actual queue, but receives the already-null record. `ValidateCalibration` returns immediately for null at [line 252](../src/Mosaic.Output/PrintService.cs#L252). The chosen printer name is stored only after submission, at editor line 425.

**Impact:** The ordinary “open new artwork → print once” journey omits an enabled measured correction even when the selected Epson queue/settings match. Later copies may receive different geometry because the first job established the name. The resulting physical error depends on the printer's measured correction; no paper error was measured here.

**Recommendation:** Resolve calibration against the actual selected, validated queue/profile/orientation/ticket before creating the paginator. Preserve driver-fingerprint, clipping, and bounds checks. Test first print after import, profile changes, repeated prints, and selecting a different queue in the native dialog; do not merely weaken matching or default to an arbitrary profile record.

### P2 Medium

#### F3 — Re-enabling proportions can commit invalid size while retaining the old layout

**Confidence:** Confirmed by editor/output-snapshot probe. **Effort:** Small. **Requirements:** product §§5/8/12, valid dimensions and one consistent physical snapshot.

**Evidence:** [MainWindow.xaml.cs:279–287](../src/Mosaic.App/MainWindow.xaml.cs#L279) mutates `state` and calculates the complementary dimension without the range validation used by `CommitDimension`. `Changed()` then calls `RefreshLayout`, where [LayoutEngine.cs:9–10](../src/Mosaic.Core/LayoutEngine.cs#L9) throws for a dimension above 120 inches.

**Reproduce:** Import a 4:3 image; unlock proportions; leave width 24 and set height 120; enable Keep proportions. The probe observed `ArgumentOutOfRangeException`, state **160 × 120**, previous layout **24 × 120**, and output still enabled. `CommitPendingInputs()` returned true, and `SnapshotAsync()` returned those inconsistent state/layout sizes. [OutputGeometry.cs:15](../src/Mosaic.Output/OutputGeometry.cs#L15) draws using the stale layout's artwork rectangle. A sufficiently wide image reaches the same failure with a 60-inch authoritative height.

**Impact:** After the generic error, editing state, output description, and actual output geometry can disagree. The global exception handler's decision to continue does not roll back the mutation.

**Recommendation:** Build and validate the complete candidate state/layout before committing either; preserve the last valid pair and show inline size validation on failure. Add a snapshot consistency guard and regression checks for relocking extreme aspect ratios in both dimension directions.

#### F4 — Default Canon calibration and artwork tickets have incompatible fingerprints

**Confidence:** Confirmed using installed Canon driver ticket validation; no print submitted. **Effort:** Small. **Requirements:** product §§6/8, usable Canon fallback and pathway-specific calibration.

**Evidence:** Normal Canon output requests monochrome ([PrintService.cs:46,49–50](../src/Mosaic.Output/PrintService.cs#L46)); calibration hardcodes the Epson fallback profile and requests color at [lines 77–78](../src/Mosaic.Output/PrintService.cs#L77). `CreateDialog` sets `OutputColor` at line 159. On the installed **Canon MF210 Series** driver, otherwise identical Letter/portrait/simplex/100%/trim requests validated as:

| Request | Validated output | Fingerprint prefix |
|---|---|---|
| Calibration: Color | Grayscale | `743cab54e1d0` |
| Artwork: Monochrome | Monochrome | `da376b509800` |

[SettingsFingerprint:237–247](../src/Mosaic.Output/PrintService.cs#L237) retains the color difference; [ValidateCalibration:253–258](../src/Mosaic.Output/PrintService.cs#L253) rejects the resulting record when used for artwork.

**Impact:** With default settings, an enabled Canon correction cannot be reused for normal Canon artwork; the user must manually reconcile settings or disable it. F2 can mask this until the printer name is known.

**Recommendation:** Give calibration the same effective profile/color policy and ticket construction as the artwork job. Verify matching *validated* ticket fingerprints for the same settings. Retain strict mismatch protection.

#### F5 — Margin-mode export drops the actual-size printing instruction

**Confidence:** Confirmed in fresh completion text and WPF render. **Effort:** Small. **Requirements:** product §§3/8, actual-size guidance at the output handoff.

**Evidence:** [MainWindow.xaml.cs:438](../src/Mosaic.App/MainWindow.xaml.cs#L438) selects `TrimHint` **instead of** `ActualSize` whenever margins are positive. Exporting a real 24 × 18-inch PDF with .25-inch margins produced completion text saying to trim/join and that PDF color is preserved, with no “Actual size / 100%” instruction. The separate assembly guide is optional and off by default.

**Impact:** The margin-aware fallback removes the scale safeguard precisely where the user is likely to encounter a PDF viewer's fit-to-printable-area option. Further scaling would defeat the exact template dimensions; that downstream physical outcome remains unmeasured.

**Recommendation:** Always display Actual size / 100%, then append margin-specific assembly instructions. Check completion in EN/PL with zero and positive margins, with/without the guide.

### P3 Low

#### F6 — The advertised persistent-cache cap does not cover derived writes

**Confidence:** Strongly supported resource risk; harmful disk exhaustion was not reproduced. **Effort:** Medium. **Requirements:** product §12, bounded temporary storage and predictable large-project behavior.

**Evidence:** The 4 GiB check is an import admission check in [ImageService.cs:214–220](../src/Mosaic.Imaging/ImageService.cs#L214), called before cache reuse at line 53. Standard/rotated assets at [lines 114–137](../src/Mosaic.Imaging/ImageService.cs#L114) do not reserve against that budget. Enhancement at [EnhancementService.cs:56–87](../src/Mosaic.Imaging/EnhancementService.cs#L56) checks drive space but not aggregate cache size. `CleanDerivedCache` has no callers. Existing verified images can also be refused when a new import reserve no longer fits, despite reusable assets being present.

**Impact:** Multiple large derived variants can exceed the documented cap, then block further imports/reopens until explicit cleanup. Input limits and free-space checks reduce the risk; this is not evidence of arbitrary or unlimited allocation.

**Recommendation:** Account for/reserve derived writes under one cache budget, distinguish verified reuse from new allocations, and reclaim disposable variants safely while preserving originals and active work. Verify near-cap reuse, rotation/enhancement, cancellation, and cleanup with a small configurable test budget.

## 3. Visual and UX assessment

Fresh renders of the actual WPF editor show a convincing light creative tool: restrained green accents, coherent typography, generous canvas, obvious Open image arrival, legible size controls, subtle page labels, and persistent Print/Export actions. The Polish size labels wrap at the compact editor size, the settings rail scrolls, and the output actions remain available. Completion offers useful next actions without exaggerated celebration. No framework replacement is justified by the observed appearance.

The highest-value improvements are behavioral: trustworthy recovery (F1), consistent dimensions (F3), and reliable printing guidance/calibration (F2/F4/F5). For further refinement, review the 12–13 px secondary warnings/help with the older target user and make the calibration state easier to notice near output. These are usability priorities, not independently proven accessibility failures. The comparison view is limited to a fixed central detail region; whether it is sufficient for actual illustrations needs the artist's review.

Inspected fresh views include arrival, English editor, compact Polish editor, enhanced comparison, both completion languages, and margin-export completion. Files are under `artifacts/code-review/app-checks/` and `artifacts/code-review/app-probe/results/`. These are application renders, **not native screen captures or proof of actual Windows 125/150% display-scaling behavior**.

## 4. Architecture and maintainability assessment

The modular monolith is appropriate. `Mosaic.Core` owns orientation, composition and exact physical coordinates; preview, PDF and print consume those rules. Reused, clipped lossless PDF imagery avoids page-by-page recompression and a poster-sized raster. Original bytes remain immutable; derived AI runs on the coherent image before paper slicing. Format/pixel/resource limits, native hash pins, direct `ArgumentList` execution, cancellation, bounded ZIP extraction and atomic destination replacement are material safeguards. No duplicate layout engine or unnecessary service framework was found.

Departures from the recommendation are largely justified and documented in [IMPLEMENTATION.md](IMPLEMENTATION.md): decimal inches match the authoritative input unit; bespoke WPF controls, dictionaries and a small logger avoid toolkit/hosting dependencies; the isolated CPU adapter uses the same model where the upstream CPU path was reported unusable. Fresh CPU/GPU inference supports retaining that adapter. Model superiority on the artist's artwork remains unproven; synthetic success does not select a universally faithful model.

The main maintainability weakness is transaction coordination in the editor: mutable state, history, notices, recovery and output preparation are coupled in the same code-behind. The findings justify small shared candidate-state validation and explicit recovery ownership, plus shared print-ticket construction. They do **not** justify a wholesale MVVM conversion, new language/backend, or geometry rewrite. Lazy project metadata exists, but editor reopen waits for source import; measure large-source reopen before adding more preview infrastructure. F6 is the remaining concrete cache-accounting weakness.

## 5. Validation summary

All results below were executed during this review, unless explicitly described as an existing-artifact check.

| Check | Result and evidential boundary |
|---|---|
| Core executable checks | **394,009 passed**, including 1,500 deterministic randomized layouts: orientation/ties, margins, fractions, composition, shared seams/coverage and localized numbers. |
| Storage executable checks | **27 passed**: embedded original after download deletion, state/integrity, malformed archives, cancellation/atomic preservation, preferences and cleanup. |
| Output smoke | **202 passed**: independent PdfPig page boxes/image transforms, XObject reuse, PDF cancellation, WPF rasterization, XPS serialization, grayscale, Polish guide/calibration and orientation parity. No paper printed. |
| Imaging checks with `--large` | Passed all four formats, EXIF/ICC/alpha, source/cache/model integrity, GPU/CPU inference and cancellation. 1,200 × 900 → 2,400 × 1,800: **1.24 s GPU / 11.34 s CPU**, sampled worker host peaks **169.4 / 117.5 MB** (decimal MB). GPU/CPU RMSE **.00196**, boundary-strip RMSE **.00179**. These compare implementations on synthetic art, not fidelity to missing original detail. |
| Enhanced 60 × 45-inch PDF | **36 landscape Letter pages**, **1,006,347 bytes**, **.52 s**; independent page-box inspection passed. Physical dimensions exercise a 1.08 MP source, not maximum input size. |
| WPF app acceptance | **45 passed** using isolated data: import, exact dimensions, undo/redo, EN/PL decimal comma, composition, 60-inch export, portable reopen, AI/comparison/reversion/cancellation, recovery and completion. Fresh EN/PL renders generated. |
| Independent editor/driver probes | Confirmed F1–F5. Recovery probes used only isolated review data. Canon reads validated tickets without submission. A pending-edit-close hypothesis did not reproduce silent loss and was rejected. |
| Additional imaging probes | Unprofiled CMYK conversion preserved the expected sample color. Canceling a supported 4,000 × 4,000 source / 8,000 × 8,000 comparison stopped in approximately **943 ms after cancellation**, without publishing output; insufficient evidence for a material cancellation finding. |
| App compiler/static check | Release `win-x64 --no-restore` build: **0 warnings, 0 errors**. No separate analyzer suite was configured/run. |
| Repository/package integrity | **66** local links, pinned AI hashes and banner XML passed. Existing published payload: **456/456** hashes matched its manifest. Existing installer: **63,337,792 bytes**, SHA-256 `d0032f09ed2ad86b08f9a6c0bdffe2e3cb561948e3f2d14a05b93efb55a5608a`, **NotSigned**. No fresh installation or rebuild of that installer. |
| Dependency advisory query | Transitive application NuGet audit against nuget.org reported **no vulnerable packages**. This does not audit all bundled native/runtime components or establish absence of vulnerabilities. |

Reproduction commands use `.tools/dotnet/dotnet.exe` (SDK **10.0.402**): `run --project tests/<harness>/<harness>.csproj -c Release`, for `Mosaic.Core.Checks`, `Mosaic.Storage.Checks`, `Mosaic.Output.Smoke`, `Mosaic.Imaging.Checks` (append `-- --large`) and `Mosaic.App.Checks` (append `-- artifacts/code-review/app-checks`). These are executable harnesses, not `dotnet test` suites. Build: `build src/Mosaic.App/Mosaic.App.csproj -c Release -r win-x64 --no-restore`. Audit: `list src/Mosaic.App/Mosaic.App.csproj package --vulnerable --include-transitive --no-restore`. Repository: `scripts/Check-Repository.ps1`.

Additional probe source and results are retained in ignored `artifacts/code-review/app-probe/` and `artifacts/code-review/imaging/`; the finding descriptions above provide the triggers without requiring those artifacts. Existing backend checks also wrote their standard ignored `artifacts/ai-smoke/`, `output-smoke/` and `storage-checks/` outputs.

**Environment/coverage limits:** The ordinary shell and computer-use JavaScript kernel failed before execution with `helper_unknown_error: setup refresh had errors`; shell checks succeeded through reviewed out-of-sandbox execution. The computer-use kernel still failed after reset, so independent native clicks, file-picker journeys and screen captures could not be completed. Actual WPF windows ran through the dispatcher harness. No Android functionality is present; the OnePlus device was not relevant.

No Epson/Canon paper measurements, driver expansion/crop validation, seam/feed repeatability, clean network-disabled installation, target-PC GPU/VRAM evaluation, maximum 80 MP decode/16 MP inference throughput, screen-reader review, actual display-scaling matrix, fluent Polish sign-off, or independent artist task completion was established. Successful spool preparation and synthetic 60-inch export are not substitutes. Disk-full behavior was assessed through source/existing failure checks rather than filling the host disk.

## 6. Ordered remediation plan

1. **Protect state and recovery before further release work — F1/F3.** Establish candidate-state/layout validation and recovery ownership. Add the exact regression journeys above, including retained prior recovery and competing sessions. Keep the layout core unchanged. These touch the same editor and should have one integration owner.
2. **Unify calibrated print selection — F2/F4.** In parallel with step 1, an output owner can implement/test common profile-aware ticket construction and calibration resolution using validated queue settings. Integrate the editor call-site after step 1; test first/repeated print, another selected queue, monochrome/grayscale, copies, margins and mismatch rejection. Do not remove fingerprint or printable-bounds safeguards.
3. **Repair the output handoff — F5.** Bundle EN/PL actual-size and trim guidance checks with editor/output integration. Verify generated PDFs still have nominal geometry and that completion describes the exported snapshot.
4. **Bound cache growth — F6.** Imaging/storage work can proceed independently once active-session ownership is defined. Test a small budget rather than generating gigabytes unnecessarily. Preserve immutable sources, portable projects and unresolved recovery.
5. **Run focused regressions, then one coordinated integration pass.** Re-run affected harnesses and the new lifecycle/selection cases; inspect EN/PL arrival, invalid input, crop, busy/cancel, recovery and completion at real Windows scaling. Include representative high-resolution input, not only large physical dimensions with small fixtures.
6. **Close the external release gates.** On the artist's Windows/NVIDIA PC, measure the four-sheet Epson fixture through direct and 100%-scale PDF printing in both orientations, including crop/seams/repeatability. Validate a measured margin-and-trim alternative and actual Canon output. Perform a clean offline install/use/uninstall journey; review representative illustration enhancement, fluent Polish and independent artist completion, including a 60-inch export/preparation. Record observed limits and signing status before declaring release acceptance.
