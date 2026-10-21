using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mosaic.Core;

namespace Mosaic.Storage;

public sealed record OpenedProject(ProjectState State, string OriginalPath, string? ThumbnailPath);
public sealed record ProjectPreview(ProjectState State, byte[]? ThumbnailBytes);

/// <summary>A portable project contains immutable source bytes, never a link to Downloads.</summary>
public sealed class ProjectStore
{
    public const long MaximumSourceBytes = 256L * 1024 * 1024;
    private const long MaximumProjectBytes = MaximumSourceBytes + 20L * 1024 * 1024;
    private const int MaximumManifestBytes = 128 * 1024;
    private const int MaximumThumbnailBytes = 16 * 1024 * 1024;
    private const long MaximumCacheBytes = 2L * 1024 * 1024 * 1024;
    private readonly string _cacheRoot;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 32
    };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

    public ProjectStore(string? cacheRoot = null)
    {
        _cacheRoot = cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MosaicTemplateStudio", "project-cache");
    }

    /// <summary>Removes only this store's disposable SHA-named extracted assets, never projects or unknown files.</summary>
    public Task<long> ClearCacheAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var root = new DirectoryInfo(Path.GetFullPath(_cacheRoot));
        if (!root.Exists) return 0L;
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0) throw new ProjectStorageException("ProjectInvalid");
        string prefix = root.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long removed = 0;
        foreach (var file in root.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(file.FullName);
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                file.Name.Length <= 64 || !file.Name[..64].All(Uri.IsHexDigit)) continue;
            string suffix = file.Name[64..];
            if (suffix != ".preview" && !Extensions.Contains(suffix)) continue;
            try
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                long length = file.Length;
                File.Delete(target);
                removed += length;
            }
            catch (IOException) { /* A cache in use by another window can be reclaimed later. */ }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }, ct);

    public async Task SaveAsync(string path, ProjectState state, SourceInfo source, CancellationToken ct = default)
    {
        ValidateState(state);
        if (!File.Exists(source.OriginalPath)) throw new ProjectStorageException("SourceUnavailable");
        var extension = Path.GetExtension(source.OriginalPath).ToLowerInvariant();
        if (!Extensions.Contains(extension))
        {
            extension = "." + source.Format.Trim('.').ToLowerInvariant();
            if (!Extensions.Contains(extension)) throw new ProjectStorageException("UnsupportedImage");
        }
        var sourceInfo = new FileInfo(source.OriginalPath);
        if (sourceInfo.Length is <= 0 or > MaximumSourceBytes) throw new ProjectStorageException("ProjectTooLarge");
        var target = Path.GetFullPath(path);
        if (target.Equals(Path.GetFullPath(source.OriginalPath), StringComparison.OrdinalIgnoreCase))
            throw new ProjectStorageException("SourceChanged");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                var entry = archive.CreateEntry("source/original" + extension, CompressionLevel.NoCompression);
                string actualHash;
                await using (var input = new FileStream(source.OriginalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                await using (var output = entry.Open())
                    actualHash = await CopyAndHashAsync(input, output, MaximumSourceBytes, ct);
                if (!string.IsNullOrEmpty(source.Sha256) && !actualHash.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ProjectStorageException("SourceChanged");
                var layout = LayoutEngine.Calculate(state.Size, state.Paper);
                var savedState = state with { SourceHash = actualHash, SourceExtension = extension,
                    SavedAt = DateTimeOffset.UtcNow, ResolvedOrientation = layout.Orientation };
                var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var json = manifest.Open())
                    await JsonSerializer.SerializeAsync(json, savedState, JsonOptions, ct);
                if (File.Exists(source.PreviewPath) && new FileInfo(source.PreviewPath).Length <= MaximumThumbnailBytes)
                {
                    var thumbnail = archive.CreateEntry("assets/thumbnail.image", CompressionLevel.NoCompression);
                    await using var input = new FileStream(source.PreviewPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await using var output = thumbnail.Open();
                    await CopyAndHashAsync(input, output, MaximumThumbnailBytes, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (ProjectStorageException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { throw new ProjectStorageException("ProjectSaveFailed", ex); }
        finally { TryDelete(temporary); }
    }

    public async Task<ProjectPreview> PeekAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var stream = OpenProjectStream(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var state = await ReadManifestAsync(archive, ct);
            byte[]? thumb = null;
            if (archive.GetEntry("assets/thumbnail.image") is { } thumbnail)
            {
                await using var input = thumbnail.Open();
                using var data = new MemoryStream();
                await CopyAndHashAsync(input, data, MaximumThumbnailBytes, ct);
                thumb = data.ToArray();
            }
            return new(state, thumb);
        }
        catch (OperationCanceledException) { throw; }
        catch (ProjectStorageException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or OverflowException)
        { throw new ProjectStorageException("ProjectInvalid", ex); }
    }

    public async Task<OpenedProject> OpenAsync(string path, CancellationToken ct = default)
    {
        string? temporary = null;
        try
        {
            await using var stream = OpenProjectStream(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var state = await ReadManifestAsync(archive, ct);
            Directory.CreateDirectory(_cacheRoot);
            // A cache is disposable but must not silently consume the whole disk.
            long cacheBytes = Directory.EnumerateFiles(_cacheRoot).Sum(file => new FileInfo(file).Length);
            long requiredBytes = archive.Entries.Where(entry => entry.FullName != "manifest.json").Sum(entry => entry.Length);
            if (cacheBytes + requiredBytes > MaximumCacheBytes) throw new ProjectStorageException("StorageFull");
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_cacheRoot))!);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes + 128L * 1024 * 1024)
                throw new ProjectStorageException("StorageFull");
            var originalPath = Path.Combine(_cacheRoot, state.SourceHash.ToLowerInvariant() + state.SourceExtension.ToLowerInvariant());
            temporary = originalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var original = archive.GetEntry("source/original" + state.SourceExtension) ?? throw new ProjectStorageException("ProjectInvalid");
            string hash;
            await using (var input = original.Open())
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                hash = await CopyAndHashAsync(input, output, MaximumSourceBytes, ct);
            if (!hash.Equals(state.SourceHash, StringComparison.OrdinalIgnoreCase)) throw new ProjectStorageException("ProjectIntegrityFailed");
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, originalPath, overwrite: true);
            temporary = null;
            string? thumbnailPath = null;
            if (archive.GetEntry("assets/thumbnail.image") is { } thumbnail)
            {
                thumbnailPath = Path.Combine(_cacheRoot, state.SourceHash.ToLowerInvariant() + ".preview");
                temporary = thumbnailPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await using (var input = thumbnail.Open())
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await CopyAndHashAsync(input, output, MaximumThumbnailBytes, ct);
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, thumbnailPath, overwrite: true);
                temporary = null;
            }
            return new(state, originalPath, thumbnailPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (ProjectStorageException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or OverflowException)
        { throw new ProjectStorageException("ProjectInvalid", ex); }
        finally { if (temporary is not null) TryDelete(temporary); }
    }

    private static FileStream OpenProjectStream(string path)
    {
        var file = new FileInfo(path);
        if (file.Length is <= 0 or > MaximumProjectBytes) throw new ProjectStorageException("ProjectTooLarge");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    private static async Task<ProjectState> ReadManifestAsync(ZipArchive archive, CancellationToken ct)
    {
        if (archive.Entries.Count is < 2 or > 3) throw new ProjectStorageException("ProjectInvalid");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(entry.FullName) || entry.FullName.Contains('\\') || entry.FullName.Contains("..", StringComparison.Ordinal) ||
                entry.FullName.StartsWith('/') || entry.FullName.Contains(':')) throw new ProjectStorageException("ProjectInvalid");
            long limit = entry.FullName switch
            {
                "manifest.json" => MaximumManifestBytes,
                "assets/thumbnail.image" => MaximumThumbnailBytes,
                _ when entry.FullName.StartsWith("source/original", StringComparison.Ordinal) => MaximumSourceBytes,
                _ => throw new ProjectStorageException("ProjectInvalid")
            };
            if (entry.Length <= 0 || entry.Length > limit || entry.Length / Math.Max(1, entry.CompressedLength) > 1000)
                throw new ProjectStorageException("ProjectTooLarge");
            total = checked(total + entry.Length);
            if (total > MaximumProjectBytes) throw new ProjectStorageException("ProjectTooLarge");
        }
        var manifest = archive.GetEntry("manifest.json") ?? throw new ProjectStorageException("ProjectInvalid");
        await using var input = manifest.Open();
        using var bounded = new MemoryStream();
        await CopyAndHashAsync(input, bounded, MaximumManifestBytes, ct);
        bounded.Position = 0;
        var state = await JsonSerializer.DeserializeAsync<ProjectState>(bounded, JsonOptions, ct) ?? throw new ProjectStorageException("ProjectInvalid");
        ValidateState(state);
        if (state.SourceHash.Length != 64 || !state.SourceHash.All(Uri.IsHexDigit) ||
            !Extensions.Contains(state.SourceExtension) || state.SourceExtension != state.SourceExtension.ToLowerInvariant())
            throw new ProjectStorageException("ProjectInvalid");
        if (archive.GetEntry("source/original" + state.SourceExtension) is null) throw new ProjectStorageException("ProjectInvalid");
        if (archive.Entries.Any(entry => entry.FullName is not ("manifest.json" or "assets/thumbnail.image") &&
            entry.FullName != "source/original" + state.SourceExtension)) throw new ProjectStorageException("ProjectInvalid");
        return state;
    }

    internal static void ValidateState(ProjectState state)
    {
        if (state.SchemaVersion != 1) throw new ProjectStorageException("ProjectVersionUnsupported");
        if (state.Size is null || state.Paper is null || state.Composition is null || state.Title is null || state.SourceHash is null || state.SourceExtension is null ||
            state.Title.Length > 250 || state.Copies is < 1 or > 99 || state.LastDimension is not ("width" or "height") ||
            state.PrinterName?.Length > 1024 || state.EnhancementModel?.Length > 1024 ||
            !Enum.IsDefined(state.Quality) || !Enum.IsDefined(state.Paper.Orientation) || !Enum.IsDefined(state.Composition.Mode) ||
            !Enum.IsDefined(state.ResolvedOrientation) || state.Composition.Rotation is not (0 or 90 or 180 or 270) ||
            state.Composition.CropX is < 0 or > 1 || state.Composition.CropY is < 0 or > 1 ||
            state.PrinterProfile is not ("epson-et8500" or "canon-mf210" or "precision-trim"))
            throw new ProjectStorageException("ProjectInvalid");
        try { LayoutEngine.Calculate(state.Size, state.Paper); }
        catch (ArgumentException ex) { throw new ProjectStorageException("ProjectInvalid", ex); }
        catch (OverflowException ex) { throw new ProjectStorageException("ProjectInvalid", ex); }
    }

    private static async Task<string> CopyAndHashAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long read = 0;
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), ct)) != 0)
            {
                read += count;
                if (read > limit) throw new ProjectStorageException("ProjectTooLarge");
                sha.AppendData(buffer, 0, count);
                await target.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
