using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mosaic.App;

public partial class MainWindow
{
    // These exercise live WPF controls and automation peers in-process, not injected OS input.
    private async Task<int> CheckArrivalLayoutsAsync(string directory)
    {
        int count = 0;
        foreach (int language in new[] { 0, 1 })
        {
            LanguageBox.SelectedIndex = language;
            foreach (var (width, height, dpi) in UiViewports)
            {
                Width = width; Height = height;
                await UiSettledAsync();
                UiCheck(!EditorSidebar.IsVisible && !OpenImageButton.IsVisible && WelcomeOpenButton.IsVisible && !PrintGuidance.IsVisible,
                    "arrival has one open-image action and no premature settings", ref count);
                UiCheck(IsInside(WelcomeOpenButton, WelcomeScroll) && IsInside(PrintButton, (FrameworkElement)Content) && IsInside(ExportButton, (FrameworkElement)Content),
                    "arrival action and disabled outputs remain in the viewport", ref count);
                CaptureUi((FrameworkElement)Content, directory, $"arrival-{Lang.Language}-{width}-{dpi}.png", dpi);
            }
        }
        LanguageBox.SelectedIndex = 0; Width = 1380; Height = 920;
        await UiSettledAsync();
        return count;
    }

    private async Task<int> CheckRefinedEditorAsync(string directory)
    {
        int count = 0;
        Completion.Visibility = Visibility.Collapsed;
        preferences = preferences with { CalibrationPromptDismissed = false };
        foreach (int language in new[] { 0, 1 })
        {
            LanguageBox.SelectedIndex = language;
            foreach (var (width, height, dpi) in UiViewports)
            {
                Width = width; Height = height; EditorScroll.ScrollToTop();
                await UiSettledAsync();
                UiCheck(IsInside(WidthBox, EditorScroll) && IsInside(HeightBox, EditorScroll) && WidthLabel.ActualHeight < 25 && HeightLabel.ActualHeight < 25,
                    "localized dimension labels and inputs fit without wrapping or clipping", ref count);
                UiCheck(WidthBox.ActualHeight >= 44 && WidthBox.ActualHeight <= 46 && PrintButton.ActualWidth == ExportButton.ActualWidth,
                    "readable dimension targets and equally weighted output actions", ref count);
                UiCheck(IsInside(PrintButton, (FrameworkElement)Content) && IsInside(ExportButton, (FrameworkElement)Content) && IsInside(CalibrationButton, (FrameworkElement)Content),
                    "both output actions and calibration remain visible at compact sizes", ref count);
                if (width == 1380) UiCheck(IsInside(PaperOptions, EditorScroll), "paper disclosure fits default sidebar without scrolling", ref count);
                CaptureUi((FrameworkElement)Content, directory, $"ready-{Lang.Language}-{width}-{dpi}.png", dpi);
            }
            // Expanded settings must remain reachable by keyboard/scroll rather than being clipped.
            var paperPeer = new ExpanderAutomationPeer(PaperOptions);
            var disclosure = (IExpandCollapseProvider)paperPeer.GetPattern(PatternInterface.ExpandCollapse)!;
            disclosure.Expand();
            await UiSettledAsync();
            MarginBox.BringIntoView(); await UiSettledAsync();
            UiCheck(PaperOptions.IsExpanded && IsInside(MarginBox, EditorScroll), "paper disclosure expands and margin field scrolls into view", ref count);
            GrayscaleCheck.BringIntoView(); await UiSettledAsync();
            UiCheck(IsInside(GrayscaleCheck, EditorScroll) && GrayscaleCheck.Content is TextBlock { TextWrapping: TextWrapping.Wrap },
                "long print options wrap and remain reachable", ref count);
            CaptureUi((FrameworkElement)Content, directory, $"paper-options-{Lang.Language}.png", 96);
            disclosure.Collapse();
            EnhancementOptions.IsExpanded = true; CpuCheck.BringIntoView(); await UiSettledAsync();
            UiCheck(IsInside(CpuCheck, EditorScroll), "CPU fallback remains reachable", ref count);
            EnhancementOptions.IsExpanded = false; EditorScroll.ScrollToTop();

            var calibration = new CalibrationWindow(state.Paper, state.PrinterProfile, null, pdf, printer) { Owner = this, Width = 650, Height = 650 };
            calibration.Show(); await UiSettledAsync();
            var calibrationRoot = (FrameworkElement)calibration.Content;
            var printerField = UiChildren<ComboBox>(calibrationRoot).Single(c => AutomationProperties.GetName(c) == Lang.T("PrinterName"));
            var valuePeer = (IValueProvider)new ComboBoxAutomationPeer(printerField).GetPattern(PatternInterface.Value)!;
            valuePeer.SetValue("UI verification printer"); await UiSettledAsync();
            var editable = printerField.Template.FindName("PART_EditableTextBox", printerField) as TextBox;
            UiCheck(editable is { IsVisible: true } && editable.Text == "UI verification printer" && printerField.Text == editable.Text,
                "editable printer dropdown preserves WPF value entry", ref count);
            var save = UiChildren<Button>(calibrationRoot).Single(b => Equals(b.Content, Lang.T("SaveMeasurements")));
            UiCheck(IsInside(save, calibrationRoot), "calibration save remains visible outside the scrolling form", ref count);
            CaptureUi(calibrationRoot, directory, $"calibration-{Lang.Language}-125.png", 120);
            var scroll = UiChildren<ScrollViewer>(calibrationRoot).First(); scroll.ScrollToEnd(); await UiSettledAsync();
            UiCheck(UiChildren<CheckBox>(calibrationRoot).All(c => IsInside(c, scroll)), "calibration warning choices fit when scrolled into view", ref count);
            CaptureUi(calibrationRoot, directory, $"calibration-{Lang.Language}-150.png", 144);
            ((IInvokeProvider)new ButtonAutomationPeer(save).GetPattern(PatternInterface.Invoke)!).Invoke(); await UiSettledAsync();
            var validation = UiChildren<TextBlock>(calibrationRoot).Single(t => t.Text == Lang.T("CalibrationInvalid"));
            UiCheck(validation.TextWrapping == TextWrapping.Wrap && IsInside(validation, calibrationRoot) && IsInside(save, calibrationRoot) && calibration.Record == null,
                "calibration validation wraps without obscuring save actions or accepting incomplete measurements", ref count);
            CaptureUi(calibrationRoot, directory, $"calibration-validation-{Lang.Language}.png", 96);
            calibration.Close();
        }
        LanguageBox.SelectedIndex = 0; Width = 1380; Height = 920; EditorScroll.ScrollToTop(); await UiSettledAsync();
        var orientationPeer = new ComboBoxAutomationPeer(OrientationBox);
        var dropdown = (IExpandCollapseProvider)orientationPeer.GetPattern(PatternInterface.ExpandCollapse)!;
        dropdown.Expand(); await UiSettledAsync();
        var popup = OrientationBox.Template.FindName("PART_Popup", OrientationBox) as Popup;
        UiCheck(OrientationBox.IsDropDownOpen && popup is { IsOpen: true }, "styled dropdown opens through WPF automation", ref count);
        CaptureUi((FrameworkElement)popup!.Child, directory, "orientation-dropdown.png", 120);
        var landscapePeer = orientationPeer.GetChildren().Single(p => p.GetName() == Lang.T("Landscape"));
        ((ISelectionItemProvider)landscapePeer.GetPattern(PatternInterface.SelectionItem)!).Select();
        dropdown.Collapse(); await UiSettledAsync();
        UiCheck(state.Paper.Orientation == Core.OrientationMode.Landscape && layout!.Orientation == Core.SheetOrientation.Landscape,
            "dropdown automation selection reaches the existing layout handler", ref count);
        OrientationBox.SelectedIndex = 0;
        var gridToggle = (IToggleProvider)new CheckBoxAutomationPeer(GridCheck).GetPattern(PatternInterface.Toggle)!;
        gridToggle.Toggle(); UiCheck(!Preview.ShowGrid, "styled checkbox toggles the preview through its native peer", ref count);
        gridToggle.Toggle();

        Activate(); WidthBox.Focus(); WidthBox.SelectAll();
        var sizeBeforeFocus = state.Size;
        WidthBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); await UiSettledAsync();
        UiCheck(HeightBox.IsKeyboardFocused && state.Size == sizeBeforeFocus, "Tab traversal reaches height without changing exact size", ref count);
        CaptureUi((FrameworkElement)Content, directory, "keyboard-focus.png", 96);
        UiCheck(new TextBoxAutomationPeer(HeightBox).GetName() == Lang.T("HeightName") && AutomationProperties.GetHelpText(HeightBox) == SizeHelp.Text,
            "localized accessible dimension name and proportional guidance", ref count);

