using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mosaic.Core;

namespace Mosaic.App;

public sealed class PreviewSurface : FrameworkElement
{
    public BitmapSource? Image { get; set; }
    public LayoutPlan? Layout { get; set; }
    public Composition Composition { get; set; } = new();
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public bool ShowGrid { get; set; } = true;
    public double Zoom { get; private set; } = 1;
    private Vector pan;
    private Point? last;
    public PreviewSurface()
    {
        ClipToBounds = true; Focusable = true; Cursor = Cursors.Hand;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        MouseWheel += (_, e) => { Zoom = Math.Clamp(Zoom * (e.Delta > 0 ? 1.18 : 1 / 1.18), 1, 12); if (Zoom == 1) pan = default; InvalidateVisual(); e.Handled = true; };
        MouseLeftButtonDown += (_, e) => { Focus(); last = e.GetPosition(this); CaptureMouse(); };
        MouseMove += (_, e) => { if (last is { } p && IsMouseCaptured) { var now = e.GetPosition(this); pan += now - p; last = now; InvalidateVisual(); } };
        MouseLeftButtonUp += (_, _) => { last = null; ReleaseMouseCapture(); };
        KeyDown += (_, e) => { if (e.Key == Key.Home) Fit(); if (e.Key == Key.Add || e.Key == Key.OemPlus) Detail(); if (e.Key == Key.Subtract || e.Key == Key.OemMinus) Fit(); };
    }
    public void Fit() { Zoom = 1; pan = default; InvalidateVisual(); }
    public void Detail() { Zoom = Zoom == 1 ? 3 : 1; pan = default; InvalidateVisual(); }

    // Invert the same artwork-to-paper translation used by the output adapters.
    // Margin strips overlap in this assembled view: they are discarded, not extra artwork.
    internal static RectInches SheetInArtwork(LayoutPlan plan, PageTile tile) => new(
        tile.ArtworkBounds.X - tile.PaperBounds.X,
        tile.ArtworkBounds.Y - tile.PaperBounds.Y, plan.PageWidth, plan.PageHeight);

    internal static RectInches PaperExtent(LayoutPlan plan)
    {
        var sheets = plan.Pages.Select(tile => SheetInArtwork(plan, tile)).ToArray();
        decimal left = sheets.Min(s => s.X), top = sheets.Min(s => s.Y);
        return new(left, top, sheets.Max(s => s.Right) - left, sheets.Max(s => s.Bottom) - top);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Image == null || Layout == null || ActualWidth < 20 || ActualHeight < 20) return;
        var plan = Layout;
        var paper = PaperExtent(plan);
        double scale = Math.Min(Math.Max(1, ActualWidth - 78) / (double)paper.Width,
            Math.Max(1, ActualHeight - 78) / (double)paper.Height) * Zoom;
        double x = (ActualWidth - (double)paper.Width * scale) / 2 - (double)paper.X * scale + pan.X;
        double y = (ActualHeight - (double)paper.Height * scale) / 2 - (double)paper.Y * scale + pan.Y;
        Rect View(RectInches bounds) => new(x + (double)bounds.X * scale, y + (double)bounds.Y * scale,
            (double)bounds.Width * scale, (double)bounds.Height * scale);
        var rect = View(new(0, 0, plan.Artwork.WidthInches, plan.Artwork.HeightInches));
        var paperRect = View(paper);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(16, 35, 60, 59)), null,
            new Rect(paperRect.X + 4, paperRect.Y + 7, paperRect.Width, paperRect.Height), 1, 1);
        var unusedPaper = new SolidColorBrush(Color.FromRgb(248, 244, 235));
        foreach (var tile in plan.Pages) dc.DrawRectangle(unusedPaper, null, View(SheetInArtwork(plan, tile)));
        dc.DrawRectangle(Brushes.White, null, rect);
        dc.PushClip(new RectangleGeometry(rect));
        var placement = LayoutEngine.ImagePlacement(plan.Artwork,
            SourceWidth > 0 ? SourceWidth : Image.PixelWidth, SourceHeight > 0 ? SourceHeight : Image.PixelHeight, Composition);
        dc.DrawImage(Image, new Rect(x + (double)placement.X * scale, y + (double)placement.Y * scale, (double)placement.Width * scale, (double)placement.Height * scale));
        if (ShowGrid)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(165, 48, 99, 89)), 1) { DashStyle = new DashStyle(new double[] { 5, 4 }, 0) };
            foreach (var tile in plan.Pages)
            {
                var r = View(tile.ArtworkBounds);
                dc.DrawRectangle(null, pen, r);
            }
        }
        dc.Pop();

        // Expose the complete sheet boundaries only on unused paper. Drawing margin
        // outlines through the assembled artwork would suggest duplicated image strips.
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(RenderSize)), new RectangleGeometry(rect)));
        var sheetPen = new Pen(new SolidColorBrush(Color.FromRgb(207, 201, 187)), 1);
        foreach (var tile in plan.Pages) dc.DrawRectangle(null, sheetPen, View(SheetInArtwork(plan, tile)));
        dc.Pop();
        var trimPen = new Pen(new SolidColorBrush(Color.FromRgb(146, 164, 150)), 1);
        if (ShowGrid) trimPen.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
        dc.DrawRectangle(null, trimPen, rect);

        // Partial edge tiles may be narrower than a page label. Let the label extend
        // onto its own unused paper, without clipping the number at the finished edge.
        if (ShowGrid)
        {
            foreach (var tile in plan.Pages)
            {
                var r = View(tile.ArtworkBounds);
                var sheet = View(SheetInArtwork(plan, tile));
                var label = new FormattedText($"{tile.Row + 1}.{tile.Column + 1}", Lang.Culture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13, new SolidColorBrush(Color.FromRgb(35, 60, 59)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double labelWidth = label.Width + 14;
                double left = Math.Min(r.X + 8, Math.Max(sheet.Left + 2, sheet.Right - labelWidth - 2));
                double top = Math.Min(r.Y + 8, Math.Max(sheet.Top + 2, sheet.Bottom - 27));
                var lr = new Rect(left, top, labelWidth, 25);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(228, 255, 255, 255)), null, lr, 5, 5);
                dc.DrawText(label, new Point(lr.X + 7, lr.Y + 3));
            }
        }
    }
}
