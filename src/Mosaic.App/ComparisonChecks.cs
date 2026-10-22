using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mosaic.App;

/// <summary>In-process WPF interaction checks. Fixtures validate navigation, not model quality.</summary>
internal static class ComparisonChecks
{
    internal static async Task<int> RunAsync(Window owner, string directory)
    {
        Directory.CreateDirectory(directory);
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Comparison: " + description);
            count++;
        }
        var model = new ComparisonViewport(640d / 418);
        model.SetSize(new(500, 400));
        Check(model.VisibleRegion == new Rect(0, 0, 1, 1), "fit shows the entire artwork");
        model.SetZoom(4); model.Select(new(.73, .38));
        Rect region = model.VisibleRegion;
        Check(region.Contains(new Point(.73, .38)) && region.Width < .3, "arbitrary noncentral region is inspectable");
        Rect before = model.ImageBounds(new(500, 400)); Point cursor = new(320, 220);
        Point anchor = new((cursor.X - before.X) / before.Width, (cursor.Y - before.Y) / before.Height);
        model.ZoomAt(cursor, new(500, 400), 1.25); Rect after = model.ImageBounds(new(500, 400));
        Check(Math.Abs((cursor.X - after.X) / after.Width - anchor.X) < 1e-10 && Math.Abs((cursor.Y - after.Y) / after.Height - anchor.Y) < 1e-10,
            "wheel zoom preserves the artwork point below the cursor");
        model.Pan(new(1e9, -1e9));
        Check(model.VisibleRegion.X >= 0 && model.VisibleRegion.Right <= 1 && model.VisibleRegion.Y >= 0 && model.VisibleRegion.Bottom <= 1,
            "pan cannot lose the artwork outside its bounds");
        model.SetNativePixelScale(2560, 1.5);
        Check(Math.Abs(model.ImageWidth * 1.5 - 2560) < 1e-8, "native detail respects Windows device-pixel scaling");
        model.Fit(); Check(model.Center == new Point(.5, .5) && model.Zoom == 1, "fit resets position and zoom");

