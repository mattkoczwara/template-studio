# Mosaic Template Studio — Architecture & Technology Selection

**Status:** Recommended architecture, subject to printer and AI fidelity proof-of-concept validation  
**Version:** 0.2 (recommendation adjudication) | **Research date:** October 20, 2026  
**Input:** `POSTER_STUDIO_PRODUCT_DEFINITION.md` v1.1  
**Target:** Offline, native, self-contained Windows x64 desktop application  
**Primary user:** Nontechnical artist creating enlarged full-color paper templates  
**Primary printer:** Epson EcoTank ET-8500 (borderless US Letter); secondary: Canon MF210 series

## 1. Executive decision

Build a **single, native WPF/.NET 10 desktop application** organized as a **modular monolith**, with an **isolated native AI worker process** for crash containment. Use a **single immutable, deterministic print-layout specification** for the preview, PDF exporter and Windows printing adapter.

**Core stack:**

| Concern | Recommendation | Selection rationale |
| --- | --- | --- |
| Language/runtime | C# 14 / .NET 10 LTS, `net10.0-windows`, Windows x64 | Supported LTS; mature printer APIs; one maintainable language |
| Desktop UI | WPF/XAML, using restrained `WPF-UI` controls and bespoke design tokens | Native accessibility, layout, high-DPI, established Windows printing; possible refined, airy interface |
| View-model pattern | `CommunityToolkit.Mvvm` | Lean source-generated observable properties/commands |
| Image processing | `Magick.NET-Q8-x64` (ImageMagick 7), selectively Q16 if measured benefit | Mature raster decode, EXIF/ICC handling, high-quality resize, explicit resource limits |
| Live preview | WPF image surface + page-grid overlay, background-created frozen preview images | Avoid a second rendering framework, and keep expensive decoding off the UI thread |
| PDF generation | PDFsharp **6.2.x stable**, test image-XObject reuse and JPEG/PNG embedding | Precise physical page boxes and clipping; file-size and memory constraints measured, not assumed |
| Direct printing | WPF `PrintDialog`, `PrintTicket`, `PrintCapabilities`, `XpsDocumentWriter` / `FixedDocument` | Native Windows printer dialog and spooler; configurable page orientation and device capabilities |
| AI inference | Bundled `waifu2x-ncnn-vulkan` + tested artwork model (CUNet candidate); 2×/4× as appropriate | GPU-accelerated, model+binaries portable, no separately installed CUDA or Python, CPU mode documented |
| AI comparison candidate | Real-ESRGAN NCNN Vulkan (`realesrgan-x4plus-anime` or general) | Evaluate against CUNet on actual source illustrations; ship only selected model(s) |
| Projects | Versioned `.mosaic` ZIP containing JSON manifest + immutable original image | Self-contained, easy to save/reopen/share; no database |
| Settings/localization | JSON settings + `.resx` English/Polish resources; string-backed dimension edit state | Correct intermediate typing, decimal comma and deferred parsing/validation |
| Logging | `Microsoft.Extensions.Logging` + Serilog hosting/provider + `Serilog.Sinks.File` | Actual bounded rolling log files under `%LocalAppData%`, no telemetry |
| Installer | **Inno Setup 7.1**, publish untrimmed `win-x64` self-contained folder | Offline installer with models, native binaries, Start-menu entry and uninstall |
| Validation | xUnit + independent PDF inspection (PdfPig) + WPF UI automation (FlaUI) | Geometry-first verification, Windows workflows, physical printer acceptance |

**Dependency policy:** Pin exact stable versions in `Directory.Packages.props` and native assets with provenance/SHA-256. Above versions are a **version family baseline**, not an assertion that all latest patch releases were verified together. No prerelease dependencies without a documented reason.

### Why WPF rather than WinUI 3?

Microsoft positions Windows App SDK/WinUI 3 as its modern native framework for new Windows apps, and WinUI 3 could produce an excellent visual experience. Nevertheless, **printer integration and predictable PDF/physical output dominate this particular product**. WPF's mature `System.Printing` and XPS printing pathway makes direct printing more straightforward. A polished, custom light design system—with restrained use of WPF-UI controls—can achieve the desired look without switching to an Electron/WebView shell.

Use WinUI 3 only if a small prototype demonstrates a compelling UX advantage **without increasing printer-driver integration risk**. Do not mix WinUI and WPF for routine controls.

### Why Magick.NET rather than ImageSharp?

Magick.NET provides broad mature formats, image resampling, ICC-related tooling and resource-limited native raster processing. It is Apache-2.0 licensed. ImageSharp is a credible alternative, but uses the Six Labors Split License and has less reason to be introduced here when Magick.NET already covers the required raster pipeline. Security updates and caps for untrusted downloaded images are essential whichever decoder is used.

