using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ImageMagick;
using Mosaic.Core;

namespace Mosaic.Imaging;

public sealed class EnhancementException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}

public interface IImageEnhancer
{
    Task<string> EnhanceAsync(SourceInfo source, IProgress<JobProgress>? progress, CancellationToken ct, bool forceCpu = false);
}

/// <summary>Hash-pinned isolated offline NCNN inference. No network, shell, or external model discovery.</summary>
public sealed partial class EnhancementService : IImageEnhancer
{
    public const string ModelId = ProjectState.CurrentEnhancementModel;
    /// <summary>At most 64 million output pixels and a 30,000-pixel edge, independent of print size.</summary>
    public static int OutputScale(SourceInfo source) => (long)source.Width * source.Height <= 4_000_000 && Math.Max(source.Width, source.Height) <= 7500 ? 4 : 2;
    private readonly string _assets;
    private readonly string? _cache;
    private readonly long _maxCacheBytes;
    public string LastBackend { get; private set; } = "";
    public long LastWorkerPeakBytes { get; private set; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>
    {
        ["waifu2x-ncnn-vulkan.exe"] = "7ef2efc4c54e1a963046b3a9b0aee6bca5b1487a7c907373035a10f4187a733d",
        ["mosaic-cunet-cpu.exe"] = "c3dc90d4e8f1fc0fd7687739b5d3c4b39c181bb0d92b248a279a4e1636ad4133",
        ["vcomp140.dll"] = "55aba23cdcd6484fbb06f4155b8ca75adfce7a881f10afd0c49457165e677164",
        ["msvcp140.dll"] = "0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9",
        ["vcruntime140.dll"] = "d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066",
        ["vcruntime140_1.dll"] = "1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f",
        ["models-cunet/scale2.0x_model.bin"] = "d82ed30207fd07e1c16726a9ca838ce44e6e25a105b0278c7ca681516ee0e18e",
        ["models-cunet/scale2.0x_model.param"] = "9b06073e039ca93d5bd79f6bb42016cd98d5bdefa0e9761df6b22b844c39965f",
        ["models-cunet/noise1_scale2.0x_model.bin"] = "862c0e40f3df956ff57bd4d168c85dea5d375b614077fdddd3ac8cfdb9a686da",
        ["models-cunet/noise1_scale2.0x_model.param"] = "9b06073e039ca93d5bd79f6bb42016cd98d5bdefa0e9761df6b22b844c39965f"
    };

    public EnhancementService(string? assetsRoot = null, string? cacheRoot = null, long maxCacheBytes = CacheBudget.DefaultLimit)
    {
        _assets = Path.GetFullPath(assetsRoot ?? Path.Combine(AppContext.BaseDirectory, "ai"));
        _cache = cacheRoot;
        if (maxCacheBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxCacheBytes));
        _maxCacheBytes = maxCacheBytes;
    }

    public async Task<string> EnhanceAsync(SourceInfo source, IProgress<JobProgress>? progress, CancellationToken ct, bool forceCpu = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => VerifyAssets(ct), ct).ConfigureAwait(false);
            if (!await Task.Run(() => ImageService.IsVerifiedImage(source.NormalizedPath, source.Width, source.Height), ct).ConfigureAwait(false))
                throw new EnhancementException("aiFailed");
            if ((long)source.Width * source.Height > 16_000_000 || Math.Max(source.Width, source.Height) > 15_000)
                throw new EnhancementException("aiSourceTooLarge");
            string dir = _cache == null ? Path.GetDirectoryName(source.NormalizedPath)! : Path.Combine(_cache, source.Sha256);
            Directory.CreateDirectory(dir);
            var budget = new CacheBudget(_cache ?? Path.GetDirectoryName(dir)!, _maxCacheBytes);
            using var cacheLease = await budget.AcquireAsync(ct).ConfigureAwait(false);
            int scale = OutputScale(source);
            string destination = Path.Combine(dir, $"enhanced-cunet-v2-n1-{scale}x.png");
            if (File.Exists(destination))
            {
                try
                {
                    var info = ImageService.ReadInfo(destination);
                    if (info.Width == source.Width * scale && info.Height == source.Height * scale && ImageService.IsVerifiedImage(destination, source.Width * scale, source.Height * scale))
                    { LastBackend = "cache"; LastWorkerPeakBytes = 0; progress?.Report(new(1, "enhancedReady")); return destination; }
                }
                catch (MagickException) { }
                File.Delete(destination);
            }
            // Bound both GPU PNG output and the CPU fallback's raw input/output,
            // atomic PNG and checksum siblings before launching the trusted worker.
            long reserve = checked((long)source.Width * source.Height * (scale == 4 ? 192 : 64));
            budget.EnsureCapacity(reserve + 2L * 1024 * 1024);
            budget.EnsureDiskSpace(Math.Max(512L * 1024 * 1024, reserve));

