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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static int _configured;

    public ImageService(string? cacheRoot = null)
    {
        _cache = Path.GetFullPath(cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosaicTemplateStudio", "cache"));
        Directory.CreateDirectory(_cache);
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
        EnsureSpace(file.Length * 6 + 256L * 1024 * 1024);
        string hash;
        using (var input = File.OpenRead(path)) hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        var dir = Path.Combine(_cache, hash);
        Directory.CreateDirectory(dir);
        var original = Path.Combine(dir, "original" + ext);
        if (!File.Exists(original)) AtomicCopy(path, original, ct);
        using (var verify = File.OpenRead(original))
            if (!Convert.ToHexString(SHA256.HashData(verify)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new ImagePipelineException("imageReadFailed");
        var normalized = Path.Combine(dir, "normalized.png");
        var preview = Path.Combine(dir, "preview.png");
        try
        {
            using var image = new MagickImage();
            image.Progress += (_, e) => e.Cancel = ct.IsCancellationRequested;
            image.Ping(original, new MagickReadSettings { Format = format, FrameCount = 1 });
            ValidateDimensions(image.Width, image.Height);
            image.Read(original, new MagickReadSettings { Format = format, FrameCount = 1 });
            ct.ThrowIfCancellationRequested();
            image.AutoOrient();
            Normalize(image);
            int width = checked((int)image.Width), height = checked((int)image.Height);
            if (!IsVerifiedImage(normalized, width, height)) WriteAtomic(image, normalized, ct);
            if (!IsVerifiedImage(preview))
            {
                image.FilterType = FilterType.Lanczos;
                image.Resize(new MagickGeometry(1800, 1800) { Greater = true });
                WriteAtomic(image, preview, ct);
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
        using var image = new MagickImage(path);
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

    /// <summary>Creates a coherent, rotated lossless image; placement and physical dimensions remain in LayoutEngine.</summary>
    public Task<string> GetOutputImageAsync(SourceInfo source, int rotation = 0, string? enhancedPath = null, CancellationToken ct = default) => RunAsync(() =>
    {
        var input = enhancedPath ?? source.NormalizedPath;
        if (enhancedPath != null && !IsVerifiedImage(enhancedPath)) throw new ImagePipelineException("imageReadFailed");
        if (rotation % 360 == 0 && enhancedPath != null) return input;
        var name = enhancedPath == null ? "standard" : "enhanced";
        var output = Path.Combine(Path.GetDirectoryName(source.NormalizedPath)!, $"{name}-r{((rotation % 360) + 360) % 360}-v1.png");
        if (IsVerifiedImage(output)) return output;
        using var image = new MagickImage(input);
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
        WriteAtomic(image, output, ct);
        return output;
    }, ct);

    public Task<string> CreateDetailComparisonAsync(SourceInfo source, string enhancedPath, CancellationToken ct = default) => RunAsync(() =>
    {
        string output = Path.Combine(Path.GetDirectoryName(source.NormalizedPath)!, "comparison.png");
        using var original = new MagickImage(source.NormalizedPath);
        using var enhanced = new MagickImage(enhancedPath);
        int size = (int)Math.Min(256, Math.Min(original.Width, original.Height));
        var crop = new MagickGeometry(((int)original.Width - size) / 2, ((int)original.Height - size) / 2, (uint)size, (uint)size);
        original.Crop(crop); original.ResetPage();
        original.FilterType = FilterType.Lanczos; original.Resize(512, 512);
        enhanced.Crop(new MagickGeometry(crop.X * 2, crop.Y * 2, (uint)size * 2, (uint)size * 2)); enhanced.ResetPage();
        enhanced.Resize(512, 512);
        using var collection = new MagickImageCollection { original.Clone(), enhanced.Clone() };
        using var joined = collection.AppendHorizontally();
        WriteAtomic(joined, output, ct);
        return output;
    }, ct);

    public void CleanDerivedCache(TimeSpan maxAge)
    {
        // Only stale derived products. Immutable originals remain until the user explicitly clears the cache.
        foreach (var file in Directory.EnumerateFiles(_cache, "*.png", SearchOption.AllDirectories))
            if (!Path.GetFileName(file).StartsWith("original", StringComparison.Ordinal) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - maxAge)
                try { File.Delete(file); } catch (IOException) { }
    }

    /// <summary>Explicit user cleanup; current document's complete source directory is retained.</summary>
    public Task ClearCacheAsync(string? preserveSourceHash, CancellationToken ct = default) => RunAsync(() =>
    {
        foreach (string folder in Directory.EnumerateDirectories(_cache))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(folder);
            // Operate only on cache-owned SHA directories, never a path from a project or manifest.
            if (name.Length != 64 || name.Any(c => !Uri.IsHexDigit(c)) || name.Equals(preserveSourceHash, StringComparison.OrdinalIgnoreCase)) continue;
            string full = Path.GetFullPath(folder);
            if (!full.StartsWith(_cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) continue;
            Directory.Delete(full, recursive: true);
        }
        return true;
    }, ct);

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(action, ct).ConfigureAwait(false); }
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

    private void EnsureSpace(long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(_cache)!);
        if (drive.AvailableFreeSpace < bytes) throw new ImagePipelineException("diskFull");
        // Bound persistent working files too: originals are retained safely until explicit cleanup.
        long cached = Directory.EnumerateFiles(_cache, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
        if (cached + bytes > 4L * 1024 * 1024 * 1024) throw new ImagePipelineException("cacheFull");
    }

    private static void AtomicCopy(string source, string destination, CancellationToken ct)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
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

    internal static void WriteAtomic(IMagickImage<byte> image, string destination, CancellationToken ct)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ct.ThrowIfCancellationRequested();
            image.Write(temp, MagickFormat.Png);
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
            var info = new MagickImageInfo(path);
            return info.Width > 0 && info.Height > 0 && (width == null || info.Width == width) && (height == null || info.Height == height);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or MagickException) { return false; }
    }

    internal static void WriteChecksum(string path)
    {
        string temp = path + ".sha256." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var input = File.OpenRead(path);
            File.WriteAllText(temp, Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
            File.Move(temp, path + ".sha256", true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