### Why PDFsharp rather than QuestPDF or HTML-to-PDF?

This is **precise image placement on fixed physical sheets**, not a flowing report or business document. PDFsharp exposes the PDF page boxes, image transforms and clipping needed for this use case. PDFsharp 6.2.3 documents .NET 10 support; 7.0 was preview-only during the research. QuestPDF is mature but targets component-based document layout and has a tiered license, so it adds no decisive advantage here. Do not produce print pages through browser CSS or screenshots.

## 2. Core architecture

```text
┌─────────────────────────────────────────────────────────────┐
│ WPF Desktop Application (C#, XAML, MVVM, EN/PL)            │
│  Welcome • Open Image • Size • Preview • Enhance • Export  │
└──────────────────────────────┬──────────────────────────────┘
                               │ user commands / state
┌──────────────────────────────▼──────────────────────────────┐
│ Application Services                                       │
│  Projects • Settings • ImageImport • EnhancementOrchestrator│
│  PrinterProfiles • Calibration • RenderJobs • LocalLogging│
└─────────────┬────────────────┬─────────────────┬───────────┘
              │                │                 │
┌─────────────▼─────────┐ ┌────▼───────────┐ ┌──▼───────────────┐
│ Pure Geometry Domain │ │ Image Pipeline │ │ Output Adapters  │
│ composition, in/mm,  │ │ Magick.NET     │ │ PDFsharp export  │
│ page coverage,       │ │ standard scale │ │ WPF print/XPS   │
│ auto portrait tie    │ │ enhancement    │ │ assembly guide  │
└───────────────────────┘ └──────┬─────────┘ └──────────────────┘
                                 │ isolated process boundary
                             ┌───▼────────────────────────────┐
                             │ Bundled NCNN/Vulkan AI worker │
                             │ model, tile selection, CPU   │
                             │ option, cancellation         │
                             └────────────────────────────────┘
```

**Architectural constraint:** `LayoutEngine` computes exactly one `LayoutPlan` per immutable `RenderJob`; neither the preview, PDF writer, nor Windows printer is allowed to independently choose the layout, page count or aspect policy.

**Recommended solution projects:**
- `Mosaic.Desktop` — WPF/XAML, custom theme, view models, user commands.
- `Mosaic.Core` — domain model, physical geometry, pure layout and orientation selection.
- `Mosaic.Imaging` — source import, color/metadata, conventional scaling, cache handling.
- `Mosaic.Enhancement` — backend interface and local AI process manager.
- `Mosaic.Output` — PDFsharp, WPF print adapter, assembly guide, calibration transforms.
- `Mosaic.Storage` — ZIP project format, settings, schema migrations and recovery.
- `Mosaic.Tests` — geometry, imaging, PDF, project serialization, failure paths.
- `Mosaic.UiTests` — limited Windows-only E2E journeys.

These are **logical and build-time boundaries**, not microservices. Merge projects if they create pointless interface churn; maintain one owner for the geometry rules.

## 3. Geometry: the correctness-critical system

**Model physical geometry in a single unit convention.** Store dimensions internally using `decimal` millimeters or integer micrometers. One US Letter sheet is exactly 8.5 × 11 in (215.9 × 279.4 mm); a PDF page uses 612 × 792 points (72 points/in). Convert at output boundaries only.

Input model:
- Source image dimensions after orientation; source bytes remain immutable.
- Composition choice: preserve proportions, Fit, Crop or explicit Stretch.
- Requested artwork `WidthMm` / `HeightMm`, **not sheet count**.
- `PaperSpec`, print margins / trim mode, profile, `OrientationMode=Auto|Portrait|Landscape`.
- Printer/output calibration is distinct from nominal document geometry.

For a borderless zero-margin case:

```text
portraitSheets = ceil(imageWidth / 8.5) * ceil(imageHeight / 11)
landscapeSheets = ceil(imageWidth / 11) * ceil(imageHeight / 8.5)
auto = portrait if portraitSheets <= landscapeSheets, else landscape
```

For nonzero margins, calculate **effective printable tile coverage** using the explicit trim/assembly strategy; do not naively divide by the full sheet.

`LayoutPlan` includes explicit page dimensions, orientation, page-space/artwork-space clipping transforms, row/column and order, unused outer-edge trim bounds, and a physical bounding rectangle. **Each seam edge refers to identical source coordinates**, without independently rounded start/end points. A manual orientation selection persists until Auto is restored.

**Quality rule:** Software-exact PDF geometry and printer-exact physical paper are different. The printer may enlarge/crop an edge even at nominal 100%; the user must not be promised perfect assembly without physical measurement.

## 4. Epson ET-8500 calibration — gating proof of concept

