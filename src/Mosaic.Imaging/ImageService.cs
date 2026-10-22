using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using ImageMagick;
using Mosaic.Core;

namespace Mosaic.Imaging;

public sealed class ImagePipelineException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}

/// <summary>Immutable source import and bounded, disk-backed derived images. Never writes user input paths.</summary>
public sealed class ImageService
{
    public const long MaxSourceBytes = 128L * 1024 * 1024;
    public const long MaxSourcePixels = 80_000_000;
    private readonly string _cache;
    private readonly CacheBudget _budget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static int _configured;

    public ImageService(string? cacheRoot = null, long maxCacheBytes = CacheBudget.DefaultLimit)
    {
        _cache = Path.GetFullPath(cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosaicTemplateStudio", "cache"));
        Directory.CreateDirectory(_cache);
        _budget = new CacheBudget(_cache, maxCacheBytes);
        if (Interlocked.Exchange(ref _configured, 1) == 0)
        {
            // Magick.NET 15 does not expose Map; ImageMagick honors this process-local limit.
            Environment.SetEnvironmentVariable("MAGICK_MAP_LIMIT", "256MiB");
            ResourceLimits.Memory = 256UL * 1024 * 1024;
            ResourceLimits.Disk = 2UL * 1024 * 1024 * 1024;
            ResourceLimits.Width = 30_000;
            ResourceLimits.Height = 30_000;
            ResourceLimits.Area = 40_000_000;
            ResourceLimits.Thread = (ulong)Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            ResourceLimits.Time = 180;
            ResourceLimits.ListLength = 2;
            ResourceLimits.MaxProfileSize = 16UL * 1024 * 1024;
        }
    }

    public Task<SourceInfo> ImportAsync(string path, CancellationToken ct = default) => RunAsync(() => Import(path, ct), ct);

    private SourceInfo Import(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var format = ext switch { ".jpg" or ".jpeg" => MagickFormat.Jpeg, ".png" => MagickFormat.Png,
            ".webp" => MagickFormat.WebP, ".bmp" => MagickFormat.Bmp, _ => throw new ImagePipelineException("unsupportedImage") };
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > MaxSourceBytes) throw new ImagePipelineException("imageTooLarge");
        ct.ThrowIfCancellationRequested();
        _budget.EnsureDiskSpace(file.Length * 6 + 256L * 1024 * 1024);
        string hash;
        using (var input = File.OpenRead(path)) hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        var dir = Path.Combine(_cache, hash);
        Directory.CreateDirectory(dir);
        var original = Path.Combine(dir, "original" + ext);
        if (!File.Exists(original)) AtomicCopy(path, original, ct, _budget);
        using (var verify = File.OpenRead(original))
            if (!Convert.ToHexString(SHA256.HashData(verify)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new ImagePipelineException("imageReadFailed");
        var normalized = Path.Combine(dir, "normalized.png");
        var preview = Path.Combine(dir, "preview.png");
        try
        {
            using var image = new MagickImage();
            image.Progress += (_, e) => e.Cancel = ct.IsCancellationRequested;
            using var originalStream = File.OpenRead(original);
            image.Ping(originalStream, new MagickReadSettings { Format = format, FrameCount = 1 });
            ValidateDimensions(image.Width, image.Height);
            originalStream.Position = 0;
            image.Read(originalStream, new MagickReadSettings { Format = format, FrameCount = 1 });
            ct.ThrowIfCancellationRequested();
            image.AutoOrient();
            Normalize(image);
            int width = checked((int)image.Width), height = checked((int)image.Height);
            if (!IsVerifiedImage(normalized, width, height)) WriteAtomic(image, normalized, ct, _budget);
            if (!IsVerifiedImage(preview))
            {
                image.FilterType = FilterType.Lanczos;
                image.Resize(new MagickGeometry(1800, 1800) { Greater = true });
                WriteAtomic(image, preview, ct, _budget);
            }
            return new(original, normalized, hash, width, height, format.ToString(), file.Length, preview);
        }
        catch (OperationCanceledException) { throw; }
        catch (ImagePipelineException) { throw; }
        catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            throw new ImagePipelineException("imageReadFailed", e);
        }
    }

    public Task<BitmapSource> GetPreviewAsync(string path, int rotation = 0, int maxEdge = 1800, CancellationToken ct = default) => RunAsync(() =>
    {
        if (maxEdge < 64 || maxEdge > 4096) throw new ArgumentOutOfRangeException(nameof(maxEdge));
        using var image = ReadImage(path);
        image.Progress += (_, e) => e.Cancel = ct.IsCancellationRequested;
        Rotate(image, rotation);
        image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry((uint)maxEdge, (uint)maxEdge) { Greater = true });
        ct.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(image.ToByteArray(MagickFormat.Png));
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return (BitmapSource)bitmap;
    }, ct);

