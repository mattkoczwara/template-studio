using System.Globalization;
using System.IO;
using Mosaic.Core;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mosaic.Output;

public sealed class PdfExporter
{
    private static readonly object FontLock = new();
    private static bool _fontsReady;

    public Task<PdfExportResult> ExportAsync(RenderSnapshot snapshot, string destination, string language = "en",
        IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = new PdfDocument();
        document.Info.Title = snapshot.State.Title;
        document.Info.Creator = "Mosaic Template Studio 1.0";
        document.Info.Subject = "Actual size / 100% — nominal physical geometry";
        using var grayStream = snapshot.State.Grayscale ? GrayscalePng(snapshot.ImagePath, cancellationToken) : null;
        using var image = grayStream is null ? XImage.FromFile(snapshot.ImagePath) : XImage.FromStream(grayStream);
        image.Interpolate = true;
        int count = snapshot.Layout.PageCount + (snapshot.State.AssemblyGuide ? 1 : 0);
        foreach (var tile in snapshot.Layout.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = AddPage(document, snapshot.Layout.PageWidth, snapshot.Layout.PageHeight);
            using var graphics = XGraphics.FromPdfPage(page);
            var drawing = OutputGeometry.Describe(snapshot, tile, image.PixelWidth, image.PixelHeight);
            var saved = graphics.Save();
            graphics.IntersectClip(ToPoints(drawing.Clip));
            var rect = ToPoints(drawing.Image);
            graphics.DrawImage(image, rect.X, rect.Y, rect.Width, rect.Height);
            graphics.Restore(saved);
            if (snapshot.Layout.Paper.MarginInches > 0) DrawTrimMarks(graphics, tile.PaperBounds, snapshot.Layout);
            progress?.Report(new((tile.Index + 1d) / (count + 1), "export"));
        }
        if (snapshot.State.AssemblyGuide) AddAssemblyGuide(document, snapshot, language);
        return SaveAtomic(document, destination, count, cancellationToken, progress);
    }, cancellationToken);

    private static MemoryStream GrayscalePng(string path, CancellationToken token)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        token.ThrowIfCancellationRequested();
        var gray = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Gray8, null, 0);
        gray.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(gray));
        var result = new MemoryStream();
        encoder.Save(result);
        result.Position = 0;
        token.ThrowIfCancellationRequested();
        return result;
    }

    public Task<PdfExportResult> ExportCalibrationAsync(string destination, PaperSpec paper, string language = "en",
        IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var layout = CalibrationFixture.Layout(paper);
        using var document = new PdfDocument();
        document.Info.Title = OutputText.Pick(language, "Mosaic printer calibration — four sheets", "Kalibracja drukarki — cztery arkusze");
        foreach (var tile in layout.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = AddPage(document, layout.PageWidth, layout.PageHeight);
            using var graphics = XGraphics.FromPdfPage(page);
            DrawCalibration(graphics, layout, tile, language);
            progress?.Report(new((tile.Index + 1d) / 5, "export"));
        }
        return SaveAtomic(document, destination, 4, cancellationToken, progress);
    }, cancellationToken);

    private static PdfExportResult SaveAtomic(PdfDocument document, string destination, int count,
        CancellationToken token, IProgress<JobProgress>? progress)
    {
        string path = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, ".mosaic-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            token.ThrowIfCancellationRequested();
            document.Save(temp);
            token.ThrowIfCancellationRequested();
            // Reopen our completed file before replacing any existing user output.
            using (var check = PdfSharp.Pdf.IO.PdfReader.Open(temp, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                if (check.PageCount != count) throw new InvalidDataException("PDF validation failed.");
            File.Move(temp, path, true);
            progress?.Report(new(1, "export"));
            return new(path, count, new FileInfo(path).Length);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static PdfPage AddPage(PdfDocument document, decimal width, decimal height)
    {
        var page = document.AddPage();
        page.Width = XUnit.FromPoint((double)(width * 72));
        page.Height = XUnit.FromPoint((double)(height * 72));
        return page;
    }

    private static XRect ToPoints(RectInches rect) => new((double)(rect.X * 72), (double)(rect.Y * 72),
        (double)(rect.Width * 72), (double)(rect.Height * 72));

    private static XFont Font(double size, bool bold = false)
    {
        lock (FontLock)
        {
            if (!_fontsReady)
            {
                if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new WindowsFontResolver();
                _fontsReady = true;
            }
        }
        return new XFont("Mosaic Sans", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
    }

    private static void DrawTrimMarks(XGraphics g, RectInches bounds, LayoutPlan layout)
    {
        var pen = new XPen(XColors.Gray, .35);
        var r = ToPoints(bounds);
        double length = Math.Min(7, (double)layout.Paper.MarginInches * 72 * .65);
        foreach (double x in new[] { r.Left, r.Right })
            foreach (double y in new[] { r.Top, r.Bottom })
            {
                double dirX = x == r.Left ? -1 : 1, dirY = y == r.Top ? -1 : 1;
                g.DrawLine(pen, x + dirX * 1.5, y, x + dirX * length, y);
                g.DrawLine(pen, x, y + dirY * 1.5, x, y + dirY * length);
            }
    }

    private static void AddAssemblyGuide(PdfDocument document, RenderSnapshot snapshot, string language)
    {
        var page = AddPage(document, 8.5m, 11m);
        using var g = XGraphics.FromPdfPage(page);
        var heading = Font(24, true); var body = Font(11); var small = Font(9);
        var accent = new XSolidBrush(XColor.FromArgb(42, 91, 77));
        g.DrawString(OutputText.Pick(language, "Your assembly guide", "Plan łączenia arkuszy"), heading, accent, 42, 58);
        var culture = CultureInfo.GetCultureInfo(OutputText.Polish(language) ? "pl-PL" : "en-US");
        string size = $"{snapshot.Layout.Artwork.WidthInches.ToString("0.###", culture)} × {snapshot.Layout.Artwork.HeightInches.ToString("0.###", culture)}";
        g.DrawString(size + OutputText.Pick(language, " in · ", " cala · ") + snapshot.Layout.PageCount + OutputText.Pick(language, " sheets", " arkuszy"), body, XBrushes.Black, 42, 84);
        string[] lines = snapshot.Layout.Paper.MarginInches > 0
            ? new[] {
                OutputText.Pick(language, "Print artwork pages at Actual size / 100%, one-sided.", "Drukuj strony wzoru jednostronnie w skali 100% (rozmiar rzeczywisty)."),
                OutputText.Pick(language, "Cut along the corner marks. Remove all blank margins.", "Przytnij według znaczników w narożnikach. Usuń puste marginesy."),
                OutputText.Pick(language, "Join cut edges in the order below. Do not overlap the artwork.", "Połącz przycięte krawędzie zgodnie z planem. Nie nakładaj obrazu.") }
            : new[] {
                OutputText.Pick(language, "Print artwork pages at Actual size / 100%, one-sided, borderless.", "Drukuj strony wzoru jednostronnie, bez marginesów, w skali 100%."),
                OutputText.Pick(language, "Join adjacent sheet edges. Do not overlap the artwork.", "Połącz sąsiednie krawędzie arkuszy. Nie nakładaj obrazu."),
                OutputText.Pick(language, "Trim unused paper only at the outside of the complete template.", "Usuń zbędny papier tylko na zewnętrznym obrysie całego wzoru.") };
        double y = 115;
        foreach (var line in lines) { g.DrawString(line, body, XBrushes.Black, 42, y); y += 20; }
        g.DrawString(OutputText.Pick(language, "Read left to right, then top to bottom. Guide is not part of the template.",
            "Kolejność: od lewej do prawej, rząd po rzędzie. Plan nie jest częścią wzoru."), small, XBrushes.DimGray, 42, 189);
        double maxWidth = 528, maxHeight = 470;
        double scale = Math.Min(maxWidth / (double)(snapshot.Layout.Columns * snapshot.Layout.PageWidth),
            maxHeight / (double)(snapshot.Layout.Rows * snapshot.Layout.PageHeight));
        foreach (var tile in snapshot.Layout.Pages)
        {
            double w = (double)snapshot.Layout.PageWidth * scale, h = (double)snapshot.Layout.PageHeight * scale;
            var box = new XRect(42 + tile.Column * w, 210 + tile.Row * h, w, h);
            g.DrawRectangle(new XPen(XColors.DarkGray, .6), new XSolidBrush(XColor.FromArgb(248, 249, 246)), box);
            g.DrawString($"{tile.Index + 1} · {tile.Row + 1},{tile.Column + 1}", small, accent, box, XStringFormats.Center);
            // Dashed artwork extent distinguishes unused outside paper from real template.
            var actual = new XRect(box.X + (double)tile.PaperBounds.X * scale, box.Y + (double)tile.PaperBounds.Y * scale,
                (double)tile.PaperBounds.Width * scale, (double)tile.PaperBounds.Height * scale);
            g.DrawRectangle(new XPen(XColor.FromArgb(42, 91, 77), .7) { DashStyle = XDashStyle.Dash }, actual);
        }
        g.DrawString(OutputText.Pick(language, "A ruler test is required: driver expansion may crop page edges.",
            "Sprawdź wydruk linijką: sterownik może powiększać obraz i obcinać krawędzie."), small, XBrushes.DimGray, 42, 728);
        if (snapshot.State.Composition.Mode == FitMode.Fit)
            g.DrawString(OutputText.Pick(language, "Fit preserves the whole image; blank bands can lie inside the requested rectangle.",
                "Dopasowanie zachowuje cały obraz; wewnątrz wzoru mogą pozostać puste pasy."), small, XBrushes.DimGray, 42, 746);
    }

    internal static void DrawCalibration(XGraphics g, LayoutPlan layout, PageTile tile, string language)
    {
        var clip = ToPoints(tile.PaperBounds);
        var saved = g.Save();
        g.IntersectClip(clip);
        // Global coordinate grid crosses every page boundary without a phase reset.
        decimal ox = tile.PaperBounds.X - tile.ArtworkBounds.X, oy = tile.PaperBounds.Y - tile.ArtworkBounds.Y;
        for (int x = 0; x <= decimal.Ceiling(layout.Artwork.WidthInches); x++)
            g.DrawLine(new XPen(XColors.LightGray, .4), (double)((ox + x) * 72), clip.Top, (double)((ox + x) * 72), clip.Bottom);
        for (int y = 0; y <= decimal.Ceiling(layout.Artwork.HeightInches); y++)
            g.DrawLine(new XPen(XColors.LightGray, .4), clip.Left, (double)((oy + y) * 72), clip.Right, (double)((oy + y) * 72));
        // Diagonals and offset circles make edge loss and translation visible at the central join.
        var red = new XPen(XColors.Firebrick, 1);
        g.DrawLine(red, (double)(ox * 72), (double)(oy * 72), (double)((ox + layout.Artwork.WidthInches) * 72), (double)((oy + layout.Artwork.HeightInches) * 72));
        g.DrawLine(red, (double)(ox * 72), (double)((oy + layout.Artwork.HeightInches) * 72), (double)((ox + layout.Artwork.WidthInches) * 72), (double)(oy * 72));
        decimal cx = layout.TileWidth, cy = layout.TileHeight;
        foreach (decimal radius in new[] { .125m, .25m, .5m, 1m })
            g.DrawEllipse(new XPen(XColors.SteelBlue, .8), (double)((ox + cx - radius) * 72), (double)((oy + cy - radius) * 72), (double)(radius * 144), (double)(radius * 144));
        double left = clip.Left + 54, top = clip.Top + 90;
        g.DrawLine(new XPen(XColors.Black, 1.2), left, top, left + 288, top);
        g.DrawLine(new XPen(XColors.Black, 1.2), left, top, left, top + 288);
        for (int i = 0; i <= 16; i++)
        {
            double tick = i % 4 == 0 ? 9 : 4;
            g.DrawLine(new XPen(XColors.Black, .8), left + i * 18, top - tick, left + i * 18, top + tick);
            g.DrawLine(new XPen(XColors.Black, .8), left - tick, top + i * 18, left + tick, top + i * 18);
        }
        var font = Font(11); var small = Font(9);
        g.DrawString(OutputText.Pick(language, "X = 4 inches", "X = 4 cale"), font, XBrushes.Black, left + 90, top - 18);
        g.DrawString(OutputText.Pick(language, "Y = 4 inches", "Y = 4 cale"), font, XBrushes.Black, left + 12, top + 156);
        g.DrawString(OutputText.Pick(language, "CALIBRATION", "KALIBRACJA") + $" · {tile.Index + 1}/4 · {tile.Row + 1},{tile.Column + 1}", Font(16, true), XBrushes.Black, clip.Left + 18, clip.Top + 36);
        string[] text = {
            OutputText.Pick(language, "Actual size / 100%. Measure both 4-inch rulers; repeat twice.", "Skala 100%. Zmierz obie linie długości 4 cali; powtórz dwa razy."),
            OutputText.Pick(language, "One-inch grid. Join all four pages and inspect the central circles.", "Siatka co 1 cal. Połącz 4 arkusze i sprawdź okręgi na łączeniu."),
            OutputText.Pick(language, "Record any missing edge content and alignment drift.", "Zapisz ubytki przy krawędziach i przesunięcia między arkuszami."),
            OutputText.Pick(language, "Record driver, paper, quality, borderless expansion and print pathway.", "Zapisz sterownik, papier, jakość, powiększenie i sposób drukowania.") };
        double by = top + 330;
        foreach (var line in text) { g.DrawString(line, small, XBrushes.Black, clip.Left + 18, by); by += 18; }
        g.Restore(saved);
        if (layout.Paper.MarginInches > 0) DrawTrimMarks(g, tile.PaperBounds, layout);
    }

    private sealed class WindowsFontResolver : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new(bold ? "mosaic-bold" : "mosaic-regular");
        public byte[] GetFont(string faceName)
        {
            string file = faceName == "mosaic-bold" ? "segoeuib.ttf" : "segoeui.ttf";
            return File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file));
        }
    }
}