            string? intermediate = null;
            LastWorkerPeakBytes = 0;
            try
            {
                int passes = scale == 4 ? 2 : 1;
                for (int pass = 0; pass < passes; pass++)
                {
                    int passIndex = pass;
                    var stageProgress = progress == null ? null : new InlineProgress(p => progress.Report(new((passIndex + p.Fraction) / passes, p.Stage)));
                    int factor = pass == 0 ? 2 : 4;
                    string result = await ProcessPassAsync(intermediate ?? source.NormalizedPath, dir,
                        source.Width * factor, source.Height * factor, forceCpu, stageProgress, ct, budget).ConfigureAwait(false);
                    if (intermediate != null) DeleteWorkingImage(intermediate);
                    intermediate = result;
                }
                ct.ThrowIfCancellationRequested();
                File.Move(intermediate!, destination, true);
                ImageService.WriteChecksum(destination);
                progress?.Report(new(1, "enhancedReady"));
                return destination;
            }
            finally { if (intermediate != null) DeleteWorkingImage(intermediate); }
        }
        catch (OperationCanceledException) { throw; }
        catch (EnhancementException) { throw; }
        catch (ImagePipelineException e) { throw new EnhancementException(e.Code, e); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or MagickException)
        { throw new EnhancementException("aiFailed", e); }
        finally { _gate.Release(); }
    }

    private async Task<string> ProcessPassAsync(string input, string dir, int width, int height, bool forceCpu,
        IProgress<JobProgress>? progress, CancellationToken ct, CacheBudget budget)
    {
        var attempts = forceCpu ? new[] { (Cpu: true, Tile: 64) } : new[] { (Cpu: false, Tile: 128), (Cpu: false, Tile: 64), (Cpu: true, Tile: 64) };
        foreach (var attempt in attempts)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(0, attempt.Cpu ? "aiCpu" : "aiGpu"));
            string temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
            bool keep = false;
            long earlierPeak = LastWorkerPeakBytes;
            try
            {
                if (!await RunWorkerAsync(input, temp, attempt.Cpu, attempt.Tile, progress, ct, budget).ConfigureAwait(false) || !File.Exists(temp)) continue;
                var info = ImageService.ReadInfo(temp);
                if (info.Width != width || info.Height != height) continue;
                ct.ThrowIfCancellationRequested();
                keep = true; return temp;
            }
            catch (System.ComponentModel.Win32Exception) when (!attempt.Cpu) { /* Try the isolated CPU backend. */ }
            finally
            {
                LastWorkerPeakBytes = Math.Max(earlierPeak, LastWorkerPeakBytes);
                if (!keep) DeleteWorkingImage(temp);
            }
        }
        throw new EnhancementException("aiFailed");
    }

    private static void DeleteWorkingImage(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".sha256")) File.Delete(path + ".sha256");
    }

    private sealed class InlineProgress(Action<JobProgress> report) : IProgress<JobProgress>
    { public void Report(JobProgress value) => report(value); }

    public void VerifyAssets(CancellationToken ct = default)
    {
        foreach (var entry in Hashes)
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(_assets, entry.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new EnhancementException("aiMissing");
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, entry.Value, StringComparison.Ordinal)) throw new EnhancementException("aiIntegrity");
        }
    }

    private async Task<bool> RunWorkerAsync(string input, string output, bool cpu, int tile, IProgress<JobProgress>? progress, CancellationToken ct, CacheBudget budget)
    {
        if (cpu) return await RunCpuAsync(input, output, tile, progress, ct, budget).ConfigureAwait(false);
        var start = new ProcessStartInfo(Path.Combine(_assets, "waifu2x-ncnn-vulkan.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _assets
        };
        foreach (string value in new[] { "-i", NativePath(input), "-o", NativePath(output), "-n", "1", "-s", "2", "-t", tile.ToString(CultureInfo.InvariantCulture),
            "-m", NativePath(Path.Combine(_assets, "models-cunet")), "-j", "1:1:1", "-f", "png" }) start.ArgumentList.Add(value);
        return await ExecuteAsync(start, cpu, progress, ct).ConfigureAwait(false);
    }

    private async Task<bool> RunCpuAsync(string input, string output, int tile, IProgress<JobProgress>? progress, CancellationToken ct, CacheBudget budget)
    {
        string rawInput = output + ".input.rgb", rawOutput = output + ".output.rgb";
        try
        {
            var dimensions = await Task.Run(() =>
            {
                using var image = ImageService.ReadImage(input);
                image.Depth = 8;
                using (var raw = File.Create(rawInput)) image.Write(raw, MagickFormat.Rgb);
                return (image.Width, image.Height);
            }, ct).ConfigureAwait(false);
            var start = new ProcessStartInfo(Path.Combine(_assets, "mosaic-cunet-cpu.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = _assets
            };
            foreach (var value in new[] { NativePath(rawInput), NativePath(rawOutput), NativePath(Path.Combine(_assets, "models-cunet")),
                dimensions.Width.ToString(CultureInfo.InvariantCulture), dimensions.Height.ToString(CultureInfo.InvariantCulture),
                tile.ToString(CultureInfo.InvariantCulture), Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(CultureInfo.InvariantCulture), "1" })
                start.ArgumentList.Add(value);
            if (!await ExecuteAsync(start, true, progress, ct).ConfigureAwait(false) || !File.Exists(rawOutput)) return false;
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var raw = File.OpenRead(rawOutput);
                using var image = new MagickImage(raw, new MagickReadSettings
                { Format = MagickFormat.Rgb, Width = dimensions.Width * 2, Height = dimensions.Height * 2, Depth = 8 });
                image.SetProfile(ColorProfiles.SRGB);
                ImageService.WriteAtomic(image, output, ct, budget);
            }, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (File.Exists(rawInput)) File.Delete(rawInput);
            if (File.Exists(rawOutput)) File.Delete(rawOutput);
        }
    }

    // The native workers use wide Win32/CRT file APIs, which require the
    // extended prefix for paths beyond the legacy filename limit.
    private static string NativePath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.Length < 248 || full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private async Task<bool> ExecuteAsync(ProcessStartInfo start, bool cpu, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        // A fully-qualified trusted exe plus private directory avoids executable/PATH lookup from artwork folders.
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(cpu ? 30 : 15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var kill = linked.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stdout = DrainAsync(process.StandardOutput, progress, cpu, linked.Token);
        var stderr = DrainAsync(process.StandardError, progress, cpu, linked.Token);
        var memory = SampleMemoryAsync(process, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            LastBackend = cpu ? "CPU" : "Vulkan GPU";
            LastWorkerPeakBytes = await memory.ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        finally
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<long> SampleMemoryAsync(Process process, CancellationToken ct)
    {
        long peak = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (process.HasExited) break;
                process.Refresh();
                peak = Math.Max(peak, process.PeakWorkingSet64);
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { break; }
            catch (OperationCanceledException) { break; }
        }
        return peak;
    }

    private static async Task DrainAsync(StreamReader reader, IProgress<JobProgress>? progress, bool cpu, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            var match = Percent().Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
                progress?.Report(new(Math.Clamp(value / 100, 0, .99), cpu ? "aiCpu" : "aiGpu"));
        }
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%", RegexOptions.CultureInvariant)]
    private static partial Regex Percent();
}
