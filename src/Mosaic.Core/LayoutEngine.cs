using System.Globalization;

namespace Mosaic.Core;

public static class LayoutEngine
{
    public static LayoutPlan Calculate(ArtworkSize art, PaperSpec paper)
    {
        if (art.WidthInches <= 0 || art.HeightInches <= 0 || art.WidthInches > 120 || art.HeightInches > 120)
            throw new ArgumentOutOfRangeException(nameof(art));
        if (paper.WidthInches <= 0 || paper.HeightInches <= 0 || paper.MarginInches < 0 ||
            paper.MarginInches * 2 >= Math.Min(paper.WidthInches, paper.HeightInches))
            throw new ArgumentOutOfRangeException(nameof(paper));
        var shortSide = Math.Min(paper.WidthInches, paper.HeightInches);
        var longSide = Math.Max(paper.WidthInches, paper.HeightInches);
        int Count(decimal w, decimal h) => checked((int)decimal.Ceiling(art.WidthInches / (w - 2 * paper.MarginInches)) *
            (int)decimal.Ceiling(art.HeightInches / (h - 2 * paper.MarginInches)));
        int pc = Count(shortSide, longSide), lc = Count(longSide, shortSide);
        var orientation = paper.Orientation switch
        {
            OrientationMode.Portrait => SheetOrientation.Portrait,
            OrientationMode.Landscape => SheetOrientation.Landscape,
            _ => pc <= lc ? SheetOrientation.Portrait : SheetOrientation.Landscape
        };
        var pw = orientation == SheetOrientation.Portrait ? shortSide : longSide;
        var ph = orientation == SheetOrientation.Portrait ? longSide : shortSide;
        var tw = pw - 2 * paper.MarginInches;
        var th = ph - 2 * paper.MarginInches;
        int cols = (int)decimal.Ceiling(art.WidthInches / tw), rows = (int)decimal.Ceiling(art.HeightInches / th);
        if ((long)cols * rows > 400) throw new ArgumentOutOfRangeException(nameof(paper));
        var pages = new List<PageTile>();
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                decimal x = col * tw, y = row * th;
                decimal w = Math.Min(tw, art.WidthInches - x), h = Math.Min(th, art.HeightInches - y);
                pages.Add(new(pages.Count, row, col, new(x, y, w, h), new(paper.MarginInches, paper.MarginInches, w, h)));
            }
        return new(art, paper, orientation, pw, ph, tw, th, rows, cols, pc, lc, pages.AsReadOnly());
    }

    // The only image-to-artwork transform. All adapters use this rectangle and the same clipping bounds.
    public static RectInches ImagePlacement(ArtworkSize art, int imageWidth, int imageHeight, Composition composition)
    {
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (composition.Mode == FitMode.Stretch) return new(0, 0, art.WidthInches, art.HeightInches);
        decimal sx = art.WidthInches / imageWidth, sy = art.HeightInches / imageHeight;
        decimal scale = composition.Mode == FitMode.Crop ? Math.Max(sx, sy) : Math.Min(sx, sy);
        decimal w = imageWidth * scale, h = imageHeight * scale;
        return new((art.WidthInches - w) * (composition.Mode == FitMode.Crop ? composition.CropX : .5m),
            (art.HeightInches - h) * (composition.Mode == FitMode.Crop ? composition.CropY : .5m), w, h);
    }

    public static bool TryInches(string text, CultureInfo culture, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            culture, out value) && value > 0 && value <= 120;

    public static ArtworkSize Proportional(decimal edited, bool widthEdited, int width, int height) => widthEdited
        ? new(edited, edited * height / width) : new(edited * width / height, edited);
}
