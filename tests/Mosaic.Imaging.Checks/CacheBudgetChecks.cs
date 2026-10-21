using System.IO;
using System.Security.Cryptography;
using ImageMagick;
using Mosaic.Imaging;

internal static class CacheBudgetChecks
{
    public static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "budget-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(folder, "cache");
        Directory.CreateDirectory(folder);
        string fixture = Path.Combine(folder, "noise.png");
        using (var image = new MagickImage(MagickColors.White, 80, 60))
        {
            image.AddNoise(NoiseType.Random);
            image.Write(fixture);
        }
        const long limit = 8L * 1024 * 1024;
        var images = new ImageService(cache, limit);
        var source = await images.ImportAsync(fixture);
        string standard = await images.GetOutputImageAsync(source);
        byte[] original = await File.ReadAllBytesAsync(source.OriginalPath);
        string padding = Path.Combine(cache, "padding.bin");
        void Fill()
        {
            File.Delete(padding);
            using var file = File.Create(padding);
            file.SetLength(limit - Used(cache) - 64);
        }
        async Task Rejected(Func<Task> action, string label)
        {
            bool rejected = false;
            try { await action(); }
            catch (ImagePipelineException ex) when (ex.Code == "cacheFull") { rejected = true; }
            catch (EnhancementException ex) when (ex.Code == "cacheFull") { rejected = true; }
            Check(rejected, label + " must reject allocation");
            Check(Used(cache) <= limit, label + " grew cache past budget");
            Check(!Directory.EnumerateFiles(cache, "*.tmp", SearchOption.AllDirectories).Any(), label + " leaked temp output");
        }
        Fill();
        var reused = await images.ImportAsync(fixture);
        Check(reused.Sha256 == source.Sha256, "near-cap import reuse");
        Check(await images.GetOutputImageAsync(source) == standard, "near-cap standard reuse");
        await Rejected(async () => { await images.GetOutputImageAsync(source, 90); }, "rotation");
        var enhancer = new EnhancementService(Path.GetFullPath("vendor/ai"), maxCacheBytes: limit);
        await Rejected(async () => { await enhancer.EnhanceAsync(source, null, CancellationToken.None, true); }, "enhancement");
        File.Delete(padding);
        string enhanced = await enhancer.EnhanceAsync(source, null, CancellationToken.None, true);
        Fill();
        Check(await enhancer.EnhanceAsync(source, null, CancellationToken.None) == enhanced && enhancer.LastBackend == "cache", "near-cap enhancement reuse");
        await Rejected(async () => { await images.CreateDetailComparisonAsync(source, enhanced); }, "comparison");
        byte[] retainedOriginal = await File.ReadAllBytesAsync(source.OriginalPath);
        Check(original.SequenceEqual(retainedOriginal), "budget failure changed immutable source");
        File.Delete(padding);

        // Separate service instances must serialize admissions, including a cancellable waiter.
        var second = new ImageService(cache, limit);
        using (var lease = new FileStream(Path.Combine(cache, ".budget.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        using (var cancel = new CancellationTokenSource(150))
        {
            bool canceled = false;
            try { await second.GetOutputImageAsync(source, 90, ct: cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "cache lock wait cancellation");
        }
        string rotated = await second.GetOutputImageAsync(source, 90);
        Check(File.Exists(rotated), "cancellation did not release service gate");
        await images.CreateDetailComparisonAsync(source, enhanced);
        images.CleanDerivedCache(TimeSpan.Zero);
        Check(!File.Exists(standard) && !File.Exists(standard + ".sha256") && !File.Exists(rotated), "derived cleanup retained disposable output");
        Check(File.Exists(source.OriginalPath) && File.Exists(source.NormalizedPath) && File.Exists(source.PreviewPath) && File.Exists(enhanced), "derived cleanup removed source/active enhancement");
        await images.GetOutputImageAsync(source);

        // Admission is serialized across competing writers; only one new allocation fits.
        long oneWrite = 160L * 120 * 4 + 1024 * 1024 + 128;
        long concurrentLimit = Used(cache) + oneWrite;
        var writerA = new ImageService(cache, concurrentLimit);
        var writerB = new ImageService(cache, concurrentLimit);
        var tasks = new[] { writerA.GetOutputImageAsync(source, 90), writerB.GetOutputImageAsync(source, 270) };
        int completed = 0, full = 0;
        foreach (var task in tasks)
        {
            try { await task; completed++; }
            catch (ImagePipelineException ex) when (ex.Code == "cacheFull") { full++; }
        }
        Check(completed == 1 && full == 1 && Used(cache) <= concurrentLimit, "concurrent derived budget admission");
        await images.ClearCacheAsync(source.Sha256);
        Check(File.Exists(source.OriginalPath) && File.Exists(source.NormalizedPath) && File.Exists(enhanced), "explicit cleanup removed active source assets");
        var sourceLeaseA = await images.PreserveSourceAsync(source.Sha256);
        var sourceLeaseB = await second.PreserveSourceAsync(source.Sha256);
        await second.ClearCacheAsync(null);
        images.CleanDerivedCache(TimeSpan.Zero);
        Check(File.Exists(source.OriginalPath) && File.Exists(standard) && File.Exists(enhanced), "other-window cleanup removed leased assets");
        sourceLeaseA.Dispose();
        await images.ClearCacheAsync(null);
        Check(File.Exists(source.OriginalPath), "first release removed another window's leased source");
        sourceLeaseB.Dispose();
        await images.ClearCacheAsync(null);
        Check(!File.Exists(source.OriginalPath) && !Directory.Exists(Path.GetDirectoryName(source.NormalizedPath)!), "released source was not eligible for explicit cleanup");
        Console.WriteLine("Cache budget checks passed: near-cap import/output/AI reuse; rotation/comparison/AI admission; original preservation; cancellation; cleanup; concurrent writers; shared source leases.");
    }

    private static long Used(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