    /// <summary>Loads a bounded detail from an output asset, without first reducing the whole image.
    /// Coordinates are normalized so unequal Standard/AI pixel dimensions inspect the same artwork.</summary>
    public Task<BitmapSource> GetInspectionRegionAsync(string path, System.Windows.Rect region, int maxEdge = 2048, CancellationToken ct = default) => RunAsync(() =>
    {
        if (maxEdge < 64 || maxEdge > 4096 || region.IsEmpty ||
            !double.IsFinite(region.X + region.Y + region.Width + region.Height) ||
            region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 || region.Right > 1.000001 || region.Bottom > 1.000001)
            throw new ArgumentOutOfRangeException(nameof(region));
        if (!IsVerifiedImage(path)) throw new ImagePipelineException("imageReadFailed");
        using var image = ReadImage(path);
        image.Progress += (_, e) => e.Cancel = ct.IsCancellationRequested;
        int x = Math.Clamp((int)Math.Round(region.X * image.Width), 0, (int)image.Width - 1);
        int y = Math.Clamp((int)Math.Round(region.Y * image.Height), 0, (int)image.Height - 1);
        int right = Math.Clamp((int)Math.Round(region.Right * image.Width), x + 1, (int)image.Width);
        int bottom = Math.Clamp((int)Math.Round(region.Bottom * image.Height), y + 1, (int)image.Height);
        image.Crop(new MagickGeometry(x, y, (uint)(right - x), (uint)(bottom - y))); image.ResetPage();
        image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry((uint)maxEdge, (uint)maxEdge) { Greater = true });
        ct.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(image.ToByteArray(MagickFormat.Png));
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return (BitmapSource)bitmap;
    }, ct);

    /// <summary>Creates a coherent, rotated lossless image; placement and physical dimensions remain in LayoutEngine.</summary>
    public Task<string> GetOutputImageAsync(SourceInfo source, int rotation = 0, string? enhancedPath = null, CancellationToken ct = default) => RunAsync(() =>
    {
        // Standard and Enhanced must share the same immutable, verified source.
        // Check before cache reuse as well as before generating a fresh rotation.
        if (!IsVerifiedImage(source.NormalizedPath, source.Width, source.Height))
            throw new ImagePipelineException("imageReadFailed");
        var input = enhancedPath ?? source.NormalizedPath;
        if (enhancedPath != null && !IsVerifiedImage(enhancedPath)) throw new ImagePipelineException("imageReadFailed");
        if (rotation % 360 == 0 && enhancedPath != null) return input;
        var name = enhancedPath == null ? "standard" : "enhanced";
        // Rotation variants belong to the processed pixels, not just the source.
        // A changed recipe must never reuse another enhancement's rotated cache.
        string identity = enhancedPath == null ? "v1" : File.ReadAllText(enhancedPath + ".sha256").Trim().ToLowerInvariant()[..32];
        var output = Path.Combine(Path.GetDirectoryName(source.NormalizedPath)!, $"{name}-r{((rotation % 360) + 360) % 360}-{identity}.png");
        if (IsVerifiedImage(output)) return output;
        using var image = ReadImage(input);
        image.Progress += (_, e) => e.Cancel = ct.IsCancellationRequested;
        Rotate(image, rotation);
        if (enhancedPath == null)
        {
            // Conventional enlargement uses Lanczos, never a nearest-neighbour screen preview.
            // Cap this reusable asset independently of a 60-inch canvas, avoiding giant poster bitmaps.
            double scale = Math.Min(2.0, Math.Min(8192.0 / Math.Max(image.Width, image.Height), Math.Sqrt(32_000_000.0 / ((double)image.Width * image.Height))));
            if (scale > 1.01)
            {
                image.FilterType = FilterType.Lanczos;
                image.Resize((uint)Math.Round(image.Width * scale), (uint)Math.Round(image.Height * scale));
            }
        }
        WriteAtomic(image, output, ct, _budget);
        return output;
    }, ct);

    public Task<string> CreateDetailComparisonAsync(SourceInfo source, string enhancedPath, CancellationToken ct = default) => RunAsync(() =>
    {
        string output = Path.Combine(Path.GetDirectoryName(source.NormalizedPath)!, "comparison.png");
        using var original = ReadImage(source.NormalizedPath);
        using var enhanced = ReadImage(enhancedPath);
        int size = (int)Math.Min(256, Math.Min(original.Width, original.Height));
        var crop = new MagickGeometry(((int)original.Width - size) / 2, ((int)original.Height - size) / 2, (uint)size, (uint)size);
        original.Crop(crop); original.ResetPage();
        original.FilterType = FilterType.Lanczos; original.Resize(512, 512);
        double sx = (double)enhanced.Width / source.Width, sy = (double)enhanced.Height / source.Height;
        enhanced.Crop(new MagickGeometry((int)Math.Round(crop.X * sx), (int)Math.Round(crop.Y * sy), (uint)Math.Round(size * sx), (uint)Math.Round(size * sy))); enhanced.ResetPage();
        enhanced.Resize(512, 512);
        using var collection = new MagickImageCollection { original.Clone(), enhanced.Clone() };
        using var joined = collection.AppendHorizontally();
        WriteAtomic(joined, output, ct, _budget);
        return output;
    }, ct);

    /// <summary>Retains all assets of a document while any window/process holds its lease.</summary>
    public async Task<IDisposable> PreserveSourceAsync(string sourceHash, CancellationToken ct = default)
    {
        if (!IsSourceHash(sourceHash)) throw new ArgumentException("Invalid source identity.", nameof(sourceHash));
        using var cacheLease = await _budget.AcquireAsync(ct).ConfigureAwait(false);
        if (!Directory.Exists(Path.Combine(_cache, sourceHash))) throw new ImagePipelineException("imageReadFailed");
        // Sidecars live outside source folders, so cleanup never removes the lock's
        // identity. Shared handles let several windows retain the same source.
        return new FileStream(SourceLeasePath(sourceHash), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
    }

    public void CleanDerivedCache(TimeSpan maxAge)
    {
        // Explicit cleanup only. Pinned documents retain every asset. For other
        // sources remove disposable output variants, never normalized/source/AI data.
        using var cacheLease = _budget.AcquireAsync(CancellationToken.None).GetAwaiter().GetResult();
        foreach (var folder in Directory.EnumerateDirectories(_cache))
        {
            string hash = Path.GetFileName(folder);
            if (!IsSourceHash(hash) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
            using var sourceLease = TryClaimSourceCleanup(hash);
            if (sourceLease == null) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.png"))
            {
                string name = Path.GetFileName(file);
                if ((name.StartsWith("standard-r", StringComparison.Ordinal) || name.StartsWith("enhanced-r", StringComparison.Ordinal) || name == "comparison.png") &&
                    File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - maxAge)
                    try { File.Delete(file); File.Delete(file + ".sha256"); } catch (IOException) { }
            }
        }
    }

    /// <summary>Explicit cleanup retains the requested source and all leased documents.</summary>
    public Task ClearCacheAsync(string? preserveSourceHash, CancellationToken ct = default) => RunAsync(() =>
    {
        foreach (string folder in Directory.EnumerateDirectories(_cache))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(folder);
            // Operate only on cache-owned SHA directories, never a path from a project or manifest.
            if (!IsSourceHash(name) || name.Equals(preserveSourceHash, StringComparison.OrdinalIgnoreCase)) continue;
            string full = Path.GetFullPath(folder);
            if (!full.StartsWith(_cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) continue;
            using var sourceLease = TryClaimSourceCleanup(name);
            if (sourceLease == null) continue;
            Directory.Delete(full, recursive: true);
        }
        return true;
    }, ct);

    private static bool IsSourceHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);
    private string SourceLeasePath(string hash) => Path.Combine(_cache, ".source-" + hash.ToLowerInvariant() + ".lock");
    private FileStream? TryClaimSourceCleanup(string hash)
    {
        try { return new FileStream(SourceLeasePath(hash), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
    }

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await _budget.AcquireAsync(ct).ConfigureAwait(false);
            return await Task.Run(action, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is MagickException or ObjectDisposedException)
        {
            // Aborting native processing can invalidate the Magick image before
            // managed code resumes. Preserve cancellation instead of reporting
            // a decode/output error from the next image access.
            throw new OperationCanceledException(null, ex, ct);
        }
        finally { _gate.Release(); }
    }

    private static void Normalize(IMagickImage<byte> image)
    {
        var profile = image.GetColorProfile();
        if (profile != null) image.TransformColorSpace(ColorProfiles.SRGB);
        else image.ColorSpace = ColorSpace.sRGB;
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove);
        image.Strip();
        image.SetProfile(ColorProfiles.SRGB);
        image.Depth = 8;
        image.Format = MagickFormat.Png;
    }

    private static void Rotate(IMagickImage<byte> image, int rotation)
    {
        if (rotation % 90 != 0) throw new ArgumentOutOfRangeException(nameof(rotation));
        if (rotation % 360 != 0) image.Rotate(((rotation % 360) + 360) % 360);
    }

    private static void ValidateDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 30_000 || height > 30_000 || (long)width * height > MaxSourcePixels)
            throw new ImagePipelineException("imageTooLarge");
    }

    private static void AtomicCopy(string source, string destination, CancellationToken ct, CacheBudget budget)
    {
        budget.EnsureCapacity(new FileInfo(source).Length);
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920]; int count;
                while ((count = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
            File.Move(temp, destination, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void WriteAtomic(IMagickImage<byte> image, string destination, CancellationToken ct, CacheBudget budget)
    {
        ct.ThrowIfCancellationRequested();
        budget.EnsureCapacity(CacheBudget.PngWriteReserve(image.Width, image.Height));
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            ct.ThrowIfCancellationRequested();
            // Managed streams support deep Windows cache paths; the native
            // filename writer can fail at MAX_PATH even in a long-path-aware app.
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { image.Write(output, MagickFormat.Png); output.Flush(true); }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
            WriteChecksum(destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static bool IsVerifiedImage(string path, int? width = null, int? height = null)
    {
        try
        {
            string sidecar = path + ".sha256";
            if (!File.Exists(path) || !File.Exists(sidecar) || new FileInfo(sidecar).Length > 80) return false;
            string expected = File.ReadAllText(sidecar).Trim();
            if (expected.Length != 64) return false;
            using var input = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
            input.Position = 0;
            var info = new MagickImageInfo(input);
            return info.Width > 0 && info.Height > 0 && (width == null || info.Width == width) && (height == null || info.Height == height);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or MagickException) { return false; }
    }

    internal static void WriteChecksum(string path)
    {
        string temp = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var input = File.OpenRead(path);
            File.WriteAllText(temp, Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
            File.Move(temp, path + ".sha256", true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static MagickImage ReadImage(string path)
    {
        using var input = File.OpenRead(path);
        return new MagickImage(input);
    }

    internal static MagickImageInfo ReadInfo(string path)
    {
        using var input = File.OpenRead(path);
        return new MagickImageInfo(input);
    }
}
