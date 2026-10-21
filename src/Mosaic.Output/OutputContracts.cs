namespace Mosaic.Output;

public sealed record PdfExportResult(string Path, int PageCount, long Bytes);
public sealed record PrintResult(bool Submitted, string PrinterName, int PageCount)
{
    public string Driver { get; init; } = "";
    public string DriverFingerprint { get; init; } = "";
}
public sealed record InstalledPrinter(string Name, string Driver, bool IsDefault);

public sealed class OutputException : Exception
{
    public string Code { get; }
    public decimal? SuggestedMarginInches { get; }
    public OutputException(string code, string message, decimal? suggestedMargin = null, Exception? inner = null) : base(message, inner)
    { Code = code; SuggestedMarginInches = suggestedMargin; }
}

internal static class OutputText
{
    public static bool Polish(string language) => language.StartsWith("pl", StringComparison.OrdinalIgnoreCase);
    public static string Pick(string language, string en, string pl) => Polish(language) ? pl : en;
}