Manufacturer documentation confirms US Letter borderless plain-paper compatibility. Epson also documents image expansion and possible slight cropping in borderless workflows; the particular exposed Windows driver controls must be confirmed on the installed PC (do not assume camera-panel settings map directly to Windows settings).

**Mandatory early hardware test:**

1. Generate a 2×2 page calibration design with known one-inch/two-inch rulers, X/Y guides and lines crossing page joins.
2. Print using the actual installed ET-8500 Windows driver, US Letter plain paper, borderless, intended quality, one-sided; save the driver setting record.
3. Assemble *without overlap*, measure printed lengths, page-edge source loss and 2×2 seam misalignment, repeat for reproducibility.
4. Repeat using a PDF viewer's **Actual size/100%** print path; verify whether driver behavior differs from direct WPF output.
5. Investigate driver borderless expansion settings, calibratable scale/translation and optional page-specific inverse compensation.
6. If a seam cannot be reproduced reliably without cropping, **offer precise margin/trim mode**, do not falsely assure seamless butt-joins.

Store calibration by printer ID + paper type/size + borderless/quality/driver settings + output pathway. Maintain **nominal exact PDF** separate from any intentionally *printer-compensated* output variant.

For Canon MF210, start with monochrome/non-borderless profile and trim/cut lines; do not promise borderless printing or color. Exact submodel/driver capabilities should be discovered, not guessed.

## 5. Image pipeline and color fidelity

**Standard** (default):
1. Accept downloaded JPEG, PNG, WebP (and common compatible formats).
2. Decode with resource limits, honor EXIF orientation and inspect color profile / alpha channel.
3. Keep immutable original; apply non-destructive crop/rotation/composition.
4. Resize with a high-quality filter (Lanczos is a starting point; evaluate halo/ringing on crisp artwork).
5. Produce a memory-bounded cached image for preview and a page-rendering image appropriate to the physical print resolution; do not allocate a monolithic gigapixel bitmap merely because the poster is 60 inches wide.

**Image dimensions are not print dimensions.** A modest source bitmap can be placed at a mathematically exact 24-inch width in the PDF without inventing pixels. Enhancement affects perceived detail, not the destination rectangle or page count.

**Color:** Prefer an explicit predictable sRGB-oriented printing path with retained source profile information and optional normalized preview. Color output ultimately depends on the printer's driver, ink, paper settings, and application-side transforms. Avoid double-applying color management; test actual artwork.

**PDF implementation feasibility and memory policy:** Try a **single reused image XObject clipped and translated across the pages** (the exact physical placement is determined only by `LayoutPlan`). This can avoid both rendering dozens of independent page crops and repeatedly embedding the same source JPEG. PDFsharp supports `XImage.FromStream`, but the claim that this **always performs zero-copy/zero-decode passthrough** is unverified and must not be assumed; PDFsharp documents additional buffer copying for some `MemoryStream` instances. For large enhanced images or unsupported source formats, generate **bounded** raster regions, using **lossless PNG** for crisp illustrated lines and **high-quality JPEG only when acceptable** after visual validation. Avoid needless JPEG transcodes of existing drawings. Test output size, managed/native peak memory, clipping, color, visual seams and reuse; switch the *PDF backend only* if controlled prototypes show PDFsharp cannot satisfy these gates.

## 6. AI super-resolution — recommendation and escape hatch

**Primary candidate:** `waifu2x-ncnn-vulkan` with the bundled **CUNet** model and conservative denoising. It is portable with model and runtime packaged together, works with NVIDIA Vulkan drivers without separately installing CUDA/Python, supports automatic/smaller inference tiles and documents `-g -1` CPU mode. The model is designed around illustration/cartoon-like features; it is a **candidate**, not an unproven universal quality winner.

**Alternative to benchmark:** `Real-ESRGAN-ncnn-vulkan` using its illustration/anime model and general model. Real-ESRGAN can produce sharper results on some sources, but may alter exact visual contours. Score using *real examples supplied by the mother*, not just marketing samples. Other SISR architectures are not needed unless these fail.

**Execution design:**
- UI chooses **Original / Enhanced**, shows a zoomed comparison before committing.
- Managed enhancement adapter invokes bundled, hash-verified native executable in a restricted working directory as a child process. Quote paths safely; no shell execution; use cancellation to terminate child if needed. Do not request Internet or download weights.
- Process *source artwork*, not independently rendered print pages. Process tiles with proper overlap/edge padding internally; normalize final image and derive all print pages from the **single enhanced artifact**.
- Detect installed NVIDIA GPU/VRAM and Vulkan readiness; choose tile memory budgets. Retry with smaller inference tiles after an out-of-memory result; offer slower CPU execution where acceptable. If enhancement fails, keep **Standard** fully functional and make failure clear.
- Persist model name/version/parameters; optional derived cache is disposable, not part of source integrity.
- Limit output resolution to what is useful for print and system memory (e.g. configurable 150/200/300 effective ppi ceiling, validated empirically). **Never imply AI-created detail is proven faithful**.

