using System.Printing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Mosaic.Core;

namespace Mosaic.Output;

public sealed class PrintService
{
    private readonly SemaphoreSlim _submission = new(1, 1);

    public static IReadOnlyList<InstalledPrinter> GetPrinters()
    {
        using var server = new LocalPrintServer();
        string defaultName = "";
        try { using var defaultQueue = LocalPrintServer.GetDefaultPrintQueue(); defaultName = defaultQueue.FullName; } catch (PrintSystemException) { }
        using var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
        var result = new List<InstalledPrinter>();
        foreach (var queue in queues)
        {
            using (queue)
            {
                try { result.Add(new(queue.FullName, queue.QueueDriver.Name, queue.FullName == defaultName)); }
                catch (PrintSystemException) { }
            }
        }
        return result;
    }

    public async Task<PrintResult> PrintAsync(RenderSnapshot snapshot, Window owner, string language = "en",
        IReadOnlyList<CalibrationRecord>? calibrations = null, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _submission.WaitAsync(0, cancellationToken))
            throw new OutputException("busy", OutputText.Pick(language, "A print job is already being submitted.", "Trwa już wysyłanie zadania do drukarki."));
        try
        {
            owner.Dispatcher.VerifyAccess();
            var bitmap = await LoadImageAsync(snapshot.ImagePath, UsesGrayscale(snapshot.State.PrinterProfile, snapshot.State.Grayscale), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string? preferredPrinter = await ResolvePreferredPrinterAsync(snapshot.State.PrinterName, snapshot.State.PrinterProfile, language, cancellationToken);
            var dialog = CreateDialog(snapshot.Layout, preferredPrinter, snapshot.State.Copies,
                snapshot.State.PrinterProfile, snapshot.State.Grayscale);
            if (dialog.ShowDialog() != true) return new(false, "", 0);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(0, "printer"));
            var settings = await ValidateDialogAsync(dialog, snapshot.Layout, language, cancellationToken);
            var validCalibration = ResolveCalibration(calibrations ?? [], snapshot, settings.PrinterName, settings.Driver, settings.Fingerprint, settings.Capabilities, language);
            var paginator = CreatePaginator(snapshot, bitmap, validCalibration);
            return await SubmitAsync(dialog, paginator, snapshot.State.Title, snapshot.Layout.PageCount, language, settings, progress, cancellationToken);
        }
        catch (PrintSystemException ex)
        {
            throw new OutputException("printer", OutputText.Pick(language,
                "Windows could not contact the printer. Check its connection and driver; you can still export a PDF.",
                "System Windows nie może połączyć się z drukarką. Sprawdź połączenie i sterownik. Nadal możesz zapisać PDF."), null, ex);
        }
        finally { _submission.Release(); }
    }

    public async Task<PrintResult> PrintCalibrationAsync(PaperSpec paper, Window owner, string language = "en", string? printerName = null,
        IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default,
        string profile = "epson-et8500", bool grayscale = false)
    {
        if (!await _submission.WaitAsync(0, cancellationToken)) throw new OutputException("busy", OutputText.Pick(language,
            "A print job is already being submitted.", "Trwa już wysyłanie zadania do drukarki."));
        try
        {
            owner.Dispatcher.VerifyAccess();
            var layout = CalibrationFixture.Layout(paper);
            string? preferredPrinter = await ResolvePreferredPrinterAsync(printerName, profile, language, cancellationToken);
            var dialog = CreateDialog(layout, preferredPrinter, 1, profile, grayscale);
            if (dialog.ShowDialog() != true) return new(false, "", 0);
            progress?.Report(new(0, "printer"));
            var settings = await ValidateDialogAsync(dialog, layout, language, cancellationToken);
            return await SubmitAsync(dialog, new CalibrationPaginator(layout, language),
                OutputText.Pick(language, "Mosaic calibration", "Kalibracja Mosaic"), 4, language, settings, progress, cancellationToken);
        }
        catch (PrintSystemException ex)
        {
            throw new OutputException("printer", OutputText.Pick(language,
                "Windows could not contact the printer. Check its connection and driver; you can still export a PDF.",
                "System Windows nie może połączyć się z drukarką. Sprawdź połączenie i sterownik. Nadal możesz zapisać PDF."), null, ex);
        }
        finally { _submission.Release(); }
    }

    // Exposed for acceptance tests and preview/print parity checks. The bitmap must be normalized and rotated.
    public static DocumentPaginator CreatePaginator(RenderSnapshot snapshot, BitmapSource bitmap, CalibrationRecord? calibration = null)
    {
        if (!bitmap.IsFrozen) throw new ArgumentException("Print images must be frozen for safe background serialization.", nameof(bitmap));
        return new ArtworkPaginator(snapshot, bitmap, calibration);
    }

    public static DocumentPaginator CreateCalibrationPaginator(PaperSpec paper, string language = "en") =>
        new CalibrationPaginator(CalibrationFixture.Layout(paper), language);

    private static async Task<string?> ResolvePreferredPrinterAsync(string? savedPrinter, string profile, string language, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(savedPrinter)) return savedPrinter;
        try
        {
            return await Task.Run(() => ChoosePreferredPrinter(GetPrinters(), profile), token).WaitAsync(TimeSpan.FromSeconds(45), token);
        }
        catch (TimeoutException ex)
        {
            throw new OutputException("printerTimeout", OutputText.Pick(language,
                "Windows is taking too long to list printers. Check the connection, then try again. PDF export remains available.",
                "System Windows zbyt długo odczytuje listę drukarek. Sprawdź połączenie i spróbuj ponownie. Nadal możesz zapisać PDF."), null, ex);
        }
    }

    public static string? ChoosePreferredPrinter(IReadOnlyList<InstalledPrinter> printers, string profile)
    {
        string model = profile == "canon-mf210" ? "MF210" : "ET-8500";
        var matching = printers.Where(p => p.Name.Contains(model, StringComparison.OrdinalIgnoreCase)).ToList();
        return (matching.FirstOrDefault(p => profile != "canon-mf210" && p.Driver.Contains("EPSON", StringComparison.OrdinalIgnoreCase))
            ?? matching.FirstOrDefault() ?? printers.FirstOrDefault(p => p.IsDefault))?.Name;
    }

    private static Task<BitmapSource> LoadImageAsync(string path, bool grayscale, CancellationToken token) => Task.Run<BitmapSource>(() =>
    {
        token.ThrowIfCancellationRequested();
        var image = new BitmapImage();
        using var stream = File.OpenRead(path);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        token.ThrowIfCancellationRequested();
        if (!grayscale) return image;
        var gray = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);
        gray.Freeze();
        return gray;
    }, token);

    private static PrintDialog CreateDialog(LayoutPlan layout, string? printerName, int copies, string profile, bool grayscale)
    {
        var dialog = new PrintDialog { UserPageRangeEnabled = false, SelectedPagesEnabled = false, CurrentPageEnabled = false };
        if (!string.IsNullOrWhiteSpace(printerName))
        {
            try { dialog.PrintQueue = new PrintQueue(new LocalPrintServer(), printerName); }
            catch (PrintSystemException) { }
        }
        dialog.PrintTicket = CreateTicket(dialog.PrintTicket, layout, copies, profile, grayscale);
        return dialog;
    }

    internal static bool UsesGrayscale(string profile, bool grayscale) => grayscale || profile == "canon-mf210";

    internal static PrintTicket CreateTicket(PrintTicket? basis, LayoutPlan layout, int copies, string profile, bool grayscale)
    {
        var ticket = basis?.Clone() ?? new PrintTicket();
        ticket.PageOrientation = layout.Orientation == SheetOrientation.Portrait ? PageOrientation.Portrait : PageOrientation.Landscape;
        // PrintSchema Letter remains its unrotated dimensions; PageOrientation rotates it once.
        ticket.PageMediaSize = new PageMediaSize(PageMediaSizeName.NorthAmericaLetter);
        ticket.Duplexing = Duplexing.OneSided;
        ticket.CopyCount = Math.Clamp(copies, 1, 99);
        ticket.OutputColor = UsesGrayscale(profile, grayscale) ? OutputColor.Monochrome : OutputColor.Color;
        ticket.PageScalingFactor = 100;
        ticket.PageBorderless = layout.Paper.MarginInches == 0 ? PageBorderless.Borderless : PageBorderless.None;
        return ticket;
    }

    public static decimal RequiredMargin(PrintCapabilities capabilities, LayoutPlan layout)
    {
        var area = capabilities.PageImageableArea;
        if (area is null) return .25m;
        double pageWidth = (double)layout.PageWidth * 96, pageHeight = (double)layout.PageHeight * 96;
        double margin = Math.Max(Math.Max(area.OriginWidth, area.OriginHeight),
            Math.Max(pageWidth - area.OriginWidth - area.ExtentWidth, pageHeight - area.OriginHeight - area.ExtentHeight));
        return decimal.Ceiling((decimal)Math.Max(0, margin / 96) * 1000) / 1000;
    }

    private sealed record ValidatedSettings(string PrinterName, string Driver, string Fingerprint, PrintTicket Ticket, PrintCapabilities Capabilities);

    private static async Task<ValidatedSettings> ValidateDialogAsync(PrintDialog dialog, LayoutPlan layout, string language, CancellationToken token)
    {
        if (dialog.PrintQueue is null || dialog.PrintTicket is null) throw new OutputException("printer", OutputText.Pick(language,
            "Choose an installed printer.", "Wybierz zainstalowaną drukarkę."));
        string queueName = dialog.PrintQueue.FullName;
        var request = dialog.PrintTicket.Clone();
        try
        {
            var result = await Task.Run(() =>
            {
                using var server = new LocalPrintServer();
                using var queue = new PrintQueue(server, queueName);
                return ValidateTicket(queue, request, layout, language);
            }, token).WaitAsync(TimeSpan.FromSeconds(45), token);
            dialog.PrintTicket = result.Ticket;
            return result;
        }
        catch (TimeoutException ex)
        {
            throw new OutputException("printerTimeout", OutputText.Pick(language,
                "The printer driver is taking too long to respond. Check the connection, then try again. PDF export remains available.",
                "Sterownik drukarki nie odpowiada. Sprawdź połączenie i spróbuj ponownie. Nadal możesz zapisać PDF."), null, ex);
        }
    }

    private static ValidatedSettings ValidateTicket(PrintQueue queue, PrintTicket request, LayoutPlan layout, string language)
    {
        var ticket = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, request).ValidatedPrintTicket;
        var expected = layout.Orientation == SheetOrientation.Portrait ? PageOrientation.Portrait : PageOrientation.Landscape;
        if (ticket.PageOrientation != expected || ticket.PageScalingFactor is not (null or 100) || HasFitScaling(ticket) || ticket.Duplexing is not (null or Duplexing.OneSided))
            throw new OutputException("settings", OutputText.Pick(language,
                "The driver changed orientation, scale or two-sided printing. Choose the shown orientation, 100% scale and one-sided printing, then try again.",
                "Sterownik zmienił orientację, skalę lub druk dwustronny. Ustaw pokazaną orientację, skalę 100% i druk jednostronny, a następnie spróbuj ponownie."));
        if (ticket.PageMediaSize?.PageMediaSizeName is not (null or PageMediaSizeName.NorthAmericaLetter))
            throw new OutputException("paper", OutputText.Pick(language,
                "Choose US Letter paper (8.5 × 11 in) in printer properties.", "W ustawieniach drukarki wybierz papier US Letter (8,5 × 11 cali)."));
        var caps = queue.GetPrintCapabilities(ticket);
        decimal required = RequiredMargin(caps, layout);
        if (required > layout.Paper.MarginInches + .005m)
            throw new OutputException("margins", OutputText.Pick(language,
                "This driver cannot print the current page edges. Choose precision trim mode with the suggested margin, or enable Letter borderless in printer properties. The artwork will keep its exact size.",
                "Ten sterownik nie drukuje do wybranych krawędzi. Włącz tryb przycinania z proponowanym marginesem lub druk bez marginesów na papierze Letter. Wymiary wzoru pozostaną bez zmian."), Math.Max(.25m, required));
        if (layout.Paper.MarginInches == 0 && ticket.PageBorderless != PageBorderless.Borderless)
            throw new OutputException("borderless", OutputText.Pick(language,
                "Enable borderless Letter printing in printer properties, or choose precision trim mode. Check expansion with a calibration test.",
                "W ustawieniach drukarki włącz druk Letter bez marginesów albo wybierz tryb przycinania. Sprawdź powiększenie wydrukiem kalibracyjnym."), .25m);
        return new(queue.FullName, queue.QueueDriver.Name, ValidatedSettingsFingerprint(queue, ticket, layout), ticket, caps);
    }

    internal static string ValidatedSettingsFingerprint(PrintQueue queue, PrintTicket ticket, LayoutPlan layout)
    {
        // IPP drivers can also encode copies inside opaque vendor DEVMODE data. Ask the driver
        // to normalize quantity, retaining all media/quality/expansion settings in the hash.
        var oneCopy = ticket.Clone();
        oneCopy.CopyCount = 1;
        var normalized = queue.MergeAndValidatePrintTicket(ticket, oneCopy).ValidatedPrintTicket;
        return SettingsFingerprint(normalized, layout);
    }

    private static bool HasFitScaling(PrintTicket ticket)
    {
        using var stream = ticket.GetXmlStream();
        var xml = XDocument.Load(stream);
        return xml.Descendants().Any(feature =>
            feature.Name.LocalName == "Feature" && feature.Attribute("name")?.Value.Split(':').Last() == "PageScaling" &&
            feature.Elements().Any(option => option.Name.LocalName == "Option" &&
                option.Attribute("name")?.Value.Split(':').Last().StartsWith("Fit", StringComparison.OrdinalIgnoreCase) == true));
    }

    public static string SettingsFingerprint(PrintTicket ticket, LayoutPlan layout)
    {
        // Keep vendor-specific media/expansion features as well as standard PrintSchema settings.
        // Copies change quantity only and must not invalidate a physical calibration.
        using var stream = ticket.GetXmlStream();
        var xml = XDocument.Load(stream);
        foreach (var node in xml.Descendants().Where(n =>
            n.Attribute("name")?.Value.Contains("Copies", StringComparison.OrdinalIgnoreCase) == true).ToList()) node.Remove();
        string value = xml.ToString(SaveOptions.DisableFormatting) + "|margin=" +
            layout.Paper.MarginInches.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    internal static CalibrationRecord? ResolveCalibration(IReadOnlyList<CalibrationRecord> records, RenderSnapshot snapshot,
        string printerName, string driver, string fingerprint, PrintCapabilities capabilities, string language = "en")
    {
        // Saved project printer names are preferences only. Match the queue the user actually selected.
        var candidates = records.Where(record => record.Enabled && record.Pathway == "direct" &&
            record.PrinterName == printerName && record.Profile == snapshot.State.PrinterProfile &&
            record.Orientation == snapshot.Layout.Orientation).ToList();
        if (candidates.Count == 0) return null;
        var record = candidates.Where(record => record.Driver == driver && record.DriverFingerprint == fingerprint &&
            !string.IsNullOrWhiteSpace(record.DriverFingerprint)).OrderByDescending(record => record.MeasuredAt).FirstOrDefault();
        if (record is null) throw new OutputException("calibrationMismatch", OutputText.Pick(language,
            "This calibration belongs to another printer, driver, orientation or print method. Disable it or calibrate the selected settings.",
            "Ta kalibracja dotyczy innej drukarki, sterownika, orientacji lub metody druku. Wyłącz ją albo skalibruj wybrane ustawienia."));
        if (record.EdgeClipping || record.MeasuredXInches is < 3.6m or > 4.4m || record.MeasuredYInches is < 3.6m or > 4.4m)
            throw new OutputException("calibrationClipping", OutputText.Pick(language,
                "Clipped edges or unusually large corrections cannot guarantee joins. Use precision trim mode and repeat calibration.",
                "Przy obciętych krawędziach lub dużej korekcie nie można zapewnić łączenia. Wybierz tryb przycinania i powtórz kalibrację."), .25m);
        var area = capabilities.PageImageableArea;
        decimal minX = area is null ? 0 : (decimal)area.OriginWidth / 96;
        decimal minY = area is null ? 0 : (decimal)area.OriginHeight / 96;
        decimal maxX = area is null ? snapshot.Layout.PageWidth : (decimal)(area.OriginWidth + area.ExtentWidth) / 96;
        decimal maxY = area is null ? snapshot.Layout.PageHeight : (decimal)(area.OriginHeight + area.ExtentHeight) / 96;
        foreach (var tile in snapshot.Layout.Pages)
        {
            var r = tile.PaperBounds;
            if (r.X * record.ScaleX + record.OffsetXInches < minX - .005m || r.Y * record.ScaleY + record.OffsetYInches < minY - .005m ||
                r.Right * record.ScaleX + record.OffsetXInches > maxX + .005m || r.Bottom * record.ScaleY + record.OffsetYInches > maxY + .005m)
                throw new OutputException("calibrationBounds", OutputText.Pick(language,
                    "This correction would move artwork outside the printable area. Increase the trim margin and calibrate again.",
                    "Ta korekta przesunęłaby wzór poza obszar wydruku. Zwiększ margines przycinania i powtórz kalibrację."), .35m);
        }
        return record;
    }

    private static async Task<PrintResult> SubmitAsync(PrintDialog dialog, DocumentPaginator paginator, string title, int count,
        string language, ValidatedSettings settings, IProgress<JobProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        dialog.PrintQueue.CurrentJobSettings.Description = title;
        var writer = PrintQueue.CreateXpsDocumentWriter(dialog.PrintQueue);
        var completion = new TaskCompletionSource<PrintResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        writer.WritingProgressChanged += (_, e) => progress?.Report(new(Math.Min(.99, (double)e.Number / Math.Max(1, count)), "print"));
        writer.WritingCompleted += (_, e) =>
        {
            if (e.Cancelled) completion.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
            else if (e.Error is not null) completion.TrySetException(new OutputException("spool", OutputText.Pick(language,
                "Windows did not accept the print job. Check the printer queue and try again.",
                "System Windows nie przyjął zadania drukowania. Sprawdź kolejkę drukarki i spróbuj ponownie."), null, e.Error));
            else { progress?.Report(new(1, "print")); completion.TrySetResult(new(true, settings.PrinterName, count) { Driver = settings.Driver, DriverFingerprint = settings.Fingerprint }); }
        };
        using var registration = token.Register(() => dispatcher.BeginInvoke(() =>
        {
            if (completion.Task.IsCompleted) return;
            try { writer.CancelAsync(); }
            catch (InvalidOperationException) { }
            catch (XpsWriterException) { } // A queued cancellation can race completed XPS serialization.
        }));
        token.ThrowIfCancellationRequested();
        writer.WriteAsync(paginator, dialog.PrintTicket);
        return await completion.Task;
    }
}

