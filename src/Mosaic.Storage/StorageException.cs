namespace Mosaic.Storage;

public sealed class ProjectStorageException : Exception
{
    public string Code { get; }
    public ProjectStorageException(string code, Exception? inner = null) : base(code, inner) => Code = code;
}