**Why not Windows ML as the default v1 backend?** By 2026 Windows ML is Microsoft's preferred Windows/ONNX deployment direction and supports bundling models. It provides CPU/DirectML and dynamic execution-provider selection. However, a suitable converted ONNX model must be validated for exact operator compatibility, output consistency, memory and deployment. Optional hardware-specific EP catalog updates may be downloaded on Windows 11, which must be disabled/avoided for this strictly offline application. For a single known NVIDIA desktop and illustration-focused worker, the portable NCNN Vulkan binary gives a shorter, already-distributable path. Define `IImageEnhancer` so Windows ML can replace NCNN *after evidence*, without changing UI or print logic.

**Downselection gates (pass before committing a bundled model):**
- Actual mother-sourced illustrations, including low-resolution JPEG compression and flat-color linework.
- Side-by-side line fidelity, edge ringing, invented details, color stability and tile seams.
- Representative 24-inch artwork time and memory use; occasional 60-inch export.
- RTX/NVIDIA target compatibility and functioning offline CPU fallback where practical.
- License, attribution, reproducible model hash, model/bin shipping rights.

## 7. PDF export and direct print

**PDFsharp:** fixed `MediaBox` of 612 × 792 pts for Letter portrait (or swapped for landscape); exact image clipping at page boundary and reuse where feasible. No invisible shrinking, page labels on image areas, bleed overrides or independently rounded tiles. Print guides appear on a **separate optional assembly guide**.

**WPF print adapter:** use `PrintCapabilities` to discover device-dependent paper/printable area and `PrintTicket` for orientation, size, simplex and color. Render a `FixedDocument`/pages using `LayoutPlan`. Send through `XpsDocumentWriter` / `PrintDialog`; the driver may transform the spool format. Apply measured calibration only in the appropriate output adapter. If the driver changes the requested settings, tell the user what must be selected in native printer properties.

**Printing fallback decision:** If WPF/XPS fails the early Epson calibration test, prototype **rendering the same PDF pages to a Windows printer graphics context through an actively maintained PDFium-based adapter** (or another validated PDF renderer) and compare physical output. This is an alternative *rendering pathway*, **not raw-PDF spooling**, and cannot by itself bypass Epson's borderless expansion/crop. Avoid adopting an archived PDF printing wrapper without maintenance, redistribution-license and binary-update checks. Retain the same `LayoutPlan` and calibration audit trail.

**Avoid double scaling:** For PDF viewer printing, explicitly advise **Actual Size / 100%**, never fit-to-printable-area. For direct print, use physical, not UI-pixel, page units. Maintain separate print-path calibration evidence. The UI may report "**Sent to printer**" only once the job was accepted, never "**Printed successfully**" without a physical-completion signal.

## 8. Refined user experience

WPF-UI supplies modern controls while bespoke XAML styles define appearance, avoiding a default corporate settings-app aesthetic.

- Single light, editorial window; spacious canvas with large image preview on the left and a concise settings rail on the right.
- One main visible journey: **Image → Size → Quality → Print / Export**, but no mandatory wizard. All values remain editable.
- Page grid is a transparent visual guide; page count automatically minimizes sheets (portrait if tied) and can be overridden.
- High-legibility body typography (~15–16 effective px baseline or equivalent WPF density), carefully restrained contrast/color, obvious field units, generous focus targets; test at Windows 125/150% scaling.
- Concise reassuring microcopy ("Image ready", "6 sheets", "Ready to print") and refined transition timing without forced congratulations.
- Show progress/cancel for AI processing and PDF export; differentiate exported file success from printer job submitted.
- Persistent and equally prominent **Print** / **Export PDF** commands.
- English/Polish localization via resources; do not concatenate translated fragments; test decimal separators and accessible names in both languages.
- Provide compact optional printer calibration workflow, never a blocking first-launch tour.
- Keep advanced sizing, crop policy, paper and color controls discoverable but collapsed by default.

## 9. Projects, privacy, installer, dependency safeguards

**`.mosaic` ZIP format:**

```text
manifest.json                # schema, metadata, physical sizes, settings
source/original.<extension>  # original downloaded bytes, immutable
assets/thumbnail.jpg         # small preview, generated when saved and used for fast reopen
```

Open project **lazily**: read the manifest and thumbnail entries first using `ZipArchive`, show the saved layout immediately, and open/decode the original from the archive when full-resolution preview, export or enhancement needs it. Large entry processing is streamed to bounded cache storage when appropriate; do not buffer the entire ZIP or full original merely to fill the welcome preview. Validate per-entry declared/actual decompressed sizes, entry names, compression ratios and project schema; keep the original available for accurate zoom/detail editing.

