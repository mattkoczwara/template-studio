namespace Mosaic.Storage;

/// <summary>Owns one recovery slot for the lifetime of an editor window.</summary>
public sealed class RecoverySession : IDisposable
{
    private readonly string root;
    private readonly string recoveryDirectory;
    private readonly FileStream ownership;
    private bool disposed;
    public string RecoveryPath { get; }

    public RecoverySession(string root)
    {
        this.root = Path.GetFullPath(root);
        recoveryDirectory = Path.Combine(this.root, "recovery");
        Directory.CreateDirectory(recoveryDirectory);
        RecoveryPath = Path.Combine(recoveryDirectory, Guid.NewGuid().ToString("N") + ".mosaic");
        ownership = Acquire(RecoveryPath);
    }

    /// <summary>Claims an interrupted session, never another live window's recovery.</summary>
    public RecoveryLease? ClaimPending()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var candidates = Directory.EnumerateFiles(recoveryDirectory, "*.mosaic")
            .Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
            .Append(Path.Combine(root, "recovery.mosaic"))
            .Where(File.Exists).OrderBy(File.GetLastWriteTimeUtc).ToArray();
        foreach (string path in candidates)
        {
            FileStream? claim = null;
            try
            {
                claim = Acquire(path);
                if (File.Exists(path)) return new RecoveryLease(path, claim);
                claim.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                claim?.Dispose(); // Another editor owns it, or it is currently inaccessible.
            }
        }
        return null;
    }

    public void DeleteOwned()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        File.Delete(RecoveryPath);
    }

    // Windows removes the sidecar atomically when its exclusive handle closes, including
    // process termination. Never unlink a lock after releasing it: another process may own it.
    private static FileStream Acquire(string path) => new(path + ".lock", FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ownership.Dispose(); // Leave the portable recovery intact unless explicitly resolved.
    }
}

public sealed class RecoveryLease : IDisposable
{
    private readonly FileStream ownership;
    private bool disposed;
    public string Path { get; }
    internal RecoveryLease(string path, FileStream ownership) { Path = path; this.ownership = ownership; }

    public void Resolve()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        File.Delete(Path);
        Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ownership.Dispose();
    }
}