        ((IInvokeProvider)new ButtonAutomationPeer(CalibrationLaterButton).GetPattern(PatternInterface.Invoke)!).Invoke();
        await UiSettledAsync();
        UiCheck(preferences.CalibrationPromptDismissed && !CalibrationPromptText.IsVisible && CalibrationStatus.IsVisible && CalibrationButton.IsEnabled,
            "Later dismisses only first-run guidance and preserves calibration access", ref count);

        var previewSize = Preview.RenderSize;
        var cancellation = RunJobAsync("Exporting", async token => await Task.Delay(Timeout.Infinite, token));
        await UiSettledAsync();
        UiCheck(BusyBar.IsVisible && !PrintButton.IsEnabled && !ExportButton.IsEnabled && !OpenImageButton.IsEnabled,
            "busy state disables competing jobs", ref count);
        UiCheck(Preview.RenderSize == previewSize, "progress feedback keeps the artwork stationary", ref count);
        CaptureUi((FrameworkElement)Content, directory, "busy-en.png", 96);
        CancelClick(this, new RoutedEventArgs()); await cancellation;
        UiCheck(!BusyBar.IsVisible && PrintButton.IsEnabled && ExportButton.IsEnabled && state.Size == sizeBeforeFocus,
            "cancel restores editing and output with original geometry", ref count);