Calibration is machine/printer-specific and stays in local settings rather than becoming universal project print coordinates. Save atomically to a temp file then replace; reject zip-slip paths, zip bombs, oversized embedded files and invalid schema versions. Autosave recovery metadata separately to `%LocalAppData%/MosaicTemplateStudio/`; keep exported PDFs in user-selected locations.

**Installer:** `.NET 10 win-x64` self-contained *folder* distribution, with explicit `<PublishTrimmed>false</PublishTrimmed>` and `<PublishAot>false</PublishAot>` in the WPF project, + bundled models/native workers through Inno Setup 7.1. Windows WPF currently does not support trimming. Use standard Inno compression (including LZMA2 where appropriate) but **do not assume any fixed percentage reduction**: benchmark installer sizes with the actual model payload. Do not demand .NET/Python/CUDA installations after setup, and avoid a single-file .NET publish option until native extraction has been validated. Include English/Polish installer labels if possible, desktop/Start shortcuts, repair or reinstall and ordinary uninstall. No automatic online update in v1. Code-signing is strongly recommended for distribution to family.

**Security/privacy:** all work local; no uploads or automatic telemetry. Resource- and file-size-cap downloaded images; minimize executable search-path risks, verify model checksum and native-worker path; avoid untrusted command arguments. Diagnostic logs contain paths only when necessary, no source artwork. Check dependency vulnerabilities and third-party licenses before release.

## 10. Test strategy and acceptance gates

1. **Pure geometry unit/property tests:** any positive dimensions, proportions, equal page counts portrait win, portrait-win/landscape-win cases, fractions, partial edges, zero/nonzero margins, manual override, seam coordinate continuity. Evaluate on validated values synchronously (microsecond/millisecond scale); no background-task per keystroke.
2. **Independent PDF inspection and memory budgets:** PdfPig reads media boxes, counts pages and permits checking page-size/orientation without relying on PDFsharp's own reader. Rasterize representative PDFs and inspect line fidelity, continuity and JPEG artifacts; measure native/managed working set and file size for 24- and 60-inch cases.
3. **Printer POC (hard gate):** real Epson 2×2 borderless test direct print and PDF viewer route; measure X/Y error and clipping, verify actual join policy or calibrated trim fallback.
4. **AI fidelity tests:** approved illustrations, GPU/cpu fallbacks, no print page boundary seams, visual before/after.
5. **Localization/UX:** EN and PL, Polish accent characters, string-edit states (`3,`, empty, `3.`), decimal comma parsing on commit, 125/150% Windows scaling, keyboard-only navigation, change any step out of order, responsive import/preview progress and cancel.
6. **24/60-inch load cases:** memory/resource bounded, UI responsive, cancellation and predictable output size.
7. **Distribution:** clean offline Windows PC install, open image from Downloads, generate correct PDF, direct print, save/reopen embedded image after deleting download, uninstall.

Tests should emphasize geometry invariants and *one actual print* early. Avoid large-scale performance tuning before functionality is proven.

## 11. Tradeoffs / architecture decision log

| Decision | Chosen | Not chosen / caveat |
| --- | --- | --- |
| Native UI | WPF + WPF-UI | WinUI 3 (modern but more integration cost); Electron/WebView (unnecessary dependency/print complications) |
| Raster processing | Magick.NET Q8 | ImageSharp (license considerations); OpenCV (oversized dependency for this workload) |
| PDF | PDFsharp stable 6.2.x | PDFsharp 7 preview, QuestPDF, browser print CSS |
| AI | waifu2x NCNN Vulkan **candidate**, isolated worker | Windows ML/ONNX only if a tested model improves fidelity, resource use or reliability; Real-ESRGAN benchmark competitor |
| Export vs print | Separate PDF and XPS adapters, shared immutable geometry | Generating direct-print page sizing independently |
| Storage | Lazy-open self-contained ZIP + JSON + cached thumbnail | SQLite server/database, cloud storage |
| Setup | Untrimmed/un-AOT .NET 10 + Inno Setup 7.1 | Store-first MSIX, unsafe trimming/AOT, Electron updater |
| Scale | 24-inch everyday and 60-inch occasionally | Monolithic 300+ DPI giant bitmap allocation |
| Calibration | Explicit profile and measured physical output | Unmeasured assumptions that borderless = geometric accuracy |

**Freeze for implementation:** C#/.NET 10 + WPF + MVVM + WPF-UI, Magick.NET, PDFsharp 6.2.x, deterministic geometry core, self-contained ZIP projects, WPF Print/XPS, Inno Setup 7.1.

