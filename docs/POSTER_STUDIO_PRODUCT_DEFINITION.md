# Mosaic Template Studio — Product Requirements & Architecture Planning Brief

**Status:** Requirements baseline for architecture planning (not an architecture decision)  
**Version:** 1.1 — refined layout defaults and user-experience requirements  
**Date:** 20 October 2026  
**Target:** Standalone Windows desktop application, personal/family use  
**Primary user:** A nontechnical mosaic artist working independently  
**Primary printer:** Epson EcoTank ET-8500  
**Secondary printer:** Canon imageCLASS MF210 series (specific submodel not established)  
**Reference inspiration:** [Rasterbator](https://rasterbator.net/), but not a feature-parity objective

> **Product promise:** Open an image downloaded from the Internet, specify exactly how large the mosaic template should be in inches, and print or save a correctly tiled, full-color paper template. The application handles pages, quality, and assembly without requiring graphic-design knowledge.

## 1. Mission and product boundaries

The product exists primarily to help the user's mother turn downloaded illustrations and artwork into physical **1:1-scale paper reference templates** for handmade mosaics. The application is not principally a halftone artist, a poster-effects generator, a mosaic-piece planner, or a general graphics editor. It enlarges the **original full-color artwork**, arranges it into printer-ready Letter sheets, and makes the resulting assembled template physically accurate and straightforward to construct.

Typical artwork is around **24 inches** in its longest dimension; occasional projects reach **60 inches**. The primary user should be able to run the app, open a downloaded image, set the size, and obtain either a multi-page PDF or a direct print job without another person's help.

### Non-negotiable first-release outcomes

1. **Precise size:** User-entered dimensions in inches govern the final image rectangle, not the number of sheets. Output PDF geometry must be exact; physical output requires calibration where the printer adds scaling.
2. **Original image fidelity:** Preserve color, composition, and original aspect ratio by default; no obligatory stylization, segmentation, or other interpretation.
3. **Easy paper assembly:** US Letter, borderless, zero image overlap, and adjacent sheets meeting edge-to-edge are the primary Epson workflow. Outside excess paper can be trimmed.
4. **Simple two-action finish:** **Print** and **Export PDF** are both prominently available.
5. **Two upscale paths:** Conventional high-quality interpolation and *optional* local AI super-resolution included in the first release, with the AI model packaged in the installer.
6. **Local, offline, private:** No account, external processing service, first-use downloads, or Internet requirement for any core feature.
7. **English and Polish:** Fully localized interface selectable in Settings.
8. **Reopenable editable projects:** Retain embedded original image, dimensions, image-processing settings, and print configuration.
9. **Installer suitable for a family member:** A dependable Windows installation that supports launching from a desktop/Start menu shortcut, uninstalling, and clean failure recovery.
10. **Smart, transparent page orientation:** Automatically select portrait or landscape sheet orientation according to the fewest required sheets; choose portrait if tied, and always allow manual override.
11. **Calm, beautiful, confidence-building interaction:** A refined, light and airy desktop experience with readable text, clear progression, gentle contextual guidance, and restrained positive feedback. The user remains fully in control.

### Explicitly out of scope for the initial release

Mosaic-piece detection; vector tracing; shape labeling; color-by-number; generating grout or cut paths; automatic image sourcing from websites; cloud storage; accounts; paid services; subscriptions; batch processing; mobile/web versions; artistic halftone/raster effects; Photoshop-class editing; collaborative project sharing. These can be revisited, but none should distract from the core workflow.

## 2. Resolved user requirements

| ID | Area | Confirmed requirement |
|---|---|---|
| R-01 | Primary user | Mother operates it independently, not through the developer |
| R-02 | Source | Internet images normally **downloaded first**; local file picker is primary |
| R-03 | Source artwork | Mainly **illustrations and artwork**, in their original full color |
| R-04 | Image geometry | Preserve original aspect ratio normally; show optional simple fit/crop/stretch choices when relevant |
| R-05 | Size input | Support both **one locked dimension with proportional calculation** and **explicit width + height** |
| R-06 | Size precision | Final artwork rectangle is the **requested exact physical size**; trim excess outer paper as necessary |
| R-07 | Normal scale | Often around 24 inches; support at least 60-inch projects |
| R-08 | Paper | US Letter **8.5 × 11 inches** by default; other paper sizes optional |
| R-09 | Joining | Preferred **zero-overlap, butt-joined page edges**, not duplicated strips |
| R-10 | Margins | Borderless/zero margins by default on Epson; optional user margins |
| R-11 | Printing | Both **direct print** and **multi-page PDF export**, prominent and equivalent |
| R-12 | Quality | High-quality conventional resizing; optional local AI-enhanced super-resolution in v1 |
| R-13 | Compute | Mother's Windows computer has a discrete **NVIDIA GPU** |
| R-14 | Installation | AI model and processing dependencies bundled; fully functional offline immediately |
| R-15 | Languages | Selectable **English / Polish** with persisted preference |
| R-16 | Persistence | Save/open self-contained, editable projects including image, dimensions, processing, printer settings |
| R-17 | Primary device | Epson **EcoTank ET-8500** |
| R-18 | Secondary device | Canon **MF210 series**, occasional use; monochrome, non-borderless fallback workflow |
| R-19 | Orientation | Choose the **paper** orientation requiring the fewest printed sheets by default; **portrait wins ties**; allow manual portrait/landscape override |
| R-20 | Visual design | Bright, airy, visually refined, welcoming UI with generous spacing, legible text and coherent layout |
| R-21 | Interaction design | Progress is evident but never coercive: subtle next-step guidance, freely editable prior steps, no mandatory wizard |
| R-22 | Feedback | Small satisfying responses to completed steps and a particularly polished export/print completion state; no intrusive animation or gamification |

The requirements above take precedence over older generic Poster Studio proposals (sheet-count-first sizing, artistic raster presets, English-only UI, deferred AI enhancement, external-image references, and PDF-only printing).

## 3. Primary use case and desired interaction

### Standard happy path — fewer decisions, no tutorial

1. Launch the application. A recent-project area may appear, but **Open Image** is the most obvious action.
2. Open an image from **Downloads** using the native Windows picker or drag and drop the file. Preserve the original data; show a large, faithful preview.
3. Select physical artwork width or height in **inches**. The other dimension is calculated automatically with the aspect ratio locked. Optionally unlock the dimensions to specify both.
4. View the art preview with an unobtrusive Letter page grid, the final physical size, and the **calculated number of sheets**. The application automatically chooses the **fewest-sheet paper orientation**, preferring portrait if counts tie; a simple orientation control allows immediate override.
5. Leave **Original / Standard** image quality selected or switch to **Enhance image**. When enhanced, show a clear original/enhanced comparison at meaningful zoom.
6. Click **Print** (Epson ET-8500 selected as default) or **Export PDF**. Show one concise summary: artwork dimensions, paper, automatically chosen or overridden orientation, page count, borderless/margin mode, and actual-size printing instruction. Present an understated, useful completion view after successful export or verified print-job handoff—not a premature claim that paper has emerged from the printer.
7. Print all pages at the specified sheet size and orientation; arrange pages using clearly indicated order; join sheets edge-to-edge. Trim only unused outer paper where necessary.
8. Save a project for easy reprinting or later changes; auto-recovery reduces the harm of closing accidentally.

### Additional journeys

- **Exact width and height:** Select custom dimensions that conflict with the source ratio. The application presents three understandable policies: *Fit entire image* (preserves image, introduces blank regions), *Crop to fill* (preserves shape, removes some source content), and *Stretch* (distorts shape). The app never silently chooses crop or stretch.
- **Low-quality input:** Import a small illustration; inspect a realistic enlarged-detail preview; choose Standard versus AI Enhanced; revert freely.
- **Secondary Canon:** Change printer profile; the app explains that printing has nonprintable margins and displays the extra trim-and-join operations required for an accurately sized template. Color artwork may be printed grayscale on Canon, but the project and PDFs remain full-color unless grayscale is explicitly requested.
- **Repeat project:** Open a saved project even if its original downloaded image was moved/deleted; change dimensions, reprocess, and print again.
- **Polish UI:** Switch once in Settings and then complete the whole standard workflow without untranslated buttons, error messages, or help instructions.

## 4. Interface specification

### Proposed single-window editor

**Design intent:** Luminous, calm, and precise—closer to a thoughtfully crafted creative tool than a dense printer utility. The image is visually prominent; the controls have strong hierarchy but never dominate it. A subtle progression from Image → Size → Review → Print/Export is visible without forcing navigation through wizard pages.

```
┌───────────────────────────────────────────────────────────────────────┐
│ Mosaic Template Studio            Open Image  Open Project  Settings  │
├───────────────────────────────────────────────┬───────────────────────┤
│                                               │ 1  IMAGE              │
│           BIG LIVE ARTWORK PREVIEW            │    Open / Replace     │
│                                               │                       │
│       ┌───────┬───────┬───────┐               │ 2  FINAL SIZE         │
│       │  1,1  │  1,2  │  1,3  │               │    Width   [24.00] in │
│       ├───────┼───────┼───────┤               │    Height  [auto ] in │
│       │  2,1  │  2,2  │  2,3  │               │    🔒 Keep proportions │
│       └───────┴───────┴───────┘               │                       │
│                                               │ 3  IMAGE QUALITY      │
│    [Entire art] [Zoom details] [Page grid]    │    Standard | Enhance │
│                                               │                       │
│                                               │ Paper: Letter · Auto  │
│                                               │ Portrait · 6 sheets   │
│                                               │ ▸ Paper / advanced    │
├───────────────────────────────────────────────┴───────────────────────┤
│  Final image: 24.00 × 18.00 in · 6 pages · Epson ET-8500             │
│                                         [Print]    [Export PDF]       │
└───────────────────────────────────────────────────────────────────────┘
```

*The example numbers are illustrative; page count must be computed from actual image, orientation, and settings.*

### Product usability rules

- Main flow has no mandatory wizard, terminology such as DPI/bleed, or settings-heavy dialogs.
- Default orientation is automatic and chooses the fewest sheets, with portrait used on a tie; show the result in simple language, e.g., “Portrait · 6 sheets (best fit),” and keep manual portrait/landscape override accessible. When the user overrides, do not silently reset it unless they explicitly return to Auto.
- The workflow uses **soft progression** rather than a forced multi-page wizard: clear short sections, subtle completed-state indicators, and contextual prompts that suggest—not demand—the next action. Previously entered choices remain visible and immediately editable.
- A light neutral canvas, comfortable whitespace, careful alignment, restrained accent color, high-quality typography, and gently differentiated surfaces establish a warm, editorial, uncluttered appearance. Decorative treatment never competes with the artwork preview.
- Friendly microcopy is brief, specific, encouraging, and respectful: “Image ready,” “Size set,” or “Ready to print,” not exaggerated praise or infantilizing coaching.
- Provide meaningful feedback after import, sizing, successful enhancement, and export; reserve the richest—but still subtle—moment for successfully generating the PDF or submitting the print job. Show useful next actions such as “Open PDF,” “Show in folder,” or “Print another copy.”
- Motion should communicate state changes (short, unobtrusive transitions), support reduced-motion preferences, and never delay editing, printing, or dismissal.
- Typography prioritizes readability for an older nontechnical user: adequately sized default body text, excellent contrast, plainly labeled inputs, large hit targets, and generous line spacing. Do not rely on tiny helper text or color alone for progress/status.
- Number fields accept decimal inches, show units, enforce positive numbers, and visibly indicate whether proportions are locked. Preserve the last *user-edited* dimension when the lock is enabled.
- **Print** and **Export PDF** always remain findable. A responsive page count and physical artwork size are visible near them.
- Printer settings are preselected from a named profile; details are in a collapsible area, not buried in Windows driver settings alone.
- Large readable controls, keyboard navigation, high-DPI display support, clear hover/focus states, undo of user choices where feasible, and plain-language errors. Usable without specialist vocabulary.
- Show a first-run **two-minute calibration** prompt for the ET-8500, with “Do this later” available; warn if physical accuracy has not yet been validated.
- Allow showing/hiding the page grid; it is a preview aid, not visible through the artwork on printed pages.
- Print guides/page numbering should never obscure or consume image area in borderless zero-overlap mode; show ordering in the interface and optionally on a *separate assembly guide* page.
- Input artwork always remains available to compare and recover; enhancement never destructively overwrites it.

### Gentle progression and emotional design acceptance criteria

The interface must actively reduce uncertainty without reducing autonomy:

1. **Arrival:** One clear call to action—**Open an image**—with recent projects in a quieter secondary region. No mandatory onboarding, marketing carousel, or account prompt.
2. **Image loaded:** Display the imported artwork prominently. A small, transient confirmation makes success apparent; focus moves naturally to physical dimensions without stealing keyboard control.
3. **Size entered:** Update physical dimensions, selected orientation, sheet count, and page grid immediately. A short, contextual explanation (“Fits on 6 sheets”) is more useful than a congratulatory badge.
4. **Ready to produce:** Keep **Print** and **Export PDF** persistently discoverable; show a compact readiness summary and invite reviewing rather than requiring acknowledgement of a completed step.
5. **Finished:** On successful PDF creation, show a modest, visually rewarding confirmation with filename, size and page count, plus **Open PDF**, **Show in folder**, and **Back to editing**. For direct printing, say **Sent to printer** only after the operating system has accepted the job, and never assert physical printing succeeded without confirmation. Make the success state dismissible without friction.

Design review should assess both **task completion** and **how the interaction feels**: Does it read clearly at normal Windows scaling? Does it feel calm, capable, beautiful, and reassuring? Can she change her mind at any stage? Can she operate the normal flow without help? Avoid heavy stepper chrome, modal confirmations for ordinary decisions, celebratory confetti, bouncing calls to action, hidden controls, or overwhelming settings panels.

## 5. Physical sizing, image aspect ratio and composition

### Canonical model

Store artwork width and height as exact physical lengths (e.g., millimeters internally as decimal/rational values or another non-lossy unit convention) independent of pixel dimensions. Inches are a display/input unit. Store paper dimensions, orientation, outer trim rectangles, and page transforms separately from the artwork dimensions.

**Proportional mode (default)**
- User enters width `W`; computed height `H = W × (source_height / source_width)` after applied source crop/rotation.
- Or user enters height `H`; computed width `W = H × (source_width / source_height)`.
- The UI never changes a user-entered dimension because the sheet grid happens not to divide evenly.

**Explicit width and height**
- If ratio matches, fill with preserved ratio.
- If it does not, surface three policies rather than silently distorting the image:
  1. **Fit:** preserve entire source at correct proportions, with blank bands inside the exact output rectangle.
  2. **Crop:** fill exact output rectangle without distortion; interactive crop location.
  3. **Stretch:** fill both dimensions via nonuniform scaling; opt-in only, with a clear warning.
- Default to **Fit** if both dimensions are forced and conflict; explain that the *artwork rectangle* is exact but the visible image occupies only part of it. This distinction must appear in the print summary.

### Export geometry invariants

- PDF page MediaBox matches the selected physical sheet dimensions: default 8.5 × 11 inches = 612 × 792 PDF points.
- Position every tile using a shared **artwork-space-to-paper-space** transform; no independent rounding per page. Adjacent edge coordinates describe the same point of the source image and must not skip or duplicate content.
- Default overlap = **0 inches**, default user margin = **0 inches** on ET-8500 borderless profile.
- For a rectangle not divisible by the full page dimensions, use enough pages to cover it. Leave unused *outermost* paper blank for physical trimming; do not rescale the image or truncate the requested area.
- **Required deterministic automatic orientation rule:** For the selected paper size, printer/paper profile, margins, and fixed physical artwork dimensions, calculate sheet count for **both portrait and landscape** using the same layout/usable-area logic. Select the orientation with the **smaller total number of sheets**; when equal, **select portrait**. Apply one chosen paper orientation consistently to all pages, and never rotate or distort the artwork itself to save sheets.
- For a uniform borderless, zero-margin grid, the reference formula is `pages = ceil(artwork_width / sheet_width) × ceil(artwork_height / sheet_height)`; evaluate twice, swapping sheet width and height. For margin-aware/trim mode, use the actual effective tile coverage and assembly rules—not raw physical paper dimensions—and still compare both candidates on identical constraints.
- Recalculate the automatic choice when physical dimensions, paper size, printer profile, printable area or margins change, and reflect the result immediately in preview, summary, export and direct print. Respect explicit manual portrait/landscape override until the user switches back to **Auto (fewest sheets)**; never surprise the user by overriding their selection.
- Display both orientation outcomes on demand (e.g., **Portrait: 6 sheets · Landscape: 8 sheets**) to preserve transparency; default UI can show only “Auto · Portrait · 6 sheets” for simplicity. Document the chosen or user-selected orientation in saved projects and print job snapshots, and include it in the final summary.
- If two layouts have identical sheet counts but different wastage, **portrait still wins** in Auto mode as explicitly required. Minimization is by whole-sheet count, not ink usage, trim length, waste area, print cost, or convenience.
- All page rows/columns use one explicit, documented enumeration order and a separate assembly diagram. Crops, margins and user-chosen orientation do not change enumeration unpredictably.
- Avoid PDF viewer “Fit to printable area,” “Shrink oversized pages,” or “Fit” options in user guidance; PDF printout should use **Actual size / 100%** unless a **validated** printer-specific calibration procedure specifies otherwise.

### Important precision distinction

An exact mathematical PDF is **not** proof of an exact physically assembled template. Real printers add feed errors, geometric distortion, overspray/expansion, paper variability, and driver-scale effects. The application can ensure correct source geometry and measure/calibrate output; it cannot promise perfect butt-joins under every paper/driver/environment combination. This is a product risk and validation requirement, not an invitation to silently alter the artwork.

## 6. Printing and assembly profiles

### Primary: Epson EcoTank ET-8500 — color / borderless

**Default profile:** US Letter, one-sided, compatible plain paper, borderless, 0 artwork overlap, 0 desired margins, original color, exact-size output; physical test and adjustment recommended before first important project.

The manufacturer's documentation confirms **Letter and plain-paper borderless** support. Epson also states that borderless modes can **expand the image and crop edges**, and exposes different expansion degrees in applicable print settings. The Windows integration must be verified on the actual installed ET-8500 driver; some named controls documented for printer-panel camera/media workflows may differ in the Windows driver.

**Calibration workflow (required)**
1. Create a printer-and-paper-specific reference sheet with large physical rulers, known-distance marks, an X/Y measurement grid, and edge-registration cues.
2. Direct user to print from the **same pathway and driver options** they will use for mosaics; physical calibration is invalid if the user subsequently changes print app, borderless option, expansion, paper type, or page fit settings.
3. Guide user to measure actual dimensions with a ruler; provide a small form for measured X and Y lengths plus observed clipping/edge behavior.
4. Store printer profile, print pathway, paper/media setting, calibration factors, and test date.
5. Where compensations are mathematically and mechanically reliable, apply controlled X/Y scale and offset adjustments in the appropriate printing pathway **without changing the nominal artwork size or original PDF**. Clearly label any *printer-calibrated PDF* as a separate output variant if needed.
6. If seam loss/edge crop cannot be calibrated out, explicitly say so and offer **precision trim mode** (print within known printable area with registration/trim lines, then cut sheet edges). Do not claim zero-trim borderless alignment is mathematically guaranteed.
7. Offer re-test after driver updates, paper/media changes, or a new printer.

**Driver boundary:** The app cannot reliably force every borderless driver setting programmatically. If driver dialogs must be used, provide concise printer-specific instructions and verify actual printed output. For direct printing and PDF viewer workflows, calibration settings may require separate records.

### Secondary: Canon MF210 series — monochrome / margins

- Model-level capabilities are limited to the series; the exact submodel is unspecified. Treat as a **monochrome, non-borderless** printer unless detection of its specific capabilities proves otherwise.
- Use an **available printable area** rather than assume 8.5 × 11 inches of printable image.
- Provide print-on-margin-aware-pages with cut/registration instructions or trim guides, and explain that it **cannot provide the same no-trim edge-to-edge join** as Epson borderless.
- Keep physical artwork size exact after trims; do not shrink-to-fit the whole multi-page image.
- Epson remains the preferred color mosaic-template printer; Canon is a convenience fallback.

**Official sources:**
- [Epson ET-8500 supported borderless paper types and sizes](https://files.support.epson.com/docid/cpd5/cpd59879/source/printers/source/paper_loading/reference/et8500_8550_l8160_l8180/paper_borderless_types_us_can_et8500.html)
- [Epson ET-8500 borderless expansion/cropping settings](https://files.support.epson.com/docid/cpd5/cpd59879/source/printers/source/printing_device/reference/et8500_8550_l8160_l8180/device_camera_print_options_et8500_l8180.html)
- [Epson ET-8500 printable-area specifications](https://files.support.epson.com/docid/cpd5/cpd59881/source/specifications/references/et8500_8550_l8160_l8180/spex_printable_area_et8500_8550.html)
- [Canon MF210-family printing without margins limitations](https://oip.manual.canon/USRMA-0024-zz-SS-enGB/contents/12005010.html)

## 7. Image-processing requirements

### Standard enlargement — first-release default

- High-quality conventional resampling of the original raster image, not nearest-neighbor pixel doubling.
- Target sensible edge quality for enlarged drawings/paintings; use appropriate filter candidates (e.g. Lanczos variants) and manage possible ringing around hard edges.
- Honor EXIF orientation, alpha compositing, embedded color profiles where practical, and deterministic image crop/rotation.
- Do **not** promise that interpolating a low-resolution source creates genuine missing detail.
- Avoid premature rendering of an enormous single composed 60-inch bitmap; support logical tiles or strips, keeping print/export quality independent of screen preview resolution.

### Optional AI enhancement — required in initial release

- Packaged with the installation and usable fully offline; no downloading model weights at first use.
- Use available NVIDIA hardware acceleration after capability/VRAM checks; offer a practical fallback if GPU is absent/incompatible. The exact inference backend/model is **an architecture decision to validate**, not a requirement fixed prematurely.
- Prefer a model and processing pipeline evaluated on **illustrations/artwork** with sharp shapes and stable color fields. Qualities to measure include edge preservation, hallucinated details, color drift, seam consistency, and artifacts around fine marks.
- Provide a visible **Enhance Image** action, status/progress, cancellation, failure recovery, and meaningful original/enhanced comparison at actual artwork detail.
- Preserve source bytes. AI inference may create derived raster caches but never alter source/project data irreversibly.
- Choose AI upscale factor/tiling based on image dimensions, available VRAM, and desired output quality. Large images are processed in overlapping **inference** tiles as appropriate; *AI tile overlap is internal and distinct from the 0-overlap printed page policy*.
- Blend/merge AI inference tile seams consistently before PDF tiling. Never run unrelated model passes independently on adjacent print pages, which could produce visible image discontinuities.
- Keep printing dimensions anchored to physical geometry regardless of chosen enhancement engine.
- Indicate that AI detail is inferred, not guaranteed faithful. Allow instant reverting to Standard.
- Bundle only model assets, code, and runtimes whose licenses permit the planned local redistribution; document licenses, provenance, checksums, and binary sizes.

### Image quality UI

Display **Standard** and **Enhanced** modes, not internal ML terminology. Source-resolution diagnostics may estimate effective pixels/inch at the requested size, but should **not** block printing. An optional confidence/quality warning should say "May appear soft at this size" rather than promise a particular AI result.

## 8. PDF output and direct printing

Both outputs use a **single immutable layout specification**: source artwork/crop, final physical image rectangle, page rectangles, page order, original/AI processing mode, profile, margins, and optional calibration variant. Render to PDF and print via different output adapters only after they have been proven geometrically equivalent.

### PDF export

- Multi-page printable PDF with Letter MediaBox for each sheet by default.
- Preserve color and nominal physical size; include pages in human-understandable row/column order.
- No inadvertent fit-to-page transformations or extra image space consumed by guide labels in borderless mode.
- Save to user-selected location with sensible default filename including project name and dimensions.
- Make an optional **separate assembly guide** PDF page or file with sheet grid, page identifiers, outside trim boundaries, and printing instructions; omit by default for the simplest use.
- Atomic file write where practical; show progress for multi-page jobs, allow cancel, and verify finished output opens.

### Direct print

- Select installed printer, paper profile, copies, and printer driver settings; color is enabled for ET-8500 and grayscale handled for MF210. The chosen automatic/manual paper orientation must be honored by the Windows print job and exposed in any driver dialog, with a warning if a driver changes it.
- Expose **actual-size / no further scaling** safeguards and explain any driver settings the app cannot set automatically.
- For long jobs, progress/cancellation/errors should be clear. Prevent double-submission of the same print job on repeated clicks.
- Preview and PDF remain usable even if the target printer is disconnected or driver fails.
- Direct Print and PDF Print should each have a physical calibration procedure/pathway if their driver behavior differs.

## 9. Saved projects, preferences, and recovery

**Required:** Self-contained editable project file containing the original image (or equivalent lossless source representation), source metadata, crop/rotation, locked or explicit dimensions, fit/crop/stretch choice, selected enhancement mode/parameters, paper and printer profile identifiers, **orientation mode (Auto/Portrait/Landscape) and resolved orientation in the saved render snapshot**, print options, and app/project schema version.

- Moving/deleting the original downloaded image must **not** break the saved project.
- Save As, Recent Projects, Open Project, dirty-state prompt, and periodic recovery of unsaved editing state.
- Store only printer *profile references* and calibration metadata needed for reproducibility; never assume a profile calibrated on one PC/driver is automatically calibrated on another.
- Do not embed giant duplicated AI caches in every project unless intentional. Derived data may be regenerated; clean orphaned caches and keep them versioned by source/model/settings hash.
- Handle migration across schema updates and corrupted projects gracefully; don't destroy originals.
- User files and preferences remain local and private. Optional disk cleanup is understandable and explicit.

## 10. English/Polish localization and accessibility

- English and idiomatic **Polish** localized for every user-facing string (controls, units, file dialogs within app scope, error messages, first-run help, printer calibration, assembly guide, confirmation dialogs).
- Persist language per installation. Language can be changed without resetting projects.
- Prefer terminology nontechnical users recognize: English "Open image", "Final width", "Keep proportions", "Print"; Polish equivalents validated by a fluent reviewer rather than machine-generated placeholders.
- Support number and decimal separators appropriate to selected language/system settings. Unit names and PDF physical geometry must not depend on locale.
- Support keyboard operation, native screen scaling, comfortably readable default font sizes, generous control hit targets, focus visibility, reduced-motion preferences, and sensible contrast. Icons are supplemental to explicit labels; do not make pastel styling reduce readability. Polish labels may be longer than English; layouts must reflow without clipping.

## 11. First-release feature priorities

**P0 — Must ship:**
- Native Windows installation, fully offline, English/Polish.
- JPG/JPEG, PNG, WebP, BMP import from local disk and drag/drop; preserve artwork and color as faithfully as feasible.
- Proportional width/height entry plus exact dual dimension entry; fit/crop/stretch as simple options.
- Preview with tiled page grid and real-world dimensions; automatic minimum-sheet paper orientation with portrait tie-break and manual override.
- Epson ET-8500 profile, borderless 0-overlap default, optional margins, physical calibration workflow and documented limitations.
- Canon MF210 margin-aware secondary profile.
- High-quality conventional resize; optional offline bundled AI illustration-friendly super-resolution with NVIDIA acceleration and suitable fallback.
- Direct print and multi-page PDF export using common geometry.
- Self-contained project save/load, recent projects, recoverable edits.
- Stable operation on representative 24- and 60-inch artwork.
- Light, airy, refined interface; highly readable, approachable text; clear nonblocking progression and restrained, useful satisfaction feedback throughout and at completion.

**P1 — Valuable after initial release:**
- More full-page paper presets/custom paper; robust printable assembly guide enhancements.
- Optional extra conventional sharpness/color controls and comparison loupe.
- Enhanced printer/media presets and reusable calibration histories.
- Quick print presets, batch export, duplicate project.
- Better GPU/runtime auto-selection where initial hardware breadth is limited.

**P2 — Consider only if usage proves demand:**
- Halftone/raster art effects, artistic styling, automated contour extraction, mosaic-oriented shape labels, color palette quantization, website/clipboard imports, external image search, image correction suite, online publishing.

## 12. Nonfunctional requirements

### Responsiveness and scale

- Core controls and preview respond quickly under ordinary 24-inch projects; AI inference runs asynchronously with progress, cancellation, and no frozen main window.
- 60-inch artwork should export/print without requiring a full 60-inch, high-DPI raster in RAM at once. Choose staged/tiled rendering and bounded working memory.
- Deterministic geometry for portrait or landscape candidate layouts and partial edge pages; numerical rounding errors must not accumulate across seams. Auto mode compares both whole-document orientations with portrait tie-break and never mixes orientations within one job.
- Startup and conventional scaling should remain available even if NVIDIA drivers/runtime or AI model initialization fail.

### Reliability and security

- Validate malformed image files and limits; handle unusual EXIF, corrupt images, disk-full errors, printer disconnects, font/localization fallbacks, and unsupported GPU scenarios.
- Do not run arbitrary source-code hooks or unsandboxed downloaded content; images are untrusted bytes.
- No telemetry/account requirement, no cloud upload of images; explicitly document any optional update-check behavior if added later.
- The uninstall process must not delete personal project files without explicit approval.

### Packaging

- Ship a fully usable Windows installer, bundled model(s) and dependencies, Start menu shortcut, normal uninstaller, application version and diagnostics.
- Review code-signing/installer reputation, redistributable licenses, model weight size, and GPU runtime requirements at architecture phase; avoid asserting a particular installer size until the bundle is measured.
- Hardware discovery must be transparent and optional AI degradation clearly handled.

## 13. Domain objects and proposed component boundaries

**Potential domain objects** (logical responsibilities, not mandated classes):

- `SourceImage`: immutable bytes, metadata, oriented dimensions, color profile, alpha behavior.
- `Composition`: crop, rotation, image-to-artwork transform, ratio policy.
- `ArtworkSize`: requested exact physical image rectangle, units and lock policy.
- `PaperSpec`: physical dimensions, **orientation selection mode (Auto/Portrait/Landscape)**, computed effective orientation, optional user margins.
- `PrinterProfile`: manufacturer/model, page capability, default margins/borderless behavior, driver/media identifiers, calibration metadata.
- `PosterLayout`: deterministic image-space/page-space transforms, orientation candidate page counts, selected orientation, rows/columns, partial-edge rects, ordering, physical trim boundaries.
- `EnhancementRecipe`: standard interpolation or local AI model/version, factor, settings and reproducibility metadata.
- `RenderJob`: immutable snapshot of input, layout, processing path, color policy and destination.
- `SavedProject`: self-contained portable file and schema version.

**Proposed functional separation:**

```
Windows desktop UI
    ├─ Import / Source integrity / Project storage
    ├─ Physical sizing + Composition
    ├─ Deterministic geometry / Sheet tiling core
    │    └─ Automatic orientation evaluator (min sheets; portrait tie)
    ├─ Preview service (lower-resolution representation)
    ├─ Processing pipeline
    │    ├─ Conventional resampling
    │    └─ Bundled local AI super-resolution (GPU / fallback)
    ├─ Print profile and calibration service
    └─ Output adapters
         ├─ Multi-page PDF
         └─ Windows Print Spooler / device driver
```

Crucial rule: PDF/Print/Preview must **not** independently recalculate layout or aspect ratio. They consume the same tested geometry. Enhancement algorithms operate on **image content**, not print dimensions.

## 14. Architecture evaluation agenda (decisions intentionally open)

Do not choose technologies merely to mirror Rasterbator's browser implementation. Compare, prototype, then record explicit decisions:

1. **Windows desktop framework and interaction design:** WPF/.NET vs WinUI 3 vs another appropriately stable local framework; evaluate the feasibility of a refined light visual system, accessible readable controls, gentle inline progress feedback, Polish localization, high-DPI preview, deployment reliability, printer integration, and maintenance burden. WPF with modern .NET is a credible default candidate, not yet binding.
2. **Raster and PDF libraries:** High-quality resampling, embedded color handling, ICC/EXIF, memory-bounded tiled rendering, PDF writing fidelity, licensing, and large-image robustness.
3. **AI engine/model:** Evaluate art/illustration-appropriate super-resolution models, offline redistributability, ONNX or equivalent runtimes, GPU compute providers (CUDA/DirectML or alternatives), VRAM footprint and CPU fallback. Compare **output fidelity**, not merely benchmark speed.
4. **Print subsystem:** Investigate Windows print API/driver UI interactions for ET-8500, whether borderless expansion can be controlled, and physical test results; avoid claiming print preview can infer exact physical behavior from PDF alone.
5. **Project format:** ZIP-based self-contained container with manifest + original image is a candidate; ensure migration and safe file opening.
6. **Numerical/layout core:** Isolate deterministic physical-space arithmetic and page-tiling from UI and drivers; decide error tolerances explicitly.
7. **Installer/updates:** Choose reproducible local builds and a reliable end-user installation. Updater is not necessary for v1; offline operation is.

**Highest-risk proof-of-concept:** ET-8500 borderless two-by-two tile test with cross-page geometric registration, *both direct printing and PDF viewer printing*. The risk of per-page driver scaling/cropping needs discovery **before** deep UI polish and broad GPU model integration.

## 15. Validation and acceptance plan

| Test | Acceptance evidence |
|---|---|
| Source fidelity | Sample JPEG/PNG/WebP illustrations retain orientation and color; originals unchanged |
| Proportional sizing | Width-only and height-only inputs produce mathematically correct complementary length |
| Dual dimensions | Mismatched ratio prompts Fit/Crop/Stretch; no silent distortion |
| Physical PDF geometry | Every PDF page exactly 8.5 × 11 in (unless another paper spec chosen); artwork occupies precisely requested physical rectangle according to PDF transforms |
| Auto-orientation optimization | Compare portrait and landscape **full-sheet counts** on identical image dimensions/profile/margins; choose fewer sheets, choose portrait on ties; re-evaluate after input changes; manual override persists; preview/PDF/print agree |
| Orientation-specific scenarios | Test both directions winning, tie (portrait), nonzero margins, partial final rows/columns, portrait↔landscape changes, save/reopen, and PDF-vs-direct-print parity |
| Seams | Adjacent exported pages have identical source-coordinate content at shared boundaries; no pixel gap or duplication introduced by software |
| Partial last sheets | 24-inch, arbitrary fraction-inch, and 60-inch test cases preserve requested size; outer trim instructions are correct |
| Epson calibration | Printed reference sheet measures actual X/Y scaling, clipping and seam registration; correction/profile or warning is applied where possible |
| Epson 2×2 print test | Physical pages assembled without overlap show measured seam continuity; any unavoidable driver crop/deviation is documented and fallback offered |
| Canon profile | Printable-area margins honored and cutting strategy explained; no misleading borderless claim |
| Standard/enhanced preview | Clear visible change in relevant low-res illustration; AI results reproducible enough, do not have inference tile seams |
| GPU/fallback | Works on target NVIDIA configuration; conventional path still works when inference fails; meaningful error rather than crash |
| Large project | Representative up-to-60-inch PDF + direct print preparation completes without UI freeze or unbounded memory |
| Projects | Save, close, delete/move original Downloads image, reopen editable file with correct image and settings |
| Offline | Freshly installed machine with network disabled can use all P0 features, including AI inference |
| Polish | Full common workflow + errors/print guidance in Polish; formats and decimal entry correctly interpreted |
| First-run usability and visual quality | Target user can complete tasks without instruction; core actions are evident, readable and reachable; auto-orientation is discoverable and overridable; there are no obstructive wizard steps |
| Gentle interaction feedback | Clear imported/size-ready/exported states, nonintrusive transitions with reduced-motion fallback, and useful completed-export/print-handoff views that never falsely claim a finished physical print |
| Install | Non-developer user installs, launches, creates, prints/exports, saves, reopens and uninstalls normally |

**Target physical accuracy:** Software geometry is exact within PDF/graphics numerical limits. Physical printed accuracy must be measured on the target device and a tolerable deviation established from calibration results; do not encode an unsupported absolute millimeter guarantee before tests. Capture X/Y scale, repeatability, seam crop and sheet-feed drift empirically.

### End-to-end first-release demonstration

A nontechnical user on the ET-8500 Windows PC can, without documentation or assistance: open a downloaded illustration; set 24 inches wide with ratio lock; optionally use Enhanced; observe the automatically optimized page orientation and sheet count (portrait if tied) and optionally override orientation; print or save a multi-page Letter PDF; assemble edge-to-edge as closely as the calibrated printer allows; reopen the project later; repeat in Polish. An arbitrary 60-inch case also exports correctly without memory failure. The experience should feel easy, deliberate and rewarding without sacrificing clarity, accessibility or user control.

## 16. Delivery strategy and dependencies

Develop in dependency-aware order. These are *engineering workstreams*, not requirements for a long multi-release roadmap:

1. **Geometry/print feasibility:** PDF page sizing and deterministic tiling; ET-8500 calibration and physically printed seam test; Canon margin profile. Do this first because it can challenge the promised assembly workflow.
2. **Core shell and polished interaction:** Native app, welcoming light interface and soft visible progression, import/preview, dimensions/aspect lock, auto minimum-sheet orientation (portrait tie) with manual override, image/paper layout, PDF export and printing based on validated core.
3. **Processing:** Conventional scaling, benchmarked bundled illustration-friendly AI model and GPU/fallback integration, before/after preview.
4. **Persistence, localization and packaging:** Self-contained projects, recent items, Polish/English, installer, offline/no-account verification.
5. **Hardening:** Representative 24/60-inch stress, seam and actual-size regression tests, GPU error paths, accessibility, and observed usability by mother.

Parallelize modules once interfaces (image source, physical geometry, enhancement recipe, render snapshot) are stable. Do not parallelize duplicate layout implementations or optimize AI throughput before correctness on representative artwork.

## 17. Risks and mitigation

| Risk | Impact | Mitigation |
|---|---|---|
| ET-8500 borderless driver expands/crops the edges | Visible sheet seams; incorrect actual scale | Early physical proof-of-concept, print-path-specific calibration, trim-mode fallback |
| Image enhancement invents contours/artifacts | Misleading mosaic template | Default Standard, explicit opt-in AI, comparison, model quality test on illustrations |
| AI model too demanding on actual NVIDIA hardware | Slow/failing enhancement | GPU/VRAM detection, bounded inference tiles, fallback strategy, test actual PC |
| Print path differs from PDF viewer | Inconsistent physical dimensions | Shared layout; calibrate/test each path separately; explicit actual-size guidance |
| Large artwork causes excessive RAM/PDF size | Crashes or unusable jobs | On-demand tile/strip rendering, predictable memory budget, PDF resource reuse |
| Self-contained projects become very large | Storage bloat | Embed original, regenerate caches, compression where safe, transparent file size |
| Polish wording causes user confusion | Abandoned flow | Fluent localization review and real-user usability testing |
| Dense or patronizing UI makes user feel constrained | Friction or avoidance | Spacious readable design, optional guidance, inline status rather than blocking wizard, explicit editability and gentle completion feedback |
| Default paper orientation uses unnecessary sheets | Extra printing, paper waste | Deterministic two-orientation optimization by total sheets with portrait tie-break, continuous recalculation, manual override and regression tests |
| Scope creep into art effects/editing suite | Delayed core utility | Anchor all design decisions to downloaded image → sized paper mosaic template |

## 18. Definition of done and next artifact

The initial application is complete when all P0 requirements in §11 and acceptance checks in §15 pass, with any measurable limitations of borderless printing transparently documented. "PDF renders correctly" alone is **not** enough; a real ET-8500 assembly test is mandatory. The output is also not complete if the primary user needs developer assistance to operate its normal workflow.

**Next document:** `ARCHITECTURE.md`, supported by narrowly scoped proof-of-concept tests and ADRs for (a) desktop UI/runtime, (b) geometry + PDF + Windows printing, (c) AI model/inference engine, (d) portable project storage and installer. Record actual ET-8500 print calibration results and available NVIDIA GPU model/VRAM during engineering validation, not as blocking product-definition questions.

### Open engineering measurements, not product-level ambiguities

- Installed NVIDIA GPU model, VRAM, driver version and Windows OS version on mother's PC.
- Exact Canon MF210 submodel and driver capabilities.
- Empirical ET-8500 borderless expansion, X/Y scaling, seam loss, orientation sensitivity and repeatability with standard plain paper.
- Physical tolerance realistically achievable with the ET-8500; acceptable trim-mode escape hatch if borderless printer physics prevents exact butt-join registration.
- Candidate AI model licenses, runtime sizes, actual illustration fidelity and inference memory use.

These should be **discovered/tested**, not guessed or used to delay architecture planning. No further user preference questions are necessary at this point.
