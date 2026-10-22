using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Mosaic.Core;
using Mosaic.Output;

namespace Mosaic.App;

internal sealed class CalibrationWindow : Window
{
    private readonly PaperSpec paper;
    private readonly string profile;
    private readonly bool grayscale;
    private readonly PdfExporter pdf;
    private readonly PrintService printer;
    private readonly ComboBox printerBox = new(), pathwayBox = new();
    private readonly TextBox measuredX = new(), measuredY = new(), driverSettings = new();
    private readonly CheckBox clipped = new(), enabled = new();
    private readonly TextBlock error = new();
    private readonly StackPanel body = new();
    private readonly Button exportButton = new(), printButton = new(), saveButton = new();
    private CancellationTokenSource? job;
    private string capturedDriver = "", capturedFingerprint = "", capturedPrinter = "";
    public CalibrationRecord? Record { get; private set; }

    public CalibrationWindow(PaperSpec paper, string profile, CalibrationRecord? prior, PdfExporter pdf, PrintService printer, bool grayscale = false)
    {
        this.paper = paper; this.profile = profile; this.pdf = pdf; this.printer = printer; this.grayscale = grayscale;
        Style = (Style)Application.Current.Resources[typeof(Window)];
        Title = Lang.T("CalibrationTitle"); Width = 650; Height = 820; MinWidth = 500; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(248, 250, 246));
        var layout = new Grid();
        layout.RowDefinitions.Add(new()); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.Margin = new Thickness(28, 24, 28, 18);
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = layout;
        body.Children.Add(new TextBlock { Text = Lang.T("CalibrationTitle"), FontFamily = new FontFamily("Georgia"), FontSize = 30 });
        body.Children.Add(new TextBlock { Text = Lang.T("CalibrationBody"), Margin = new Thickness(0, 12, 0, 16), LineHeight = 24 });
        var actions = new WrapPanel();
        exportButton.Content = Lang.T("CalibrationPdf"); exportButton.Margin = new Thickness(0, 0, 10, 8); exportButton.Click += ExportClick;
        printButton.Content = Lang.T("CalibrationPrint"); printButton.Margin = new Thickness(0, 0, 0, 8); printButton.Click += PrintClick;
        actions.Children.Add(exportButton); actions.Children.Add(printButton); body.Children.Add(actions);
        AddField("PrinterName", printerBox);
        printerBox.IsEditable = true;
        printerBox.Text = prior?.PrinterName ?? "";
        capturedPrinter = prior?.PrinterName ?? ""; capturedDriver = prior?.Driver ?? ""; capturedFingerprint = prior?.DriverFingerprint ?? "";
        Loaded += async (_, _) =>
        {
            App.FitOnScreen(this);
            try { var printers = await Task.Run(PrintService.GetPrinters); foreach (var p in printers) printerBox.Items.Add(p.Name); if (string.IsNullOrEmpty(printerBox.Text)) printerBox.Text = PrintService.ChoosePreferredPrinter(printers, profile) ?? ""; }
            catch { error.Text = Lang.T("PrintError"); }
        };
        AddField("Pathway", pathwayBox);
        pathwayBox.Items.Add(Lang.T("Direct")); pathwayBox.Items.Add(Lang.T("PdfViewer")); pathwayBox.SelectedIndex = prior?.Pathway == "pdf" ? 1 : 0;
        var measures = new Grid(); measures.ColumnDefinitions.Add(new()); measures.ColumnDefinitions.Add(new() { Width = new GridLength(14) }); measures.ColumnDefinitions.Add(new());
        var x = new StackPanel(); var y = new StackPanel(); Grid.SetColumn(y, 2); measures.Children.Add(x); measures.Children.Add(y);
        AddField("MeasuredX", measuredX, x); AddField("MeasuredY", measuredY, y); body.Children.Add(measures);
        measuredX.Text = (prior?.MeasuredXInches ?? 4).ToString("0.####", Lang.Culture); measuredY.Text = (prior?.MeasuredYInches ?? 4).ToString("0.####", Lang.Culture);
        AddField("DriverSettings", driverSettings); driverSettings.Text = prior?.DriverSettings ?? ""; driverSettings.TextWrapping = TextWrapping.Wrap; driverSettings.MinHeight = 62;
        clipped.Content = new TextBlock { Text = Lang.T("EdgeClipping"), TextWrapping = TextWrapping.Wrap }; clipped.IsChecked = prior?.EdgeClipping ?? false; body.Children.Add(clipped);
        enabled.Content = new TextBlock { Text = Lang.T("ApplyCorrection"), TextWrapping = TextWrapping.Wrap }; enabled.IsChecked = prior?.Enabled ?? false; body.Children.Add(enabled);
        System.Windows.Automation.AutomationProperties.SetName(clipped, Lang.T("EdgeClipping"));
        System.Windows.Automation.AutomationProperties.SetName(enabled, Lang.T("ApplyCorrection"));
        body.Children.Add(new TextBlock { Text = Lang.T("CalibrationWarning"), FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(117, 83, 40)), Margin = new Thickness(0, 12, 0, 0), LineHeight = 23 });
        var footerBody = new StackPanel();
        error.Foreground = Brushes.Firebrick; error.FontSize = 15; error.LineHeight = 23; error.Margin = new Thickness(0, 0, 0, 10); footerBody.Children.Add(error);
        var errorStyle = new Style(typeof(TextBlock), (Style)Application.Current.Resources[typeof(TextBlock)]);
        var noMessage = new Trigger { Property = TextBlock.TextProperty, Value = "" };
        noMessage.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); errorStyle.Triggers.Add(noMessage); error.Style = errorStyle;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Polite);
        var footer = new WrapPanel();
        saveButton.Content = Lang.T("SaveMeasurements"); saveButton.Style = (Style)Application.Current.Resources["Primary"]; saveButton.Click += SaveClick; saveButton.Margin = new Thickness(0, 0, 10, 6);
        var cancel = new Button { Content = Lang.T("Close"), Margin = new Thickness(0, 0, 0, 6) }; cancel.Click += (_, _) => CancelOrClose();
        footer.Children.Add(saveButton); footer.Children.Add(cancel); footerBody.Children.Add(footer);
        var footerShell = new Border { Background = Brushes.White, BorderBrush = (Brush)Application.Current.Resources["Line"], BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(28, 14, 28, 10), Child = footerBody };
        Grid.SetRow(footerShell, 1); layout.Children.Add(footerShell);
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !printerBox.IsDropDownOpen && !pathwayBox.IsDropDownOpen) { CancelOrClose(); e.Handled = true; } };
        Closing += (_, e) => { if (job != null) { job.Cancel(); e.Cancel = true; } };
    }
    private void AddField(string key, Control control, StackPanel? parent = null)
    {
        var container = parent ?? body;
        var label = new TextBlock { Text = Lang.T(key), FontSize = 15, LineHeight = 22, Margin = new Thickness(0, 12, 0, 6) };
        container.Children.Add(label);
        System.Windows.Automation.AutomationProperties.SetLabeledBy(control, label);
        System.Windows.Automation.AutomationProperties.SetName(control, Lang.T(key)); container.Children.Add(control);
    }
    private void CancelOrClose() { if (job != null) job.Cancel(); else Close(); }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = Lang.T("PdfFilter"), FileName = "Mosaic-calibration.pdf" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async token => { await pdf.ExportCalibrationAsync(dialog.FileName, paper, Lang.Language, cancellationToken: token); error.Text = Lang.T("ExportedTitle") + " " + System.IO.Path.GetFileName(dialog.FileName); });
    }
    private async void PrintClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var result = await printer.PrintCalibrationAsync(paper, this, Lang.Language, printerBox.Text, cancellationToken: token, profile: profile, grayscale: grayscale);
        if (result.Submitted) { capturedPrinter = result.PrinterName; capturedDriver = result.Driver; capturedFingerprint = result.DriverFingerprint; printerBox.Text = result.PrinterName; error.Text = Lang.T("SentTitle"); }
    });
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (job != null) return;
        job = new(); exportButton.IsEnabled = printButton.IsEnabled = saveButton.IsEnabled = false;
        try { await action(job.Token); }
        catch (OperationCanceledException) { error.Text = Lang.T("Cancelled"); }
        catch (OutputException ex) { error.Text = ex.Message; }
        catch (Exception ex) { LocalLog.Write("calibration_failure", ex.GetType().Name); error.Text = Lang.T("Unexpected"); }
        finally { job.Dispose(); job = null; exportButton.IsEnabled = printButton.IsEnabled = saveButton.IsEnabled = true; }
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(measuredX.Text, NumberStyles.AllowDecimalPoint, Lang.Culture, out var x) ||
            !decimal.TryParse(measuredY.Text, NumberStyles.AllowDecimalPoint, Lang.Culture, out var y) || x < 3 || x > 5 || y < 3 || y > 5 ||
            string.IsNullOrWhiteSpace(printerBox.Text) || string.IsNullOrWhiteSpace(driverSettings.Text))
        { error.Text = Lang.T("CalibrationInvalid"); return; }
        var orientation = paper.Orientation == OrientationMode.Landscape ? SheetOrientation.Landscape : SheetOrientation.Portrait;
        if (enabled.IsChecked == true && pathwayBox.SelectedIndex == 0 && (string.IsNullOrEmpty(capturedFingerprint) || capturedPrinter != printerBox.Text)) { error.Text = Lang.T("PrintCalibrationFirst"); return; }
        Record = new CalibrationRecord { PrinterName = printerBox.Text, Driver = capturedPrinter == printerBox.Text ? capturedDriver : "", DriverFingerprint = capturedPrinter == printerBox.Text ? capturedFingerprint : "", Profile = profile, Pathway = pathwayBox.SelectedIndex == 1 ? "pdf" : "direct", Orientation = orientation, Media = "Letter", DriverSettings = driverSettings.Text, MeasuredXInches = x, MeasuredYInches = y, EdgeClipping = clipped.IsChecked == true, Enabled = enabled.IsChecked == true && clipped.IsChecked != true && pathwayBox.SelectedIndex == 0 };
        DialogResult = true;
    }
}