**Conditional decision:** The shipped AI model/runtime, PDF image-embedding strategy and physical calibration correction math must be selected *by narrowly scoped measured prototypes*, not by benchmark assumptions or preference.

## 12. Immediate build sequence

**Proof A — PDF geometry and real printer.** Implement the pure `LayoutEngine`, a CLI/test harness exporting 2×2 calibration pages with PDFsharp, and a minimal direct WPF print job using the same `LayoutPlan`; print on the ET-8500. Record measured seam and scale behavior. **Do not polish the app before this works.**

**Proof B — actual-source AI comparison.** Bundle/test waifu2x CUNet and a Real-ESRGAN illustration model on a small fixed mother-sourced corpus. Benchmark fidelity, artifacts, offline performance and CPU resilience; select and pin one.

**Product assembly.** Build WPF UI + bilingual resources, wire image import/crop/dimensions/preview, quality mode, direct print/PDF, calibrated profiles, self-contained projects and installer. Keep tests at module boundaries and run integrated validation after meaningful batches.

**Finish.** Acceptance tests on a fresh offline Windows machine and the ET-8500; mother independently completes a real 24-inch mosaic template; 60-inch project demonstrably works; printer limitations and fallback are clear.

---

## Primary research sources (official documentation / upstream projects)

1. .NET 10 support dates: https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core
2. Windows App SDK guidance: https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/
3. WPF printing, XPS / PrintTicket: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/documents/printing-overview
4. WPF-UI official repository: https://github.com/lepoco/wpfui
5. CommunityToolkit.Mvvm: https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/
6. Magick.NET: https://github.com/dlemstra/Magick.NET
7. PDFsharp stable 6.2.3 / .NET 10: https://docs.pdfsharp.net/General/History/README-v6.2.html
8. PDFsharp image-format limitations: https://docs.pdfsharp.net/PDFsharp/Topics/Bitmap-Images/Formats.html
9. waifu2x NCNN Vulkan: https://github.com/nihui/waifu2x-ncnn-vulkan
10. waifu2x published releases: https://github.com/nihui/waifu2x-ncnn-vulkan/releases
11. Real-ESRGAN NCNN Vulkan: https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan
12. Windows ML overview: https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/overview
13. Windows ML offline model distribution: https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/model-distribution
14. Windows ML vs own execution providers: https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/windows-ml-eps-vs-bring-your-own
15. Epson ET-8500 borderless paper: https://files.support.epson.com/docid/cpd5/cpd59879/source/printers/source/paper_loading/reference/et8500_8550_l8160_l8180/paper_borderless_types_us_can_et8500.html
16. Epson borderless expansion caveat (camera settings; not necessarily identical to Windows UI): https://files.support.epson.com/docid/cpd5/cpd59879/source/printers/source/printing_device/reference/et8500_8550_l8160_l8180/device_camera_print_options_et8500_l8180.html
17. Inno Setup 7.1.0: https://jrsoftware.org/files/is7-whatsnew.htm
18. PdfPig for independent page inspection: https://github.com/UglyToad/PdfPig
19. ImageSharp licensing comparison: https://github.com/SixLabors/ImageSharp/blob/main/LICENSE
20. QuestPDF license comparison (2026 edition): https://github.com/QuestPDF/QuestPDF/blob/main/LICENSE.md

*Research is documentary; no actual Epson print, native application build, model benchmark, or printer driver inspection was performed during this review.*


## 13. Review of external architecture recommendations (decision record, October 20, 2026)

**Decision rule:** Accept an implementation change only when it demonstrably improves correctness, resource use, maintainability, or end-user experience. Do not change languages, frameworks or runtimes merely to match suggested pseudocode.

