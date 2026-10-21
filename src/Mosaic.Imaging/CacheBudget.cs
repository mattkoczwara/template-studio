using System.IO;

namespace Mosaic.Imaging;

/// <summary>One admission policy for source, derived and native-worker cache writes.</summary>
internal sealed class CacheBudget
{
    public const long DefaultLimit = 4L * 1024 * 1024 * 1024;
    private readonly string root;
    private readonly long limit;

    public CacheBudget(string root, long limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        this.root = Path.GetFullPath(root);
        this.limit = limit;
        Directory.CreateDirectory(this.root);
        if ((File.GetAttributes(this.root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The cache root cannot be a linked directory.");
    }

    // Held through publication/cleanup, including native inference. File sharing also
    // coordinates independent windows/processes, rather than only one service instance.
    public async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(root, ".budget.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(40, token).ConfigureAwait(false); }
        }
    }

    public void EnsureCapacity(long additionalBytes)
    {
        long used = Directory.EnumerateFiles(root, "*", new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(path => new FileInfo(path).Length);
        if (additionalBytes < 0 || additionalBytes > limit || used > limit - additionalBytes)
            throw new ImagePipelineException("cacheFull");
        EnsureDiskSpace(additionalBytes);
    }

    public void EnsureDiskSpace(long bytes)
    {
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < bytes)
            throw new ImagePipelineException("diskFull");
    }

    // Lossless PNG upper bound includes scanlines/deflate overhead, sRGB metadata,
    // checksum publication and atomic replacement's temporary sibling.
    public static long PngWriteReserve(uint width, uint height) => checked((long)width * height * 4 + 1024 * 1024 + 128);
}
