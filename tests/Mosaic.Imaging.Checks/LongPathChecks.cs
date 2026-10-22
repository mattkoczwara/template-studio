using System.IO;
using ImageMagick;
using ImageMagick.Drawing;
using Mosaic.Core;
using Mosaic.Imaging;
using Mosaic.Output;

internal static class LongPathChecks
{
    internal static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "long-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string input = Path.Combine(folder, "artwork.jpg");
        using (var image = new MagickImage(MagickColors.Ivory, 96, 64))
        {
            new Drawables().StrokeColor(MagickColors.DarkGreen).StrokeWidth(1.5).FillColor(MagickColors.None)
                .Circle(42, 32, 64, 32).Line(4, 58, 91, 5).Draw(image);
            image.Quality = 45; image.Write(input);
        }
        string cache = Path.Combine(folder, "cache with spaces", new string('a', 80), new string('b', 80));
        var images = new ImageService(cache);
        var source = await images.ImportAsync(input);
        if (source.NormalizedPath.Length <= 260) throw new Exception("Long-path fixture is not deep enough");
        string standard = await images.GetOutputImageAsync(source, 90);
        var standardPreview = await images.GetPreviewAsync(standard);
        var gpu = new EnhancementService(Path.GetFullPath("vendor/ai"));
        string enhanced = await gpu.EnhanceAsync(source, null, CancellationToken.None);
        if (gpu.LastBackend is not ("Vulkan GPU" or "CPU")) throw new Exception("Long-path automatic inference did not execute");
        string cpuCache = Path.Combine(cache, "cpu");
        var cpu = new EnhancementService(Path.GetFullPath("vendor/ai"), cpuCache);
        string cpuResult = await cpu.EnhanceAsync(source, null, CancellationToken.None, forceCpu: true);
        string rotated = await images.GetOutputImageAsync(source, 90, enhanced);
        var preview = await images.GetPreviewAsync(rotated);
        var detail = await images.GetInspectionRegionAsync(rotated, new(.25, .25, .5, .5));
        if (!preview.IsFrozen || !detail.IsFrozen || standardPreview.PixelWidth != 128 || preview.PixelWidth != 256)
            throw new Exception("Long-path rotated previews/inspection are incorrect");
        using var gpuInput = File.OpenRead(enhanced); using var cpuInput = File.OpenRead(cpuResult);
        using var a = new MagickImage(gpuInput); using var b = new MagickImage(cpuInput);
        double rmse = a.Compare(b, ErrorMetric.RootMeanSquared);
        if (rmse > .015) throw new Exception("Long-path GPU/CPU disagreement");
        var state = new ProjectState { Size = new(24, 36), Quality = QualityMode.Enhanced, Composition = new(Rotation: 90) };
        var snapshot = new RenderSnapshot(source, rotated, state, LayoutEngine.Calculate(state.Size, state.Paper));
        var pdf = await new PdfExporter().ExportAsync(snapshot, Path.Combine(folder, "deep-cache-output.pdf"));
        if (!File.Exists(pdf.Path)) throw new Exception("Long-path output PDF missing");
        File.WriteAllText(Path.Combine(root, "long-path-result.json"), System.Text.Json.JsonSerializer.Serialize(new
        { Passed = true, CachePathLength = source.NormalizedPath.Length, GpuBackend = gpu.LastBackend, CpuBackend = cpu.LastBackend,
            GpuCpuRmse = rmse, RotatedPreview = new { preview.PixelWidth, preview.PixelHeight }, PdfPages = pdf.PageCount },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: deep Windows paths with spaces; import, Standard, {gpu.LastBackend}/forced CPU, rotation, inspection and PDF.");
    }
}