| Recommendation | Decision | Reason and resulting contract |
|---|---|---|
| Export every page as a JPEG for PDFsharp | **Modify** | JPEG compresses, but adds generation costs and lossy line/color artifacts. First test **one reusable clipped XObject**; if page rasterization is needed, cap tile memory and choose JPEG or lossless PNG by content. `XImage.FromStream` is not guaranteed zero-copy. |
| Run decode/resampling off WPF UI thread; freeze WPF bitmaps | **Accept** | `Task.Run` for synchronous, CPU-bound decoding/resampling; cancellation and bounded queue. Construct and `Freeze()` a `BitmapSource` on its worker thread before publishing; or construct it on UI dispatcher. `Freeze()` is needed **only for cross-thread sharing**, not every bitmap. |
| Add Serilog rolling file sink | **Accept** | The framework has no built-in general-purpose rolling-file logger. Add `Serilog.Extensions.Hosting` and `Serilog.Sinks.File`, with bounded daily/size rotation and privacy-safe messages. |
| Disable PublishTrimmed / PublishAot; use Inno LZMA2 | **Accept with correction** | WPF trimming is unsupported. Explicit false properties; compress installer payload, **no unsupported 70% guarantee**. |
| Save ZIP thumbnail; lazy original extraction | **Accept with correction** | Fast project reopen via manifest+thumbnail; high-resolution preview **also** needs original (not only print/export/AI). Stream/extract on demand; test zip bombs and project integrity. |
| Replace deterministic C# layout with Python | **Reject** | A pure C# geometry domain is deterministic, locally testable and faster to integrate into WPF; Python adds packaging/interoperability complexity with no geometry benefit. |
| Python + ONNX inference strips + ReportLab | **Reject** | Adds second language and competing PDF/layout implementation. Keep a C# enhancement orchestrator, isolated local native worker and the same PDF engine. |
| Tauri/TypeScript project manager | **Reject** | Adds another UI/runtime bridge solely for ZIP+JSON operations that built-in C# handles directly. |
| Python inverse scale calibration for Epson | **Modify** | Calibration and device settings are important, but a single 0.98× affine factor cannot reliably recover per-edge crop, drift or feed variability. Validate driver's **Windows Borderless > Settings > Expansion** controls first, measure X and Y, and use margin-and-trim fallback if no seamless butt join is feasible. |
| Blend overlapping AI tiles before cutting pages | **Accept principle** | Avoid **printer-page-aligned** AI inference. Prefer native upscaler's tested internal padding/tile handling and one coherent derived image. Only write our own overlap/crop/blending if reproducible test images reveal seams. |
| Async/debounced auto-orientation calculation | **Reject Task.Run; accept edit debouncing** | Pure sheet-count arithmetic is negligible. Validate/commit localized dimension text, recompute geometry immediately for valid values; optionally debounce preview rerender, **not geometry**. |
| XPS to Epson: PDFium printing fallback | **Conditional** | Real-device WPF print test first. If it fails, compare an actively maintained PDFium **render/print adapter**; this cannot bypass Epson driver scaling. Never send arbitrary PDF bytes as a RAW print job to a printer lacking native PDF interpretation. |
| Replace NCNN worker with in-process ONNX DirectML | **Reject for initial implementation; benchmark gate** | Fewer disk handoffs do not guarantee less memory use or better reliability; an in-process native crash could terminate the UI. DirectML is in sustained engineering, with Windows ML now preferred for new ONNX Windows integration. An `IImageEnhancer` interface permits a proven backend switch after fidelity and resource benchmarks. |
| String-backed localized dimension inputs | **Accept** | Keep arbitrary text (`"3,"`, empty, partial entry) separate from validated `decimal` mm; parse against selected culture on commit, show inline validation, preserve prior valid geometry while editing. |
| Set Magick.NET ResourceLimits and bounded image processing | **Accept** | Limit memory/map/disk/width/height/time/threads at startup based on detected resources. Limits may force disk-backed pixel caches rather than prevent all allocation; use disposal, bounded preview and tile paths, cancellation and measurements. |

### 13.1 Concrete implementation contracts added

**WPF async state:**
- `ImportImageAsync` reads metadata/downsamples off dispatcher; use `Task.Run` for CPU-bound synchronous work, avoid nested unbounded tasks. `CancellationToken` and last-request-wins prevent stale previews.
- Worker-created WPF `BitmapSource` is completely initialized and `Freeze()`d before assignment to a UI-bound VM; otherwise hand raw pixels or encoded bytes to the dispatcher for UI-thread construction.
- UI property changes and progress updates are marshalled onto UI dispatcher. No WPF element or mutable `DispatcherObject` is touched by a worker.

**Localized measurements:**
- Text controls bind to strings and do not reject intermediate typing.
- On commit/focus loss, parse culture-aware decimal; reject negatives, 0, impossible canvas sizes with friendly English/Polish validation. Display values in inches; keep internal exact rational/decimal physical dimensions.
- Orientation evaluator operates on **last valid** `ArtworkSize`, never partially parsed text; immediate math and sheet-count update upon successful commit. Debounce expensive image preview only.

**Local logs:**
- `%LocalAppData%/MosaicTemplateStudio/logs/` with restricted file access where possible.
- Set daily rolling plus a small size cap and limited retention (e.g. 5–10 files, bounded MB totals). No image data, full paths or personal file names by default.
- Log structured job IDs, model and driver versions, non-sensitive status, timings, failures and peak memory metrics. Log an optional diagnostic bundle only after explicit user action.

