using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Drawing;
using Mosaic.Imaging;

internal static class SelectionChecks
{
    public static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Output selection: " + message);
            checks++;
        }
        var images = new ImageService(Path.Combine(folder, "cache"));
        string fixture = Path.Combine(folder, "linework.png");
        using (var image = new MagickImage(new MagickColor("#f6e7cc"), 96, 64))
        {
            new Drawables().StrokeColor(new MagickColor("#284837")).StrokeWidth(1.5).FillColor(MagickColors.None)
                .Circle(37, 32, 58, 32).Line(3, 5, 89, 58)
                .Bezier(new PointD(2, 54), new PointD(33, 5), new PointD(59, 60), new PointD(94, 10)).Draw(image);
            image.Write(fixture);
        }
        string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)));
        var source = await images.ImportAsync(fixture);
        Check(EnhancementService.ModelId == new Mosaic.Core.ProjectState().EnhancementModel, "default project recipe differs from inference recipe");
        Check(EnhancementService.OutputScale(source with { Width = 2000, Height = 2000 }) == 4 &&
            EnhancementService.OutputScale(source with { Width = 2001, Height = 2000 }) == 2 &&
            EnhancementService.OutputScale(source with { Width = 7500, Height = 10 }) == 4 &&
            EnhancementService.OutputScale(source with { Width = 7501, Height = 10 }) == 2,
            "bounded 4x selection does not enforce output pixel/edge limits");
        string standard = await images.GetOutputImageAsync(source);
        var enhancer = new EnhancementService(Path.GetFullPath("vendor/ai"));
        string enhanced = await enhancer.EnhanceAsync(source, null, CancellationToken.None, forceCpu: true);
        using var aiImage = new MagickImage(enhanced);
        using var standardImage = new MagickImage(standard);
        standardImage.FilterType = FilterType.Lanczos;
        standardImage.Resize(aiImage.Width, aiImage.Height);
        double difference = standardImage.Compare(aiImage, ErrorMetric.RootMeanSquared);
        Check(difference > .0001, "actual inference did not produce distinct pixels from Lanczos");
        Check(await images.GetOutputImageAsync(source, enhancedPath: enhanced) == enhanced, "unrotated output did not select exact AI asset");
        Check(await enhancer.EnhanceAsync(source, null, CancellationToken.None) == enhanced && enhancer.LastBackend == "cache" && enhancer.LastWorkerPeakBytes == 0, "AI cache did not reuse verified result with fresh diagnostics");
        // Same-sized valid PNG bytes must not bypass the cache's content checksum.
        using (var replacement = new MagickImage(MagickColors.Magenta, aiImage.Width, aiImage.Height)) replacement.Write(enhanced);
        string repairedAi = await enhancer.EnhanceAsync(source, null, CancellationToken.None, forceCpu: true);
        using (var repairedImage = new MagickImage(repairedAi))
            Check(repairedImage.Compare(aiImage, ErrorMetric.RootMeanSquared) < .000001, "tampered AI cache was reused instead of regenerated");

        string variantA = Path.Combine(folder, "red.png"), variantB = Path.Combine(folder, "blue.png");
        WriteVerified(variantA, MagickColors.Red, 192, 128);
        WriteVerified(variantB, MagickColors.Blue, 384, 256);
        string rotatedA = await images.GetOutputImageAsync(source, 90, variantA);
        string rotatedB = await images.GetOutputImageAsync(source, 90, variantB);
        Check(rotatedA != rotatedB, "rotated caches alias different processed images");
        using (var a = new MagickImage(rotatedA))
        using (var b = new MagickImage(rotatedB))
        {
            Check(a.Width == 128 && a.Height == 192 && b.Width == 256 && b.Height == 384, "rotation did not preserve selected pixel dimensions");
            using var pa = a.GetPixels(); using var pb = b.GetPixels();
            var ca = pa.GetPixel(0, 0).ToColor()!; var cb = pb.GetPixel(0, 0).ToColor()!;
            Check(ca.R == 255 && ca.B == 0 && cb.R == 0 && cb.B == 255, "rotated output reused stale pixels");
        }
        // Replacing a checksum-valid asset at the same path must also invalidate its rotation.
        WriteVerified(variantA, MagickColors.Green, 192, 128);
        string replaced = await images.GetOutputImageAsync(source, 90, variantA);
        Check(replaced != rotatedA, "replaced processed pixels reused prior rotated cache");

        string regionsA = Path.Combine(folder, "regions-2x.png"), regionsB = Path.Combine(folder, "regions-4x.png");
        WriteRegions(regionsA, 192, 128); WriteRegions(regionsB, 384, 256);
        var region = new Rect(.125, .25, .25, .5);
        var detailA = await images.GetInspectionRegionAsync(regionsA, region, maxEdge: 64);
        var detailB = await images.GetInspectionRegionAsync(regionsB, region, maxEdge: 64);
        Check(detailA.IsFrozen && detailB.IsFrozen, "inspection assets are not frozen");
        Check(detailA.PixelWidth == 48 && detailA.PixelHeight == 64 && detailB.PixelWidth == 48 && detailB.PixelHeight == 64,
            "same normalized region is not shown at matched scale with bounded output");
        Check(ColorAt(detailA, 20, 10) == (255, 0, 0) && ColorAt(detailB, 20, 10) == (255, 0, 0), "off-center upper region is not the selected red artwork");
        Check(ColorAt(detailA, 20, 50) == (0, 0, 255) && ColorAt(detailB, 20, 50) == (0, 0, 255), "off-center lower region is not the selected blue artwork");
        var edge = await images.GetInspectionRegionAsync(regionsB, new Rect(.75, .75, .25, .25), maxEdge: 2048);
        Check(edge.PixelWidth == 96 && edge.PixelHeight == 64 && ColorAt(edge, 40, 20) == (255, 255, 0), "edge region was shifted or unnecessarily upscaled");
        bool invalidRejected = false;
        try { await images.GetInspectionRegionAsync(regionsA, new Rect(.9, .9, .2, .2)); }
        catch (ArgumentOutOfRangeException) { invalidRejected = true; }
        Check(invalidRejected, "out-of-image region accepted");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancellationObserved = false;
        try { await images.GetInspectionRegionAsync(regionsA, region, ct: cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved, "inspection ignored cancellation");
        File.WriteAllText(regionsA + ".sha256", new string('0', 64));
        bool corruptRejected = false;
        try { await images.GetInspectionRegionAsync(regionsA, region); }
        catch (ImagePipelineException) { corruptRejected = true; }
        Check(corruptRejected, "inspection accepted unverified output asset");
        byte[] normalized = File.ReadAllBytes(source.NormalizedPath);
        bool normalizedRejected = false;
        try
        {
            using (var replacement = new MagickImage(MagickColors.White, (uint)source.Width, (uint)source.Height)) replacement.Write(source.NormalizedPath);
            foreach (int rotation in new[] { 0, 180 })
            {
                bool standardRejected = false;
                try { await images.GetOutputImageAsync(source, rotation); }
                catch (ImagePipelineException ex) when (ex.Code == "imageReadFailed") { standardRejected = true; }
                Check(standardRejected, $"Standard rotation {rotation} accepted tampered normalized input");
            }
            try { await enhancer.EnhanceAsync(source, null, CancellationToken.None, forceCpu: true); }
            catch (EnhancementException ex) when (ex.Code == "aiFailed") { normalizedRejected = true; }
        }
        finally { File.WriteAllBytes(source.NormalizedPath, normalized); }
        Check(normalizedRejected, "enhancement accepted tampered normalized input even with an existing AI cache");
        string cancelledCache = Path.Combine(folder, "second-pass");
        var cancelEnhancer = new EnhancementService(Path.GetFullPath("vendor/ai"), cancelledCache);
        using var secondPassCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        bool secondPassObserved = false, cancelledInSecondPass = false;
        var secondPassProgress = new InlineProgress(p =>
        {
            if (p.Fraction > .5 && p.Fraction < 1)
            { secondPassObserved = true; secondPassCancellation.Cancel(); }
        });
        try { await cancelEnhancer.EnhanceAsync(source, secondPassProgress, secondPassCancellation.Token, forceCpu: true); }
        catch (OperationCanceledException) { cancelledInSecondPass = true; }
        Check(secondPassObserved && cancelledInSecondPass, "second inference pass did not honor cancellation");
        Check(!Directory.EnumerateFiles(cancelledCache, "*", SearchOption.AllDirectories)
            .Any(path => Path.GetExtension(path) is ".png" or ".rgb" or ".sha256"), "second-pass cancellation leaked intermediate or published output");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))) == originalHash, "processing changed original bytes");
        var summary = new { Passed = true, Checks = checks, StandardEnhancedRmse = difference, Backend = "CPU", Standard = standard, Enhanced = enhanced,
            Rotated = new[] { rotatedA, rotatedB, replaced }, MatchedRegion = new { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height }, PhysicalGeometryChanged = false };
        File.WriteAllText(Path.Combine(root, "selection-result.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {checks} output/inspection checks; actual AI/Lanczos RMSE {difference:0.000000}; rotated recipe invalidation and normalized matched regions.");
    }

    private static void WriteVerified(string path, MagickColor color, uint width, uint height)
    {
        using var image = new MagickImage(color, width, height); image.Write(path); Checksum(path);
    }
    private static void WriteRegions(string path, uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.Red, width, height);
        new Drawables().FillColor(MagickColors.Green).Rectangle(width / 2, 0, width, height / 2 - 1)
            .FillColor(MagickColors.Blue).Rectangle(0, height / 2, width / 2 - 1, height)
            .FillColor(MagickColors.Yellow).Rectangle(width / 2, height / 2, width, height).Draw(image);
        image.Write(path); Checksum(path);
    }
    private static void Checksum(string path) => File.WriteAllText(path + ".sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
    private static (byte R, byte G, byte B) ColorAt(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4]; converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]);
    }
}
