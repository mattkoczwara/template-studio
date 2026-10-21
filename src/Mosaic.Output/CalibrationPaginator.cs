using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Mosaic.Core;

namespace Mosaic.Output;

internal sealed class CalibrationPaginator(LayoutPlan layout, string language) : DocumentPaginator
{
    public override bool IsPageCountValid => true;
    public override int PageCount => 4;
    public override Size PageSize { get => new((double)layout.PageWidth * 96, (double)layout.PageHeight * 96); set { } }
    public override IDocumentPaginatorSource? Source => null;
    public override DocumentPage GetPage(int pageNumber)
    {
        if (pageNumber is < 0 or > 3) return DocumentPage.Missing;
        var tile = layout.Pages[pageNumber];
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(PageSize));
            var clip = ArtworkPaginator.ToDips(tile.PaperBounds);
            dc.PushClip(new RectangleGeometry(clip));
            decimal ox = tile.PaperBounds.X - tile.ArtworkBounds.X, oy = tile.PaperBounds.Y - tile.ArtworkBounds.Y;
            for (int x = 0; x <= decimal.Ceiling(layout.Artwork.WidthInches); x++)
                dc.DrawLine(new Pen(Brushes.LightGray, .55), new((double)(ox + x) * 96, clip.Top), new((double)(ox + x) * 96, clip.Bottom));
            for (int y = 0; y <= decimal.Ceiling(layout.Artwork.HeightInches); y++)
                dc.DrawLine(new Pen(Brushes.LightGray, .55), new(clip.Left, (double)(oy + y) * 96), new(clip.Right, (double)(oy + y) * 96));
            var red = new Pen(Brushes.Firebrick, 1.33);
            dc.DrawLine(red, new((double)ox * 96, (double)oy * 96), new((double)(ox + layout.Artwork.WidthInches) * 96, (double)(oy + layout.Artwork.HeightInches) * 96));
            dc.DrawLine(red, new((double)ox * 96, (double)(oy + layout.Artwork.HeightInches) * 96), new((double)(ox + layout.Artwork.WidthInches) * 96, (double)oy * 96));
            foreach (decimal radius in new[] { .125m, .25m, .5m, 1m })
                dc.DrawEllipse(null, new Pen(Brushes.SteelBlue, 1.07), new((double)(ox + layout.TileWidth) * 96, (double)(oy + layout.TileHeight) * 96), (double)radius * 96, (double)radius * 96);
            double left = clip.Left + 72, top = clip.Top + 120;
            dc.DrawLine(new Pen(Brushes.Black, 1.6), new(left, top), new(left + 384, top));
            dc.DrawLine(new Pen(Brushes.Black, 1.6), new(left, top), new(left, top + 384));
            for (int i = 0; i <= 16; i++)
            {
                double tick = i % 4 == 0 ? 12 : 5.33;
                dc.DrawLine(new Pen(Brushes.Black, 1.07), new(left + i * 24, top - tick), new(left + i * 24, top + tick));
                dc.DrawLine(new Pen(Brushes.Black, 1.07), new(left - tick, top + i * 24), new(left + tick, top + i * 24));
            }
            Text(dc, OutputText.Pick(language, "X = 4 inches", "X = 4 cale"), left + 120, top - 36, 14.67);
            Text(dc, OutputText.Pick(language, "Y = 4 inches", "Y = 4 cale"), left + 16, top + 194, 14.67);
            Text(dc, OutputText.Pick(language, "CALIBRATION", "KALIBRACJA") + $" · {tile.Index + 1}/4 · {tile.Row + 1},{tile.Column + 1}", clip.Left + 24, clip.Top + 24, 21.33);
            string[] text = {
                OutputText.Pick(language, "Actual size / 100%. Measure both 4-inch rulers; repeat twice.", "Skala 100%. Zmierz obie linie długości 4 cali; powtórz dwa razy."),
                OutputText.Pick(language, "One-inch grid. Join four sheets; inspect the central circles.", "Siatka co 1 cal. Połącz 4 arkusze i sprawdź okręgi na łączeniu."),
                OutputText.Pick(language, "Record any missing edge content and alignment drift.", "Zapisz ubytki przy krawędziach i przesunięcia między arkuszami."),
                OutputText.Pick(language, "Record driver, paper, quality, expansion and print pathway.", "Zapisz sterownik, papier, jakość, powiększenie i sposób druku.") };
            double yText = top + 424;
            foreach (var line in text) { Text(dc, line, clip.Left + 24, yText, 12); yText += 24; }
            dc.Pop();
            if (layout.Paper.MarginInches > 0) PrintDrawing.TrimMarks(dc, tile.PaperBounds, layout.Paper.MarginInches);
        }
        return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
    }
    private void Text(DrawingContext dc, string value, double x, double y, double size) =>
        dc.DrawText(new FormattedText(value, CultureInfo.GetCultureInfo(OutputText.Polish(language) ? "pl-PL" : "en-US"),
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Black, 1), new(x, y));
}