**Magick and export:**
- Set ImageMagick `ResourceLimits` *before first decode*. Tune `Memory`, `Map` (if exposed), `Disk`, `Width`, `Height`, `Area`, and `Thread` and record what is actually enforced; ImageMagick may fall back to disk pixel cache after a memory cap, so free disk must also be checked. Retain native buffers only for the duration of one job.
- Stream JPEG/PNG output where supported and dispose promptly; do not materialize all PDF page tiles in RAM. JPEG quality should never be lowered invisibly on line art.
- Use atomic file saves for PDF/project output and ensure cancelled jobs discard incomplete temporary files. PDFsharp may hold resources through `PdfDocument` save; measure and impose a job-level budget.

**Printer measurements:**
- An internal 2×2 calibration fixture must support independent checks for absolute size, X/Y anisotropy, image translation, **lost content at page edges**, and repeatability.
- Record model/driver/paper/quality/borderless-expansion setting and print pathway (WPF vs PDF viewer/PDF renderer). Calibration corrections must be reversible and separate from nominal PDF export.
- The UI must never promise a seamless untrimmed join if Epson's required expansion destroys edge content; offer documented trim mode.

**Native AI integration:**
- Source working asset is compressed lossless file (not a giant raw RGB buffer). GPU CLI file I/O may cost time, but protects application process and supports bounded memory; evaluate total elapsed time rather than assuming a bottleneck.
- Invoke child process via fixed trusted executable and escaped args, never a shell; cap size/time/temp usage, robust progress/cancel, cache outputs by source/model/parameters. Use worker's own tiling first.
- Select ONNX/Windows ML only if a **redistributable offline model** proves better in source-line fidelity, seam behavior, crash isolation or whole-job time/memory on mother's NVIDIA PC.

**Deployment:**
- `PublishTrimmed=false`, `PublishAot=false`, `SelfContained=true`, `RuntimeIdentifier=win-x64`, `PublishSingleFile=false` by default.
- Pin tested stable package versions, native worker hashes and model licenses. Generate Inno Setup installer and test on a clean offline Windows device. Do not assert compression-ratio outcomes without measurement.

### 13.2 Validation that can materially change a technology choice

1. **PDF gate:** On representative 24- and 60-inch images, compare reused source-XObject PDF, bounded page JPEG, and bounded lossless PNG: peak managed+native memory, temporary storage, time, PDF size, and illustrated line integrity. Keep PDFsharp if acceptable.
2. **Print gate:** Epson ET-8500 with actual Windows driver: direct WPF XPS vs PDF viewer 100% test. Try borderless expansion settings and measure seam loss; only prototype PDFium-based route if XPS materially fails.
3. **AI gate:** waifu2x/CUNet and Real-ESRGAN on mother-sourced artwork; compare optional ONNX/Windows ML only if observed quality or throughput motivates it. Always include no-AI comparison.
4. **UX gate:** Recover project thumbnail quickly, then load full-resolution zoom as needed; dimension typing with comma and incomplete values must remain smooth and safe.
5. **Distribution gate:** Untrimmed self-contained installer, binaries/models working with networking turned off, measurable package size, fully uninstallable.

### Verified upstream references for this review

- PDFsharp image loading/stream semantics: https://docs.pdfsharp.net/PDFsharp/Topics/Bitmap-Images/Drawing.html
- PDFsharp image types and compression: https://docs.pdfsharp.net/PDFsharp/Topics/Bitmap-Images/Formats.html
- PDFsharp clipping API: https://docs.dndocs.com/n/PDFsharp/6.2.4/api/PdfSharp.Drawing.XGraphics.html
- WPF dispatcher and threading: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model
- WPF `Freezable` and `BitmapSource`: https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.bitmapsource?view=windowsdesktop-10.0
- .NET native trimming limitations (WPF unsupported): https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities
- .NET built-in logging providers: https://learn.microsoft.com/en-us/dotnet/core/extensions/logging-providers
- Serilog file sink: https://github.com/serilog/serilog-sinks-file
- Magick.NET `ResourceLimits`: https://github.com/dlemstra/Magick.NET/blob/main/src/Magick.NET/ResourceLimits.cs
- Magick.NET upstream readme on default memory limits: https://github.com/dlemstra/Magick.NET/blob/main/docs/Readme.md
- waifu2x NCNN Vulkan CLI and built-in tiling: https://github.com/nihui/waifu2x-ncnn-vulkan
- ONNX Runtime DirectML EP notes: https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html
- Windows ML available EPs/offline considerations: https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers
- Epson ET-8500 Windows borderless expansion: https://download4.epson.biz/sec_pubs/et-8500_series/useg/en/GUID-89718614-F931-4C9C-A56C-38058798AE43.htm
- Windows RAW print data type: https://learn.microsoft.com/en-us/windows-hardware/drivers/print/raw-data-type

*This revision is an evidence-based architectural decision, not a report of native application builds or physical Epson measurements.*
