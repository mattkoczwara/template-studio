using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Drawing;
using Mosaic.Core;
using Mosaic.Imaging;
using Mosaic.Output;
using Reader = UglyToad.PdfPig.PdfDocument;

internal static class EnhancementOutputChecks
{
    // Runs on the smoke host's STA thread; async imaging/output operations own their background work.
    public static int Run(string output)
    {
        string folder = Path.Combine(output, "enhancement"); Directory.CreateDirectory(folder);
        string input = Path.GetFullPath("artifacts/quality/compressed-illustration-640/input.jpg");
        if (!File.Exists(input))
        {
            input = Path.Combine(folder, "input.jpg");
            using var fixture = new MagickImage(new MagickColor("#f6e7cc"), 96, 64);
            new Drawables().StrokeColor(new MagickColor("#284837")).StrokeWidth(1.5).FillColor(MagickColors.None)
                .Circle(37, 32, 58, 32).Line(3, 5, 89, 58)
                .Bezier(new PointD(2, 54), new PointD(33, 5), new PointD(59, 60), new PointD(94, 10)).Draw(fixture);
            fixture.Quality = 45; fixture.Write(input);
        }
        var images = new ImageService(Path.Combine(folder, "cache"));
        var source = images.ImportAsync(input).GetAwaiter().GetResult();
        var enhancer = new EnhancementService(Path.GetFullPath("vendor/ai"));
        string processed = enhancer.EnhanceAsync(source, null, CancellationToken.None).GetAwaiter().GetResult();
        string backend = enhancer.LastBackend;
        var selected = new Dictionary<QualityMode, string>
        {
            [QualityMode.Standard] = images.GetOutputImageAsync(source).GetAwaiter().GetResult(),
            [QualityMode.Enhanced] = images.GetOutputImageAsync(source, enhancedPath: processed).GetAwaiter().GetResult()
        };
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("Enhancement output: " + message); checks++; }
        void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < .001, message);
        var evidence = new List<object>();
        var printLoader = typeof(PrintService).GetMethod("LoadImageAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception("Direct print image loader missing");
        foreach (decimal width in new[] { 24m, 60m })
        {
            var size = LayoutEngine.Proportional(width, true, source.Width, source.Height);
            var layout = LayoutEngine.Calculate(size, new());
            foreach (var (quality, path) in selected)
            {
                var state = new ProjectState { Title = "Enhancement output " + quality, Size = size, Quality = quality };
                var snapshot = new RenderSnapshot(source, path, state, layout);
                using var pixels = new MagickImage(path);
                string name = $"{width}-{quality}";
                var result = new PdfExporter().ExportAsync(snapshot, Path.Combine(folder, name + ".pdf")).GetAwaiter().GetResult();
                using var pdf = Reader.Open(result.Path);
                Check(pdf.NumberOfPages == layout.PageCount, name + " PDF physical page count");
                foreach (var page in pdf.GetPages())
                {
                    Near(page.Width, (double)layout.PageWidth * 72, name + " PDF Letter width");
                    Near(page.Height, (double)layout.PageHeight * 72, name + " PDF Letter height");
                }
                var embedded = pdf.GetPage(1).GetImages().Single();
                Check(embedded.WidthInSamples == pixels.Width && embedded.HeightInSamples == pixels.Height, name + " embedded selected asset dimensions");
                Check(embedded.TryGetPng(out var png), name + " independently extractable lossless PDF image");
                double pdfPixelRmse;
                using (var actual = new MagickImage(png!))
                {
                    pdfPixelRmse = actual.Compare(pixels, ErrorMetric.RootMeanSquared);
                    Check(pdfPixelRmse < .000001, name + " PDF embeds exact selected pixels");
                }
                File.WriteAllBytes(Path.Combine(folder, name + "-embedded.png"), png!);
                Near(embedded.BoundingBox.Width, (double)size.WidthInches * 72, name + " PDF exact artwork width");
                Near(embedded.BoundingBox.Height, (double)size.HeightInches * 72, name + " PDF exact artwork height");
                if (layout.Columns > 1)
                {
                    var next = pdf.GetPage(2).GetImages().Single();
                    Near(next.BoundingBox.Left - embedded.BoundingBox.Left, -(double)layout.TileWidth * 72, name + " PDF shared seam coordinates");
                }

                // Invoke the same file loader used by PrintAsync, without contacting or submitting to a printer.
                var bitmap = ((Task<BitmapSource>)printLoader.Invoke(null, new object[] { snapshot.ImagePath, false, CancellationToken.None })!)
                    .GetAwaiter().GetResult();
                Check(bitmap.IsFrozen && bitmap.PixelWidth == pixels.Width && bitmap.PixelHeight == pixels.Height, name + " direct loader selected asset");
                double printPixelRmse;
                using (var stream = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
                    using var actual = new MagickImage(stream.ToArray());
                    printPixelRmse = actual.Compare(pixels, ErrorMetric.RootMeanSquared);
                    Check(printPixelRmse < .000001, name + " direct loader decodes exact selected pixels");
                }
                var paginator = PrintService.CreatePaginator(snapshot, bitmap);
                Check(paginator.PageCount == layout.PageCount, name + " direct paginator physical page count");
                Near(paginator.PageSize.Width, (double)layout.PageWidth * 96, name + " direct Letter width");
                Near(paginator.PageSize.Height, (double)layout.PageHeight * 96, name + " direct Letter height");
                using var first = paginator.GetPage(0);
                var drawing = FindImage(((DrawingVisual)first.Visual).Drawing).Single();
                Check(ReferenceEquals(drawing.ImageSource, bitmap), name + " direct page uses actual selected bitmap");
                Near(drawing.Rect.Width, (double)size.WidthInches * 96, name + " direct exact artwork width");
                Near(drawing.Rect.Height, (double)size.HeightInches * 96, name + " direct exact artwork height");
                if (layout.Columns > 1)
                {
                    using var next = paginator.GetPage(1);
                    var nextDrawing = FindImage(((DrawingVisual)next.Visual).Drawing).Single();
                    Check(ReferenceEquals(nextDrawing.ImageSource, bitmap), name + " direct next page reuses selected bitmap");
                    Near(nextDrawing.Rect.X - drawing.Rect.X, -(double)layout.TileWidth * 96, name + " direct shared seam coordinates");
                }
                evidence.Add(new { Quality = quality.ToString(), WidthInches = width, HeightInches = size.HeightInches, Pages = layout.PageCount,
                    PixelWidth = pixels.Width, PixelHeight = pixels.Height, Pdf = result.Path, PdfPixelRmse = pdfPixelRmse, PrintPixelRmse = printPixelRmse, PrinterJobSubmitted = false });
            }
        }
        using (var standard = new MagickImage(selected[QualityMode.Standard]))
        using (var enhanced = new MagickImage(selected[QualityMode.Enhanced]))
        {
            standard.FilterType = FilterType.Lanczos; standard.Resize(enhanced.Width, enhanced.Height);
            Check(standard.Compare(enhanced, ErrorMetric.RootMeanSquared) > .0001, "selected output assets contain distinct content");
        }
        File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks, Backend = backend,
            SourceWidth = source.Width, SourceHeight = source.Height, Cases = evidence, PhysicalPrintVerified = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {checks} selected enhancement PDF/direct-paginator checks; 24/60-inch geometry; independently extracted PDF pixels; no printer job submitted.");
        return checks;
    }

    private static IEnumerable<ImageDrawing> FindImage(Drawing drawing)
    {
        if (drawing is ImageDrawing image) yield return image;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var found in FindImage(child)) yield return found;
    }
}
