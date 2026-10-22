using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mosaic.App;

/// <summary>Two output alternatives share one artwork-space viewport, independently of pixel dimensions.</summary>
internal sealed class ComparisonWindow : Window
{
    private readonly ComparisonViewport viewport;
    private readonly InspectionSurface left, right;
    private readonly OverviewSurface overview;
    private readonly TextBlock scale = new(), status = new();
    private readonly bool polish;
    private readonly Func<Rect, CancellationToken, Task<(BitmapSource Standard, BitmapSource Enhanced)>>? loadRegion;
    private CancellationTokenSource? detailJob;
    private long generation;
    private bool closed;
    internal ComparisonViewport Viewport => viewport;
    internal FrameworkElement StandardSurface => left;
    internal FrameworkElement EnhancedSurface => right;
    internal FrameworkElement Overview => overview;
    internal string DetailStatus => status.Text;
    internal Rect? StandardDetailRegion => left.DetailRegion;
    internal Rect? EnhancedDetailRegion => right.DetailRegion;

    /// <param name="loadRegion">Loads a normalized artwork rectangle from the actual output files off the UI thread.
    /// Returned bitmaps must be frozen; both must represent the same requested rectangle. No enlargement is needed.</param>
    internal ComparisonWindow(Window owner, BitmapSource standardOverview, BitmapSource enhancedOverview, bool polish,
        Func<Rect, CancellationToken, Task<(BitmapSource Standard, BitmapSource Enhanced)>>? loadRegion = null, int enhancedPixelWidth = 0)
    {
        this.polish = polish; this.loadRegion = loadRegion;
        viewport = new((double)standardOverview.PixelWidth / standardOverview.PixelHeight);
        Owner = owner; Title = T("Inspect enhancement", "Porównaj ulepszenie");
        Style = (Style)Application.Current.Resources[typeof(Window)];
        Width = 1180; Height = 790; MinWidth = 720; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new Grid { Margin = new Thickness(24) };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new());
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = body;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(new TextBlock { Text = Title, FontFamily = new FontFamily("Georgia"), FontSize = 28 });
        heading.Children.Add(new TextBlock { Text = T("Choose a region below. Zoom and drag either image; both views move together.",
            "Wybierz fragment na dole. Powiększaj i przeciągaj dowolny obraz — oba widoki przesuwają się razem."),
            Foreground = ResourceBrush("Muted"), FontSize = 15, Margin = new Thickness(0, 8, 0, 0), LineHeight = 22 });
        body.Children.Add(heading);
        var panes = new Grid(); panes.ColumnDefinitions.Add(new()); panes.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); panes.ColumnDefinitions.Add(new());
        Grid.SetIsSharedSizeScope(panes, true); Grid.SetRow(panes, 1); body.Children.Add(panes);
        left = new(viewport, standardOverview); right = new(viewport, enhancedOverview);
        AddPane(panes, left, 0, T("Original · Standard enlargement", "Oryginał · Standardowe powiększenie"));
        AddPane(panes, right, 2, T("Enhanced", "Ulepszona"));
        foreach (var surface in new[] { left, right })
        {
            surface.ViewChanged += ViewChanged;
            surface.SizeChanged += (_, _) => ResizeViewport();
        }
        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(160) }); footer.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); footer.ColumnDefinitions.Add(new());
        Grid.SetRow(footer, 2); body.Children.Add(footer);
        overview = new(viewport, standardOverview) { Height = 104 };
        overview.SelectionChanged += ViewChanged;
        AutomationProperties.SetName(overview, T("Select image region", "Wybierz fragment obrazu"));
        footer.Children.Add(new Border { Child = overview, Background = Brushes.White, BorderBrush = ResourceBrush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true });
        var tools = new StackPanel(); Grid.SetColumn(tools, 2); footer.Children.Add(tools);
        var actions = new WrapPanel(); tools.Children.Add(actions);
        AddButton(actions, T("Fit", "Całość"), () => { viewport.Fit(); ViewChanged(); });
        AddButton(actions, "−", () => { viewport.ChangeZoom(1 / 1.5); ViewChanged(); }, T("Zoom out", "Pomniejsz"));
        AddButton(actions, "+", () => { viewport.ChangeZoom(1.5); ViewChanged(); }, T("Zoom in", "Powiększ"));
        AddButton(actions, T("Pixel detail", "Szczegóły pikseli"), () => { if (enhancedPixelWidth > 0) viewport.SetNativePixelScale(enhancedPixelWidth, VisualTreeHelper.GetDpi(left).PixelsPerDip); else viewport.SetZoom(4); ViewChanged(); });
        scale.VerticalAlignment = VerticalAlignment.Center; scale.FontSize = 15; scale.Margin = new Thickness(6, 0, 12, 6); actions.Children.Add(scale);
        AddButton(actions, T("Close", "Zamknij"), Close);
        status.FontSize = 14; status.Foreground = ResourceBrush("Muted"); status.LineHeight = 21;
        status.Margin = new Thickness(0, 5, 0, 0); tools.Children.Add(status);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        Loaded += (_, _) => { App.FitOnScreen(this); ResizeViewport(); left.Focus(); };
        Closed += (_, _) => { closed = true; generation++; detailJob?.Cancel(); detailJob?.Dispose(); detailJob = null; };
        ViewChanged();
    }

    private string T(string english, string polishText) => polish ? polishText : english;
    private static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];
    private static void AddButton(Panel parent, string text, Action action, string? accessibleName = null)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(13, 7, 13, 7) };
        AutomationProperties.SetName(button, accessibleName ?? text); if (accessibleName != null) button.ToolTip = accessibleName;
        button.Click += (_, _) => action(); parent.Children.Add(button);
    }
    private void AddPane(Grid panes, InspectionSurface surface, int column, string label)
    {
        var pane = new Grid(); pane.RowDefinitions.Add(new() { Height = GridLength.Auto, SharedSizeGroup = "ComparisonLabels" }); pane.RowDefinitions.Add(new());
        Grid.SetColumn(pane, column); panes.Children.Add(pane);
        pane.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 9), FontSize = 17 });
        var shell = new Border { Child = surface, Background = Brushes.White, BorderBrush = ResourceBrush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true };
        Grid.SetRow(shell, 1); pane.Children.Add(shell);
        AutomationProperties.SetName(surface, label);
        surface.ToolTip = T("Wheel: zoom · Drag: move · Arrow keys: move · Home: fit · + / −: zoom",
            "Kółko: powiększanie · Przeciąganie: przesuwanie · Strzałki: przesuwanie · Home: całość · + / −: powiększanie");
    }
    private void ResizeViewport()
    {
        if (left.ActualWidth <= 0 || right.ActualWidth <= 0) return;
        viewport.SetSize(new(Math.Min(left.ActualWidth, right.ActualWidth), Math.Min(left.ActualHeight, right.ActualHeight)));
        ViewChanged();
    }
    private void ViewChanged()
    {
        left.InvalidateVisual(); right.InvalidateVisual(); overview?.InvalidateVisual();
        scale.Text = viewport.Zoom.ToString("0.0", polish ? System.Globalization.CultureInfo.GetCultureInfo("pl-PL") : System.Globalization.CultureInfo.GetCultureInfo("en-US")) + "×";
        if (!closed) _ = RefreshDetailAsync();
    }
    private async Task RefreshDetailAsync()
    {
        detailJob?.Cancel(); detailJob?.Dispose(); detailJob = null;
        long request = ++generation;
        if (loadRegion == null || viewport.ViewSize.Width <= 1)
        {
            status.Text = T("Same region and scale. Standard uses conventional enlargement.", "Ten sam fragment i skala. Standard korzysta ze zwykłego powiększania.");
            return;
        }
        var cancellation = new CancellationTokenSource(); detailJob = cancellation;
        var token = cancellation.Token;
        try
        {
            await Task.Delay(140, token);
            if (request != generation || closed) return;
            status.Text = T("Loading image detail…", "Wczytywanie szczegółów obrazu…");
            Rect region = viewport.VisibleRegion;
            var result = await loadRegion(region, token);
            if (request != generation || closed || token.IsCancellationRequested) return;
            left.SetDetail(result.Standard, region); right.SetDetail(result.Enhanced, region);
            status.Text = T("Same scale, actual output images. AI can change fine marks; check contours and colors before choosing.",
                "Ta sama skala, obrazy do wydruku. AI może zmieniać drobne kreski; przed wyborem sprawdź kontury i kolory.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (request == generation && !closed)
            {
                LocalLog.Write("comparison_detail_failure", ex.GetType().Name);
                status.Text = T("Detail could not be loaded. The overview remains available; close and try again.",
                    "Nie udało się wczytać szczegółów. Podgląd jest nadal dostępny; zamknij okno i spróbuj ponownie.");
            }
        }
        finally
        {
            if (ReferenceEquals(detailJob, cancellation)) { detailJob = null; cancellation.Dispose(); }
        }
    }

    private sealed class InspectionSurface : FrameworkElement
    {
        private readonly ComparisonViewport viewport;
        private readonly BitmapSource overview;
        private BitmapSource? detail;
        private Rect detailRegion;
        private Point? last;
        internal event Action? ViewChanged;
        internal Rect? DetailRegion => detail == null ? null : detailRegion;
        internal InspectionSurface(ComparisonViewport viewport, BitmapSource overview)
        {
            this.viewport = viewport; this.overview = overview;
            ClipToBounds = true; Focusable = true; Cursor = Cursors.Hand;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            MouseWheel += (_, e) => { viewport.ZoomAt(e.GetPosition(this), RenderSize, e.Delta > 0 ? 1.25 : .8); ViewChanged?.Invoke(); e.Handled = true; };
            MouseLeftButtonDown += (_, e) => { Focus(); last = e.GetPosition(this); CaptureMouse(); e.Handled = true; };
            MouseMove += (_, e) => { if (last is { } previous && IsMouseCaptured) { Point current = e.GetPosition(this); viewport.Pan(current - previous); last = current; ViewChanged?.Invoke(); } };
            MouseLeftButtonUp += (_, e) => { last = null; ReleaseMouseCapture(); e.Handled = true; };
            LostMouseCapture += (_, _) => last = null;
            KeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case Key.Home: viewport.Fit(); break;
                    case Key.Add: case Key.OemPlus: viewport.ChangeZoom(1.25); break;
                    case Key.Subtract: case Key.OemMinus: viewport.ChangeZoom(.8); break;
                    case Key.Left: viewport.Pan(new Vector(60, 0)); break;
                    case Key.Right: viewport.Pan(new Vector(-60, 0)); break;
                    case Key.Up: viewport.Pan(new Vector(0, 60)); break;
                    case Key.Down: viewport.Pan(new Vector(0, -60)); break;
                    default: return;
                }
                ViewChanged?.Invoke(); e.Handled = true;
            };
        }
        internal void SetDetail(BitmapSource bitmap, Rect region) { detail = bitmap; detailRegion = region; InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
            Rect image = viewport.ImageBounds(RenderSize); if (image.IsEmpty || image.Width <= 0) return;
            dc.DrawImage(overview, image);
            if (detail != null)
                dc.DrawImage(detail, new(image.X + detailRegion.X * image.Width, image.Y + detailRegion.Y * image.Height, detailRegion.Width * image.Width, detailRegion.Height * image.Height));
        }
    }

    private sealed class OverviewSurface : FrameworkElement
    {
        private readonly ComparisonViewport viewport;
        private readonly BitmapSource image;
        internal event Action? SelectionChanged;
        internal OverviewSurface(ComparisonViewport viewport, BitmapSource image)
        {
            this.viewport = viewport; this.image = image; Cursor = Cursors.Cross; ClipToBounds = true;
            MouseLeftButtonDown += (_, e) => { CaptureMouse(); Select(e.GetPosition(this)); e.Handled = true; };
            MouseMove += (_, e) => { if (IsMouseCaptured) Select(e.GetPosition(this)); };
            MouseLeftButtonUp += (_, e) => { ReleaseMouseCapture(); e.Handled = true; };
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        }
        private Rect Bounds
        {
            get
            {
                double width = Math.Max(1, Math.Min(ActualWidth - 12, (ActualHeight - 12) * viewport.AspectRatio));
                return new((ActualWidth - width) / 2, (ActualHeight - width / viewport.AspectRatio) / 2, width, width / viewport.AspectRatio);
            }
        }
        private void Select(Point point)
        {
            Rect bounds = Bounds;
            if (viewport.Zoom < 2) viewport.SetZoom(4);
            viewport.Select(new((point.X - bounds.X) / bounds.Width, (point.Y - bounds.Y) / bounds.Height));
            SelectionChanged?.Invoke();
        }
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
            Rect bounds = Bounds; dc.DrawImage(image, bounds);
            Rect region = viewport.VisibleRegion;
            var selected = new Rect(bounds.X + region.X * bounds.Width, bounds.Y + region.Y * bounds.Height, region.Width * bounds.Width, region.Height * bounds.Height);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(25, 40, 107, 92)), new Pen(ResourceBrush("Accent"), 2), selected);
        }
    }
}

