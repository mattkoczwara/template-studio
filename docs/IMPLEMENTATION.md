# Implemented architecture

The application is a native WPF/.NET 10 Windows x64 modular monolith. `Mosaic.Core` owns all decimal physical geometry, portrait/landscape comparison and image placement. `Mosaic.Imaging` retains immutable sources, normalizes images and runs isolated offline enhancement workers. `Mosaic.Output` writes precise PDFs and Windows XPS print jobs from the same immutable snapshot. `Mosaic.Storage` owns portable ZIP projects and local preferences. `Mosaic.App` provides the bilingual editor and coordinates jobs.

## Decisions against the architecture recommendation

| Area | Implemented choice and evidence |
|---|---|
| UI | Bespoke WPF XAML tokens and native controls; no WPF-UI or MVVM toolkit dependency. The single small editor coordinates explicit immutable state and async services. This reduces dependency and deployment surface while retaining WPF accessibility, keyboard controls, responsive background processing and readable styles. The actual EN/PL views are rendered by integrated dispatcher checks. |
| Physical units | Exact `decimal` inches rather than decimal mm. Inches are the binding user unit; 8.5/11 paper dimensions and the 72-point/96-DIP output conversion are exact. There is no conversion or page-count rounding back into artwork size. Source aspect ratios govern every adapter, including downsampled previews. |
| PDF | PDFsharp 6.2.4 with one lossless source XObject reused and clipped across pages, conventional Lanczos preprocessing and explicit interpolation. Independent PdfPig checks and XPS serialization establish software dimensions and translations; memory stays independent of the physical poster raster size. Grayscale is opt-in for PDF. |
| AI | Official NCNN Vulkan CUNet worker plus a small isolated NCNN CPU adapter. The advertised upstream CPU mode actually crashed here; the tested CPU adapter uses the same model and padded inference tiles. Full rationale, limits, licenses and measured GPU/CPU results are in [AI.md](AI.md). |
| Logging | A small bounded local stage/error-type log replaces Serilog. Five retained daily files, a 1 MiB daily bound and privacy-safe job stages meet this app's diagnostics needs without another hosting framework. Logs never contain artwork, complete paths or personal filenames. |
| Projects | Atomic, streamed ZIP source/manifest/thumbnail with checksum/size/schema validation. The store exposes lazy metadata, while editing awaits a bounded full-resolution import. Derived AI images are regenerated rather than duplicated in portable projects. |
| Localization | Paired English/Polish dictionary resources replace `.resx`; one resource update changes controls immediately. String-backed measurements accept partial typing and selected-culture decimals. Actual text edits, rather than focus changes, govern committing; rounded complementary displays cannot alter exact dimensions. |
| Packaging | Pinned .NET10.0.13 self-contained untrimmed/non-AOT folder, Inno Setup7.1 per-user installer, bundled runtime/model licenses and native dependencies. Signing and clean-machine acceptance remain explicit release boundaries. |

## Correctness and failure behavior

The editor uses the last valid geometry while a dimension is being edited. A pending invalid dimension disables output and cannot be erased by a valid margin edit or bypassed with keyboard printing. Re-enabling proportions preserves the last actual user-edited dimension. All tiles are row-major disjoint rectangles; partial edge pages retain blank outer paper for trimming. Preview grid labels never enter borderless output.

Project-menu Undo/Redo and keyboard shortcuts retain up to fifty editing snapshots without duplicating source imagery. Original/enhanced comparison opens an independent detail view; canceling a quality-preview change retains the previous recipe and bitmap. Export completion retains its output snapshot when the interface language changes. All application windows fit within the current Windows work area.

Image decode, resampling, PDF generation, project I/O and inference run asynchronously with cancellation and explicit limits. Workers run directly from trusted pinned paths with no shell or network. A canceled project open commits no partial state. AI failure keeps Standard available. Export and project writes replace destinations only after successful completion. One print submission is allowed at a time; native driver settings are validated without recalculating geometry, and scale-to-fit tickets are rejected. Calibration is tied to the exact printer, driver, orientation, pathway and validated ticket fingerprint; nominal PDFs are never compensated silently.

Recovery stores a separate self-contained project at most once per editing revision/interval. There is one in-flight recovery writer; normal close cancels and awaits it before removing recovery after an explicit save/discard choice. Settings and logs stay under `%LocalAppData%/MosaicTemplateStudio`. No automatic updater or telemetry is implemented.

## Verification limits

The five focused executable test harnesses check pure geometry, malformed ZIP/image recovery, exact PDF/XPS sizes and translations, real GPU and CPU inference and the actual WPF editor operation paths. Rendered UI views at 100/125/150% output DPI are software evidence, not an assertion of actual Windows scaling or independent usability. Native Windows screen capture timed out after rebinding and activation in this run. Physical Epson/Canon output, target-PC hardware, mother-sourced artwork fidelity, fluent Polish wording and clean offline Windows installation remain as recorded in [ACCEPTANCE.md](ACCEPTANCE.md).

The installed executable supports `--verify-install <report-directory>` for the distribution check. It opens the real editor with isolated local data, runs the same import/sizing/AI/comparison/export/project/recovery checks using bundled dependencies and writes `installed-runtime.json` before exiting. This provides an installed-host runtime journey without development tools; it does not pretend to be a separate clean offline machine.