        // Use a real exported snapshot; no physical print submission is simulated.
        foreach (int language in new[] { 0, 1 })
        {
            LanguageBox.SelectedIndex = language; Width = 1050; Height = 700;
            RefreshCompletion(); await UiSettledAsync();
            UiCheck(!EditorRail.IsEnabled && !ApplicationCommands.Print.CanExecute(null, this) && OpenPdfButton.IsKeyboardFocused,
                "completion focuses its actions and blocks underlying keyboard print", ref count);
            CaptureUi((FrameworkElement)Content, directory, $"completion-compact-{Lang.Language}.png", 144);
            BackToEditingButton.BringIntoView(); await UiSettledAsync();
            UiCheck(IsInside(BackToEditingButton, (FrameworkElement)Content) && IsInside(CompletionTitle, (FrameworkElement)Content), "completion heading and dismissal stay visible in compact windows", ref count);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            RaiseEvent(key); await UiSettledAsync();
            UiCheck(Completion.Visibility == Visibility.Collapsed && PrintButton.IsEnabled && ExportButton.IsEnabled,
                "Escape returns from completion to the editor", ref count);
        }
        LanguageBox.SelectedIndex = 0; Width = 1380; Height = 920; await UiSettledAsync();
        File.WriteAllText(Path.Combine(directory, "ui-verification.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            Checks = count, HostDpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX,
            Viewports = UiViewports.Select(v => new { v.Width, v.Height, OutputDpi = v.Dpi }),
            WindowsDisplayScalingChanged = false, NativeInputInjected = false,
            Method = "Live WPF dispatcher operations, automation peers, focus traversal and RenderTargetBitmap"
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return count;
    }

    private static readonly (double Width, double Height, double Dpi)[] UiViewports = [(1380, 920, 96), (1150, 780, 120), (1050, 700, 144)];
    private async Task UiSettledAsync() { UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout(); }
    private static void UiCheck(bool condition, string description, ref int count)
    { if (!condition) throw new InvalidOperationException(description); count++; }
    private static bool IsInside(FrameworkElement element, FrameworkElement viewport)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
    }
    private static IEnumerable<T> UiChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in UiChildren<T>(child)) yield return descendant;
        }
    }
    private static void CaptureUi(FrameworkElement content, string directory, string filename, double dpi)
    {
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi / 96), (int)Math.Ceiling(content.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Window.GetWindow(content)?.Background ?? Brushes.White, null, new Rect(content.RenderSize));
        bitmap.Render(background);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, filename)); encoder.Save(file);
    }
}
