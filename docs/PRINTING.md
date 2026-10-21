# Printing, PDF geometry and calibration

`Mosaic.Core.LayoutEngine` owns the physical artwork rectangle, page orientation, usable tile coverage and row-major page order. `OutputGeometry.Describe` provides the same clipping rectangle and image translation to PDF and Windows printing. Source aspect ratio comes from immutable oriented source dimensions, rather than rounded preview/enhancement raster sizes.

## Export

`PdfExporter.ExportAsync` writes nominal physical geometry: Letter portrait is exactly 612 × 792 PDF points; landscape swaps the dimensions. One lossless normalized image XObject is reused with per-page clipping and translations. PDF interpolation is enabled. This avoids allocating a complete 60-inch poster raster or embedding the image once per sheet. Fit preserves the whole source and may leave internal blank bands; Crop and Stretch are explicit composition choices.

Export runs on a worker thread, checks cancellation between pages and before committing, writes to an adjacent temporary file, reopens the completed PDF, and atomically replaces the destination. Cancellation preserves an existing destination. PDFsharp's final file serialization is synchronous; cancellation during that phase is honored before replacement. Ordinary PDF export never receives measured printer compensation.

An optional final assembly guide is separate from the artwork. It contains page numbers, row/column order, exact outer extent, trim instructions and Actual size / 100% guidance in English or Polish. In margin mode, small corner marks lie outside the artwork clipping rectangle. Borderless artwork pages contain no labels or registration marks. Explicit grayscale output makes a lossless gray representation without changing the embedded original or project source; Canon selection alone leaves PDF color intact.

Export completion always includes localized Actual size / 100% instructions, including margin mode and exports without an assembly guide. Margin-specific trim/join guidance is appended to the scale instruction.

## Direct print

`PrintService.PrintAsync` uses the native Windows print dialog and XPS serialization. Each page uses physical WPF units (96 per inch), not screen pixels; bitmap interpolation is HighQuality. The ticket requests Letter, the shared resolved orientation, one-sided printing, 100% scaling, copies and color. Canon MF210 direct printing requests monochrome; nominal PDFs remain color unless grayscale was explicitly selected. With no saved printer, the Canon profile prefers an MF210 queue; the Epson profile prefers the OEM ET-8500 driver where available. Printer preference discovery runs in the background with cancellation and a 45-second timeout.

After the dialog, driver validation runs off the UI thread with cancellation and a 45-second response timeout. The adapter checks the validated orientation, scale, paper, borderless mode and printable area before submitting. It rejects changed geometry and offers a suggested uniform trim margin through `OutputException.SuggestedMarginInches`; it never shrinks the artwork to fit. Slow native driver operations cannot be forcibly interrupted internally, but the application can stop awaiting them. Native driver-dialog latency remains under Windows/driver control.

Only one submission can run at a time. XPS completion without an error means Windows accepted serialization to the print queue; the UI may then say **Sent to printer**. It does not establish that ink reached paper. Cancel requests cancel XPS serialization; sheets already spooled may need cancelling in the Windows printer queue.

## Calibration

`ExportCalibrationAsync` and `PrintCalibrationAsync` produce four sheets in a 2 × 2 arrangement using the same layout model. Every sheet includes independently measurable four-inch X/Y rulers with quarter-inch ticks, a one-inch global grid, diagonal crossing lines and concentric circles at the central page junction. Print the fixture with the exact intended paper, quality, borderless expansion and pathway; measure both rulers and inspect the assembled page edges. Repeat to assess feed drift and reproducibility.

Calibration records store printer, driver, profile, orientation, media notes, pathway, measurement date, observed clipping and measured X/Y lengths. A successful direct calibration handoff returns `PrintResult.Driver` and `DriverFingerprint`; save those with the record. `DriverFingerprint` is a hash of the validated PrintTicket (including vendor-specific features, excluding copies) plus the trim margin. `DriverSettings` retains the user's human-readable notes. PDF-viewer measurements remain a separate pathway and are not automatically applied to nominal PDF output.

Enabled direct-print correction requires the same printer, driver, profile, orientation and settings fingerprint. Corrections are reversible X/Y scale and offsets applied only in the print adapter, including the trim marks so cuts remain aligned with corrected artwork. Large corrections, known edge loss or corrections that would push artwork outside the actual printable area are rejected. Physical measurements still determine whether this correction is reliable. If the driver loses page-edge content or produces inconsistent feed, use a measured margin-and-trim workflow; a scalar factor cannot recover cropped content.

Calibration selection happens after the native dialog and driver validation, using the actual selected queue, even on the first print of a newly imported image. Exact settings matches take precedence over newer records for different settings; enabled records for the selected queue/profile/orientation with stale driver settings still cause a mismatch error. Calibration and artwork use the same profile-aware ticket builder, including Canon monochrome and explicit grayscale. Before fingerprinting, the driver revalidates a one-copy clone so copy count embedded in opaque IPP settings does not invalidate calibration; vendor media/expansion settings remain included.

## Verification performed

Run the focused output acceptance harness from the repository root:

```powershell
.\.tools\dotnet\dotnet.exe run --project tests/Mosaic.Output.Smoke -c Release
```

The harness creates real PDFs for 24-inch, 60-inch, fractional-margin, landscape-winning and manually oriented cases. Independent PdfPig inspection checks page boxes, count, image placement and adjacent translation. It checks one reusable XObject, WPF page parity and rasterization, actual XPS fixed-page serialization, Polish guide text and calibration labels, both calibration orientations, grayscale export and cancellation preserving user output. Artifacts are written to `artifacts/output-smoke/`.

The October 21 review-remediation run passed **238 checks** with `-- --printers`: the original geometry/XPS coverage, 24 additional calibration selection/safety/policy assertions, and 12 installed-driver ticket-parity checks. Epson OEM/IPP and Canon MF210 fingerprints matched artwork to calibration in portrait/landscape, default/explicit grayscale, and two artwork copies versus one calibration copy. These driver checks did not submit jobs. Earlier Poppler-rendered color, grayscale, assembly and calibration pages were visually inspected. The OEM Epson driver also validated borderless Letter tickets in both orientations and reported zero hard margins, with 816 × 1056 WPF units in portrait and swapped landscape dimensions. Driver validation is software evidence only. Add `-- --printers` to the command above to repeat these slower device-capability reads.

**No physical printer jobs or measurements were performed.** Unverified acceptance items include actual ET-8500 scale, seam crop and feed repeatability; Canon hardware printable area; direct-print versus PDF-viewer physical parity; and paper/media-specific compensation. No physical precision or seamless untrimmed join is claimed by these software checks.
