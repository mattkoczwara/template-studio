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
    public const string ModelId = "waifu2x-cunet-20250915-scale2x-no-denoise";
    private readonly string _assets;
    private readonly string? _cache;
    public string LastBackend { get; private set; } = "";
    public long LastWorkerPeakBytes { get; private set; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>
    {
        ["waifu2x-ncnn-vulkan.exe"] = "7ef2efc4c54e1a963046b3a9b0aee6bca5b1487a7c907373035a10f4187a733d",
        ["mosaic-cunet-cpu.exe"] = "b0670b8ea4e7d37c2c01e2d133df671571fbff3f636502b441c5367e380f430c",
        ["vcomp140.dll"] = "55aba23cdcd6484fbb06f4155b8ca75adfce7a881f10afd0c49457165e677164",
        ["msvcp140.dll"] = "0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9",
        ["vcruntime140.dll"] = "d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066",
        ["vcruntime140_1.dll"] = "1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f",
        ["models-cunet/scale2.0x_model.bin"] = "d82ed30207fd07e1c16726a9ca838ce44e6e25a105b0278c7ca681516ee0e18e",
        ["models-cunet/scale2.0x_model.param"] = "9b06073e039ca93d5bd79f6bb42016cd98d5bdefa0e9761df6b22b844c39965f"
    };

    public EnhancementService(string? assetsRoot = null, string? cacheRoot = null)
    {
        _assets = Path.GetFullPath(assetsRoot ?? Path.Combine(AppContext.BaseDirectory, "ai"));
        _cache = cacheRoot;
    }

    public async Task<string> EnhanceAsync(SourceInfo source, IProgress<JobProgress>? progress, CancellationToken ct, bool forceCpu = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => VerifyAssets(ct), ct).ConfigureAwait(false);
            if ((long)source.Width * source.Height > 16_000_000 || Math.Max(source.Width, source.Height) > 15_000)
                throw new EnhancementException("aiSourceTooLarge");
            string dir = _cache == null ? Path.GetDirectoryName(source.NormalizedPath)! : Path.Combine(_cache, source.Sha256);
            Directory.CreateDirectory(dir);
            string destination = Path.Combine(dir, "enhanced-cunet-20250915-2x-n-1.png");
            if (File.Exists(destination))
            {
                try
                {
                    var info = new MagickImageInfo(destination);
                    if (info.Width == source.Width * 2 && info.Height == source.Height * 2 && ImageService.IsVerifiedImage(destination, source.Width * 2, source.Height * 2))
                    { LastBackend = "cache"; progress?.Report(new(1, "enhancedReady")); return destination; }
                }
                catch (MagickException) { }
                File.Delete(destination);
            }
            if (new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace < Math.Max(512L * 1024 * 1024, (long)source.Width * source.Height * 24))
                throw new EnhancementException("diskFull");

            var attempts = forceCpu ? new[] { (Cpu: true, Tile: 64) } : new[] { (Cpu: false, Tile: 128), (Cpu: false, Tile: 64), (Cpu: true, Tile: 64) };
            foreach (var attempt in attempts)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(0, attempt.Cpu ? "aiCpu" : "aiGpu"));
                string temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    bool completed = await RunWorkerAsync(source.NormalizedPath, temp, attempt.Cpu, attempt.Tile, progress, ct).ConfigureAwait(false);
                    if (!completed || !File.Exists(temp)) continue;
                    var info = new MagickImageInfo(temp);
                    if (info.Width != source.Width * 2 || info.Height != source.Height * 2) continue;
                    ct.ThrowIfCancellationRequested();
                    File.Move(temp, destination, true);
                    ImageService.WriteChecksum(destination);
                    progress?.Report(new(1, "enhancedReady"));
                    return destination;
                }
                finally { if (File.Exists(temp)) File.Delete(temp); if (File.Exists(temp + ".sha256")) File.Delete(temp + ".sha256"); }
            }
            throw new EnhancementException("aiFailed");
        }
        catch (OperationCanceledException) { throw; }
        catch (EnhancementException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or MagickException)
        { throw new EnhancementException("aiFailed", e); }
        finally { _gate.Release(); }
    }

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

    private async Task<bool> RunWorkerAsync(string input, string output, bool cpu, int tile, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        if (cpu) return await RunCpuAsync(input, output, tile, progress, ct).ConfigureAwait(false);
        var start = new ProcessStartInfo(Path.Combine(_assets, "waifu2x-ncnn-vulkan.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _assets
        };
        foreach (string value in new[] { "-i", input, "-o", output, "-n", "-1", "-s", "2", "-t", tile.ToString(CultureInfo.InvariantCulture),
            "-m", Path.Combine(_assets, "models-cunet"), "-j", "1:1:1", "-f", "png" }) start.ArgumentList.Add(value);
        return await ExecuteAsync(start, cpu, progress, ct).ConfigureAwait(false);
    }

    private async Task<bool> RunCpuAsync(string input, string output, int tile, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        string rawInput = output + ".input.rgb", rawOutput = output + ".output.rgb";
        try
        {
            var dimensions = await Task.Run(() =>
            {
                using var image = new MagickImage(input);
                image.Depth = 8;
                image.Write(rawInput, MagickFormat.Rgb);
                return (image.Width, image.Height);
            }, ct).ConfigureAwait(false);
            var start = new ProcessStartInfo(Path.Combine(_assets, "mosaic-cunet-cpu.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = _assets
            };
            foreach (var value in new[] { rawInput, rawOutput, Path.Combine(_assets, "models-cunet"),
                dimensions.Width.ToString(CultureInfo.InvariantCulture), dimensions.Height.ToString(CultureInfo.InvariantCulture),
                tile.ToString(CultureInfo.InvariantCulture), Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(value);
            if (!await ExecuteAsync(start, true, progress, ct).ConfigureAwait(false) || !File.Exists(rawOutput)) return false;
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var image = new MagickImage(rawOutput, new MagickReadSettings
                { Format = MagickFormat.Rgb, Width = dimensions.Width * 2, Height = dimensions.Height * 2, Depth = 8 });
                ImageService.WriteAtomic(image, output, ct);
            }, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (File.Exists(rawInput)) File.Delete(rawInput);
            if (File.Exists(rawOutput)) File.Delete(rawOutput);
        }
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
