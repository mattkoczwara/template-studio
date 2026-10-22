namespace Mosaic.Core;

public enum OrientationMode { Auto, Portrait, Landscape }
public enum SheetOrientation { Portrait, Landscape }
public enum FitMode { Fit, Crop, Stretch }
public enum QualityMode { Standard, Enhanced }

public sealed record ArtworkSize(decimal WidthInches, decimal HeightInches);
public sealed record PaperSpec(decimal WidthInches = 8.5m, decimal HeightInches = 11m,
    decimal MarginInches = 0m, OrientationMode Orientation = OrientationMode.Auto);
public sealed record Composition(FitMode Mode = FitMode.Fit, int Rotation = 0, decimal CropX = 0.5m, decimal CropY = 0.5m);
public sealed record RectInches(decimal X, decimal Y, decimal Width, decimal Height)
{
    public decimal Right => X + Width;
    public decimal Bottom => Y + Height;
}
public sealed record PageTile(int Index, int Row, int Column, RectInches ArtworkBounds, RectInches PaperBounds);
public sealed record LayoutPlan(ArtworkSize Artwork, PaperSpec Paper, SheetOrientation Orientation,
    decimal PageWidth, decimal PageHeight, decimal TileWidth, decimal TileHeight,
    int Rows, int Columns, int PortraitCount, int LandscapeCount, IReadOnlyList<PageTile> Pages)
{
    public int PageCount => Pages.Count;
}
public sealed record SourceInfo(string OriginalPath, string NormalizedPath, string Sha256, int Width, int Height,
    string Format, long ByteLength, string PreviewPath);
public sealed record ProjectState
{
    public const string CurrentEnhancementModel = "waifu2x-cunet-20250915-noise1-bounded4x-v2";
    public int SchemaVersion { get; init; } = 1;
    public string Title { get; init; } = "Untitled";
    public ArtworkSize Size { get; init; } = new(24m, 18m);
    public bool KeepProportions { get; init; } = true;
    public string LastDimension { get; init; } = "width";
    public Composition Composition { get; init; } = new();
    public PaperSpec Paper { get; init; } = new();
    public QualityMode Quality { get; init; }
    public string EnhancementModel { get; init; } = CurrentEnhancementModel;
    public string PrinterProfile { get; init; } = "epson-et8500";
    public string? PrinterName { get; init; }
    public bool Grayscale { get; init; }
    public int Copies { get; init; } = 1;
    public bool AssemblyGuide { get; init; }
    public SheetOrientation ResolvedOrientation { get; init; }
    public string SourceHash { get; init; } = "";
    public string SourceExtension { get; init; } = ".png";
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
}
public sealed record RenderSnapshot(SourceInfo Source, string ImagePath, ProjectState State, LayoutPlan Layout);
public sealed record JobProgress(double Fraction, string Stage);
public sealed record CalibrationRecord
{
    public string PrinterName { get; init; } = "";
    public string Driver { get; init; } = "";
    public string Profile { get; init; } = "epson-et8500";
    public string Pathway { get; init; } = "direct";
    public string Media { get; init; } = "Letter / plain paper";
    public string DriverSettings { get; init; } = "";
    public string DriverFingerprint { get; init; } = "";
    public SheetOrientation Orientation { get; init; }
    public decimal MeasuredXInches { get; init; } = 4m;
    public decimal MeasuredYInches { get; init; } = 4m;
    public decimal OffsetXInches { get; init; }
    public decimal OffsetYInches { get; init; }
    public bool EdgeClipping { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset MeasuredAt { get; init; } = DateTimeOffset.UtcNow;
    public decimal ScaleX => 4m / MeasuredXInches;
    public decimal ScaleY => 4m / MeasuredYInches;
}