/// <summary>Normalized artwork coordinates keep differently sized output assets aligned.</summary>
internal sealed class ComparisonViewport
{
    internal double AspectRatio { get; }
    internal double Zoom { get; private set; } = 1;
    internal Point Center { get; private set; } = new(.5, .5);
    internal Size ViewSize { get; private set; } = new(1, 1);
    internal double ImageWidth => Math.Min(ViewSize.Width, ViewSize.Height * AspectRatio) * Zoom;
    internal ComparisonViewport(double aspectRatio)
    {
        if (!double.IsFinite(aspectRatio) || aspectRatio <= 0) throw new ArgumentOutOfRangeException(nameof(aspectRatio));
        AspectRatio = aspectRatio;
    }
    internal void SetSize(Size size) { ViewSize = new(Math.Max(1, size.Width), Math.Max(1, size.Height)); ClampCenter(); }
    internal void Fit() { Zoom = 1; Center = new(.5, .5); }
    internal void SetZoom(double zoom) { Zoom = Math.Clamp(zoom, 1, 256); ClampCenter(); }
    internal void ChangeZoom(double factor) => SetZoom(Zoom * factor);
    internal void SetNativePixelScale(int pixelWidth, double devicePixelsPerDip) => SetZoom(pixelWidth / (ImageWidth / Zoom * devicePixelsPerDip));
    internal void Select(Point point) { Center = point; ClampCenter(); }
    internal void Pan(Vector delta)
    {
        Center = new(Center.X - delta.X / ImageWidth, Center.Y - delta.Y / (ImageWidth / AspectRatio)); ClampCenter();
    }
    internal void ZoomAt(Point point, Size surfaceSize, double factor)
    {
        double before = ImageWidth;
        double dx = point.X - surfaceSize.Width / 2, dy = point.Y - surfaceSize.Height / 2;
        Point anchor = new(Center.X + dx / before, Center.Y + dy / (before / AspectRatio));
        Zoom = Math.Clamp(Zoom * factor, 1, 256);
        Center = new(anchor.X - dx / ImageWidth, anchor.Y - dy / (ImageWidth / AspectRatio)); ClampCenter();
    }
    internal Rect ImageBounds(Size size) => new(size.Width / 2 - Center.X * ImageWidth, size.Height / 2 - Center.Y * ImageWidth / AspectRatio, ImageWidth, ImageWidth / AspectRatio);
    internal Rect VisibleRegion
    {
        get
        {
            double width = Math.Min(1, ViewSize.Width / ImageWidth), height = Math.Min(1, ViewSize.Height / (ImageWidth / AspectRatio));
            return new(Math.Clamp(Center.X - width / 2, 0, 1 - width), Math.Clamp(Center.Y - height / 2, 0, 1 - height), width, height);
        }
    }
    private void ClampCenter()
    {
        double halfWidth = Math.Min(.5, ViewSize.Width / ImageWidth / 2), halfHeight = Math.Min(.5, ViewSize.Height / (ImageWidth / AspectRatio) / 2);
        Center = new(Math.Clamp(Center.X, halfWidth, 1 - halfWidth), Math.Clamp(Center.Y, halfHeight, 1 - halfHeight));
    }
}
