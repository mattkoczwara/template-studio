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
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Image == null || Layout == null || ActualWidth < 20 || ActualHeight < 20) return;
        var plan = Layout;
        double w = (double)plan.Artwork.WidthInches, h = (double)plan.Artwork.HeightInches;
        double scale = Math.Min((ActualWidth - 78) / w, (ActualHeight - 78) / h) * Zoom;
        double x = (ActualWidth - w * scale) / 2 + pan.X, y = (ActualHeight - h * scale) / 2 + pan.Y;
        var rect = new Rect(x, y, w * scale, h * scale);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(16, 35, 60, 59)), null, new Rect(x + 4, y + 7, rect.Width, rect.Height), 1, 1);
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
                var b = tile.ArtworkBounds;
                var r = new Rect(x + (double)b.X * scale, y + (double)b.Y * scale, (double)b.Width * scale, (double)b.Height * scale);
                dc.DrawRectangle(null, pen, r);
                var label = new FormattedText($"{tile.Row + 1}.{tile.Column + 1}", Lang.Culture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13, new SolidColorBrush(Color.FromRgb(35, 60, 59)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var lr = new Rect(r.X + 8, r.Y + 8, label.Width + 14, 25);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(228, 255, 255, 255)), null, lr, 5, 5);
                dc.DrawText(label, new Point(lr.X + 7, lr.Y + 3));
            }
        }
        dc.Pop();
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(192, 207, 197)), 1), rect);
    }
}