        BitmapSource standard = Fixture(1280, 836), enhanced = Fixture(2560, 1672);
        foreach (bool polish in new[] { false, true })
        {
            int requests = 0, cancelled = 0;
            bool ignoreCancellation = false;
            TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialog = new ComparisonWindow(owner, standard, enhanced, polish, async (requested, token) =>
            {
                requests++; token.Register(() => cancelled++); requestStarted.TrySetResult();
                await Task.Delay(80, ignoreCancellation ? CancellationToken.None : token);
                return (Crop(standard, requested), Crop(enhanced, requested));
            }, enhanced.PixelWidth);
            try
            {
                dialog.Show(); await SettleAsync(); await Task.Delay(350); await SettleAsync();
                Check(dialog.StandardSurface.ActualWidth == dialog.EnhancedSurface.ActualWidth && dialog.StandardSurface.ActualHeight == dialog.EnhancedSurface.ActualHeight,
                    "localized labels preserve equal inspection pane sizes");
                Check(dialog.StandardDetailRegion == new Rect(0, 0, 1, 1) && dialog.EnhancedDetailRegion == dialog.StandardDetailRegion,
                    "initial exact crops share complete artwork coordinates");
                dialog.StandardSurface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog.StandardSurface), 0, Key.Add) { RoutedEvent = Keyboard.KeyDownEvent });
                await Task.Delay(350); await SettleAsync();
                Check(dialog.Viewport.Zoom > 1 && dialog.StandardDetailRegion == dialog.EnhancedDetailRegion && dialog.StandardDetailRegion == dialog.Viewport.VisibleRegion,
                    "keyboard zoom updates both exact-asset crops");
                var detail = Children<Button>((FrameworkElement)dialog.Content).Single(b => Equals(b.Content, polish ? "Szczegóły pikseli" : "Pixel detail"));
                ((IInvokeProvider)new ButtonAutomationPeer(detail).GetPattern(PatternInterface.Invoke)!).Invoke();
                await SettleAsync(); await Task.Delay(350);
                Check(Math.Abs(dialog.Viewport.ImageWidth * VisualTreeHelper.GetDpi(dialog.StandardSurface).PixelsPerDip - enhanced.PixelWidth) < 1e-6,
                    "pixel-detail action displays one Enhanced pixel per device pixel");
                Point previous = dialog.Viewport.Center;
                dialog.EnhancedSurface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog.EnhancedSurface), 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
                await Task.Delay(350); await SettleAsync();
                Check(dialog.Viewport.Center.X > previous.X && dialog.StandardDetailRegion == dialog.EnhancedDetailRegion,
                    "panning Enhanced moves both panes to the same position");
                dialog.Overview.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                dialog.Overview.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                await Task.Delay(350); await SettleAsync();
                Check(dialog.StandardDetailRegion == dialog.Viewport.VisibleRegion && dialog.EnhancedDetailRegion == dialog.Viewport.VisibleRegion,
                    "overview selection loads the chosen region in both panes");
                // Even a backend that finishes after cancellation cannot publish a stale crop.
                ignoreCancellation = true; requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Rect? retainedRegion = dialog.StandardDetailRegion; dialog.Viewport.Select(new(.5, .5));
                dialog.StandardSurface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog.StandardSurface), 0, Key.Left) { RoutedEvent = Keyboard.KeyDownEvent });
                await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                dialog.EnhancedSurface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog.EnhancedSurface), 0, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
                await Task.Delay(100); await SettleAsync();
                Check(dialog.StandardDetailRegion == retainedRegion && dialog.EnhancedDetailRegion == retainedRegion, "a canceled backend result cannot replace the retained crop");
                await Task.Delay(300); await SettleAsync();
                Check(requests >= 5 && cancelled >= 1 && dialog.StandardDetailRegion == dialog.Viewport.VisibleRegion && dialog.EnhancedDetailRegion == dialog.Viewport.VisibleRegion,
                    "superseded requests cancel and cannot replace newer crops");
                dialog.StandardSurface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog.StandardSurface), 0, Key.Home) { RoutedEvent = Keyboard.KeyDownEvent });
                await Task.Delay(350); await SettleAsync();
                Check(dialog.Viewport.Zoom == 1 && dialog.Viewport.Center == new Point(.5, .5), "Home restores the complete artwork");
                Capture((FrameworkElement)dialog.Content, Path.Combine(directory, "comparison-" + (polish ? "pl" : "en") + ".png"));
                dialog.Width = 760; dialog.Height = 580; await SettleAsync();
                Check(Children<Button>((FrameworkElement)dialog.Content).All(button => IsInside(button, (FrameworkElement)dialog.Content)), "all actions fit a compact localized window");
                Check(Children<TextBlock>((FrameworkElement)dialog.Content).All(label => IsInside(label, (FrameworkElement)dialog.Content)), "localized guidance and pane labels remain inside the compact window");
                Capture((FrameworkElement)dialog.Content, Path.Combine(directory, "comparison-compact-" + (polish ? "pl" : "en") + ".png"));
            }
            finally { dialog.Close(); }
        }
        File.WriteAllText(Path.Combine(directory, "comparison-checks.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed = true, Checks = count, Evidence = "Live WPF routed-input and automation checks with synthetic illustration fixtures; not an AI quality assessment." }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return count;
    }
    private static async Task SettleAsync() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static BitmapSource Fixture(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Ivory, null, new Rect(0, 0, width, height));
            dc.PushTransform(new ScaleTransform(width / 640d, height / 418d));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(35, 70, 61)), 3);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(220, 136, 83)), pen, new(210, 205), 135, 145);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(109, 153, 117)), pen, new(430, 206), 104, 142);
            dc.DrawLine(pen, new(110, 340), new(535, 90));
            dc.DrawLine(pen, new(100, 90), new(560, 340)); dc.Pop();
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    private static BitmapSource Crop(BitmapSource image, Rect region)
    {
        int x = Math.Clamp((int)Math.Floor(region.X * image.PixelWidth), 0, image.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Floor(region.Y * image.PixelHeight), 0, image.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(region.Right * image.PixelWidth), x + 1, image.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(region.Bottom * image.PixelHeight), y + 1, image.PixelHeight);
        var crop = new CroppedBitmap(image, new(x, y, right - x, bottom - y)); crop.Freeze(); return crop;
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
    private static bool IsInside(FrameworkElement item, FrameworkElement root)
    {
        Rect bounds = item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }
    private static void Capture(FrameworkElement root, string path)
    {
        var captureSize = new Size(root.ActualWidth + root.Margin.Left + root.Margin.Right, root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
        var image = new RenderTargetBitmap((int)Math.Ceiling(captureSize.Width), (int)Math.Ceiling(captureSize.Height), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Window.GetWindow(root)?.Background ?? Brushes.White, null, new Rect(captureSize));
            dc.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(captureSize), Stretch = Stretch.Fill }, null, new Rect(captureSize));
        }
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