internal sealed class ArtworkPaginator(RenderSnapshot snapshot, BitmapSource bitmap, CalibrationRecord? calibration) : DocumentPaginator
{
    public override bool IsPageCountValid => true;
    public override int PageCount => snapshot.Layout.PageCount;
    public override Size PageSize { get => new((double)snapshot.Layout.PageWidth * 96, (double)snapshot.Layout.PageHeight * 96); set { } }
    public override IDocumentPaginatorSource? Source => null;
    public override DocumentPage GetPage(int pageNumber)
    {
        if (pageNumber < 0 || pageNumber >= PageCount) return DocumentPage.Missing;
        var tile = snapshot.Layout.Pages[pageNumber];
        var drawing = OutputGeometry.Describe(snapshot, tile, bitmap.PixelWidth, bitmap.PixelHeight);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(PageSize));
            if (calibration is not null)
            {
                context.PushTransform(new MatrixTransform((double)calibration.ScaleX, 0, 0, (double)calibration.ScaleY,
                    (double)calibration.OffsetXInches * 96, (double)calibration.OffsetYInches * 96));
            }
            context.PushClip(new RectangleGeometry(ToDips(drawing.Clip)));
            context.DrawImage(bitmap, ToDips(drawing.Image));
            context.Pop();
            if (snapshot.Layout.Paper.MarginInches > 0) PrintDrawing.TrimMarks(context, tile.PaperBounds, snapshot.Layout.Paper.MarginInches);
            if (calibration is not null) context.Pop();
        }
        return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
    }
    internal static Rect ToDips(RectInches rect) => new((double)rect.X * 96, (double)rect.Y * 96, (double)rect.Width * 96, (double)rect.Height * 96);
}

internal static class PrintDrawing
{
    public static void TrimMarks(DrawingContext context, RectInches bounds, decimal margin)
    {
        var r = ArtworkPaginator.ToDips(bounds);
        var pen = new Pen(Brushes.Gray, .45); pen.Freeze();
        double length = Math.Min(9, (double)margin * 96 * .65);
        foreach (double x in new[] { r.Left, r.Right })
            foreach (double y in new[] { r.Top, r.Bottom })
            {
                double dx = x == r.Left ? -1 : 1, dy = y == r.Top ? -1 : 1;
                context.DrawLine(pen, new(x + dx * 2, y), new(x + dx * length, y));
                context.DrawLine(pen, new(x, y + dy * 2), new(x, y + dy * length));
            }
    }
}
