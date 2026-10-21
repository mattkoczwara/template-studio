using Mosaic.Core;
using System.Globalization;

int checks = 0;
void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }
var tie = LayoutEngine.Calculate(new(24, 18), new());
Check(tie.Orientation == SheetOrientation.Portrait && tie.PageCount == 6, "portrait tie");
Check(LayoutEngine.Calculate(new(10, 8), new()).Orientation == SheetOrientation.Landscape, "landscape wins");
Check(LayoutEngine.Calculate(new(8, 10), new()).Orientation == SheetOrientation.Portrait, "portrait wins");
Check(LayoutEngine.Calculate(new(10, 8), new(Orientation: OrientationMode.Portrait)).PageCount == 2, "manual orientation");
var random = new Random(143167);
for (int i = 0; i < 1500; i++)
{
    var art = new ArtworkSize((decimal)random.Next(1, 12000) / 100, (decimal)random.Next(1, 12000) / 100);
    decimal margin = (decimal)random.Next(0, 100) / 100;
    var plan = LayoutEngine.Calculate(art, new(MarginInches: margin));
    int p = (int)decimal.Ceiling(art.WidthInches / (8.5m - 2 * margin)) * (int)decimal.Ceiling(art.HeightInches / (11m - 2 * margin));
    int l = (int)decimal.Ceiling(art.WidthInches / (11m - 2 * margin)) * (int)decimal.Ceiling(art.HeightInches / (8.5m - 2 * margin));
    Check(plan.PageCount == Math.Min(p, l), "count minimal");
    Check(plan.Orientation == (p <= l ? SheetOrientation.Portrait : SheetOrientation.Landscape), "tie rule");
    Check(plan.Pages.Sum(t => t.ArtworkBounds.Width * t.ArtworkBounds.Height) == art.WidthInches * art.HeightInches, "coverage has no overlap or gap");
    foreach (var tile in plan.Pages)
    {
        Check(tile.Index == tile.Row * plan.Columns + tile.Column, "row major");
        Check(tile.ArtworkBounds.Right <= art.WidthInches && tile.ArtworkBounds.Bottom <= art.HeightInches, "partial trim");
        Check(tile.PaperBounds.Width == tile.ArtworkBounds.Width && tile.PaperBounds.Height == tile.ArtworkBounds.Height, "actual size");
        if (tile.Column > 0) Check(plan.Pages[tile.Index - 1].ArtworkBounds.Right == tile.ArtworkBounds.X, "horizontal seam exact");
        if (tile.Row > 0) Check(plan.Pages[tile.Index - plan.Columns].ArtworkBounds.Bottom == tile.ArtworkBounds.Y, "vertical seam exact");
    }
}
foreach (var language in new[] { "en-US", "pl-PL" })
{
    var culture = CultureInfo.GetCultureInfo(language);
    Check(LayoutEngine.TryInches(language == "pl-PL" ? "24,125" : "24.125", culture, out var value) && value == 24.125m, "locale inches");
    foreach (var invalid in new[] { "", "-1", "0", "121", "1e3", "NaN", "1,234.5" }) Check(!LayoutEngine.TryInches(invalid, culture, out _), "reject invalid size");
}
Check(LayoutEngine.Proportional(24, true, 4000, 3000) == new ArtworkSize(24, 18), "width proportion");
Check(LayoutEngine.Proportional(18, false, 4000, 3000) == new ArtworkSize(24, 18), "height proportion");
var fit = LayoutEngine.ImagePlacement(new(24, 24), 4000, 3000, new());
Check(fit == new RectInches(0, 3, 24, 18), "fit includes bands");
var crop = LayoutEngine.ImagePlacement(new(24, 24), 4000, 3000, new(FitMode.Crop));
Check(crop == new RectInches(-4, 0, 32, 24), "crop centered");
Check(LayoutEngine.ImagePlacement(new(24, 24), 4000, 3000, new(FitMode.Crop, CropX: 1)) == crop with { X = -8 }, "crop positioning");
Check(LayoutEngine.ImagePlacement(new(24, 24), 4000, 3000, new(FitMode.Stretch)) == new RectInches(0, 0, 24, 24), "stretch explicit");
Console.WriteLine($"PASS: {checks:N0} geometry checks, 1500 deterministic randomized layouts.");
