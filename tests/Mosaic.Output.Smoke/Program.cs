using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using System.IO.Compression;
using System.Xml.Linq;
using Mosaic.Core;
using Mosaic.Output;
using Reader = UglyToad.PdfPig.PdfDocument;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        string folder = Path.GetFullPath(args.FirstOrDefault(a=>!a.StartsWith("--")) ?? "artifacts/output-smoke");
        Directory.CreateDirectory(folder);
        try
        {
            string image = Path.Combine(folder, "fixture.png");
            var bitmap = CreateFixture(image);
            var source = new SourceInfo(image, image, "synthetic-fixture", bitmap.PixelWidth, bitmap.PixelHeight, "PNG", new FileInfo(image).Length, image);
            var exporter = new PdfExporter();
            foreach (var (name, width, height, orientation, margin) in new[] {
                ("24-inch",24m,18m,OrientationMode.Auto,0m),
                ("60-inch",60m,45m,OrientationMode.Auto,0m),
                ("fractional-trim",23.375m,17.125m,OrientationMode.Portrait,.25m),
                ("landscape",20m,8.5m,OrientationMode.Auto,0m),
                ("manual",24m,18m,OrientationMode.Landscape,0m) })
            {
                var state = new ProjectState { Title = name, Size = new(width,height), Paper = new(MarginInches:margin, Orientation:orientation) };
                var layout = LayoutEngine.Calculate(state.Size,state.Paper);
                var snapshot = new RenderSnapshot(source,image,state,layout);
                var result = exporter.ExportAsync(snapshot,Path.Combine(folder,name+".pdf")).GetAwaiter().GetResult();
                using var pdf = Reader.Open(result.Path);
                Check(pdf.NumberOfPages == layout.PageCount, name+" page count");
                foreach (var page in pdf.GetPages())
                {
                    Near((double)page.Width, (double)layout.PageWidth*72,name+" MediaBox width");
                    Near((double)page.Height,(double)layout.PageHeight*72,name+" MediaBox height");
                }
                var images = pdf.GetPage(1).GetImages().ToList();
                Check(images.Count == 1,name+" reusable image");
                double fitScale=Math.Min((double)state.Size.WidthInches/bitmap.PixelWidth,(double)state.Size.HeightInches/bitmap.PixelHeight);
                Near(images[0].BoundingBox.Width,bitmap.PixelWidth*fitScale*72,name+" exact image width");
                Near(images[0].BoundingBox.Height,bitmap.PixelHeight*fitScale*72,name+" exact image height");
                if (layout.Columns > 1)
                {
                    var second = pdf.GetPage(2).GetImages().Single();
                    Near(second.BoundingBox.Left-images[0].BoundingBox.Left,-(double)layout.TileWidth*72,name+" adjacent seam translation");
                }
                var raw=Encoding.Latin1.GetString(File.ReadAllBytes(result.Path));
                Check(System.Text.RegularExpressions.Regex.Matches(raw,@"/Subtype\s*/Image").Count == 1,name+" only one image XObject across pages");
                Check(result.Bytes < bitmap.PixelWidth*bitmap.PixelHeight*3L+100_000,name+" bounded PDF resource reuse");
                var paginator=PrintService.CreatePaginator(snapshot,bitmap);
                Check(paginator.PageCount==layout.PageCount,name+" print page parity");
                Near(paginator.PageSize.Width,(double)layout.PageWidth*96,name+" print physical width");
                Near(paginator.PageSize.Height,(double)layout.PageHeight*96,name+" print physical height");
                using(var page=paginator.GetPage(0))
                {
                    var render=new RenderTargetBitmap((int)Math.Ceiling(page.Size.Width),(int)Math.Ceiling(page.Size.Height),96,96,PixelFormats.Pbgra32);
                    render.Render(page.Visual);
                    SavePng(render,Path.Combine(folder,name+"-print-page1.png"));
                    Check(render.PixelWidth>0,name+" print page rasterized");
                }
            }
            var guideState=new ProjectState { AssemblyGuide=true, Title="Próba — łączenie" };
            var guideSnapshot=new RenderSnapshot(source,image,guideState,LayoutEngine.Calculate(guideState.Size,guideState.Paper));
            var trimState=guideState with { Paper=new(MarginInches:.25m), AssemblyGuide=false };
            var trimSnapshot=new RenderSnapshot(source,image,trimState,LayoutEngine.Calculate(trimState.Size,trimState.Paper));
            var correction=new CalibrationRecord {MeasuredXInches=4.4m,MeasuredYInches=4m,OffsetXInches=.5m,Enabled=true};
            using(var corrected=PrintService.CreatePaginator(trimSnapshot,bitmap,correction).GetPage(0))
            {
                var raster=new RenderTargetBitmap((int)Math.Ceiling(corrected.Size.Width),(int)Math.Ceiling(corrected.Size.Height),96,96,PixelFormats.Pbgra32);
                raster.Render(corrected.Visual);
                // A vertical trim tick above the artwork must follow the same correction as its clip edge.
                int markX=(int)Math.Round((.25*(double)correction.ScaleX+.5)*96);
                byte[] region=new byte[5*5*4]; raster.CopyPixels(new Int32Rect(markX-2,16,5,5),region,5*4,0);
                Check(Enumerable.Range(0,25).Any(i=>region[i*4]<220&&Math.Abs(region[i*4]-region[i*4+1])<3),"calibrated trim marks align with corrected artwork");
                SavePng(raster,Path.Combine(folder,"calibrated-trim-page1.png"));
            }
            string xpsPath=Path.Combine(folder,"direct-path.xps");
            if(File.Exists(xpsPath)) File.Delete(xpsPath);
            using(var xps=new XpsDocument(xpsPath,FileAccess.ReadWrite))
                XpsDocument.CreateXpsDocumentWriter(xps).Write(PrintService.CreatePaginator(guideSnapshot,bitmap));
            using(var zip=ZipFile.OpenRead(xpsPath))
            {
                var pages=zip.Entries.Where(e=>e.FullName.EndsWith(".fpage",StringComparison.OrdinalIgnoreCase)).ToList();
                Check(pages.Count==guideSnapshot.Layout.PageCount,"XPS serializes every direct-print page");
                foreach(var entry in pages)
                {
                    using var data=entry.Open(); var xml=XDocument.Load(data);
                    Near(double.Parse(xml.Root!.Attribute("Width")!.Value,System.Globalization.CultureInfo.InvariantCulture),(double)guideSnapshot.Layout.PageWidth*96,"XPS page width");
                    Near(double.Parse(xml.Root!.Attribute("Height")!.Value,System.Globalization.CultureInfo.InvariantCulture),(double)guideSnapshot.Layout.PageHeight*96,"XPS page height");
                }
            }
            var guide=exporter.ExportAsync(guideSnapshot,Path.Combine(folder,"assembly-pl.pdf"),"pl").GetAwaiter().GetResult();
            using(var pdf=Reader.Open(guide.Path))
            {
                Check(pdf.NumberOfPages==guideSnapshot.Layout.PageCount+1,"separate assembly page");
                Check(pdf.GetPage(pdf.NumberOfPages).Text.Contains("łączenia"),"Polish guide text embedded");
            }
            var grayState=guideState with { Grayscale=true,AssemblyGuide=false };
            exporter.ExportAsync(new(source,image,grayState,LayoutEngine.Calculate(grayState.Size,grayState.Paper)),Path.Combine(folder,"grayscale.pdf")).GetAwaiter().GetResult();
            Check(Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(folder,"grayscale.pdf"))).Contains("/ColorSpace/DeviceGray"),"explicit grayscale image color space");
            var defaultTicket=new System.Printing.PrintTicket { PageOrientation=System.Printing.PageOrientation.Portrait,CopyCount=1 };
            var ticketCopy=defaultTicket.Clone(); ticketCopy.CopyCount=2;
            Check(PrintService.SettingsFingerprint(defaultTicket,guideSnapshot.Layout)==PrintService.SettingsFingerprint(ticketCopy,guideSnapshot.Layout),"copies retain calibration fingerprint");
            ticketCopy.PageOrientation=System.Printing.PageOrientation.Landscape;
            Check(PrintService.SettingsFingerprint(defaultTicket,guideSnapshot.Layout)!=PrintService.SettingsFingerprint(ticketCopy,guideSnapshot.Layout),"orientation invalidates calibration fingerprint");
            InstalledPrinter[] preferredFixtures = {new("Windows PDF","Virtual",true),new("EPSON ET-8500 Copy","Microsoft IPP",false),new("EPSON ET-8500 Series","EPSON ET-8500 Series",false),new("Canon MF210 Series","Microsoft IPP",false)};
            Check(PrintService.ChoosePreferredPrinter(preferredFixtures,"canon-mf210")=="Canon MF210 Series","Canon profile selects Canon queue");
            Check(PrintService.ChoosePreferredPrinter(preferredFixtures,"epson-et8500")=="EPSON ET-8500 Series","Epson profile prefers OEM queue");
            foreach(var mode in new[]{OrientationMode.Portrait,OrientationMode.Landscape})
            {
                var cal=exporter.ExportCalibrationAsync(Path.Combine(folder,"calibration-"+mode+"-pl.pdf"),new(Orientation:mode),"pl").GetAwaiter().GetResult();
                using var pdf=Reader.Open(cal.Path);
                Check(pdf.NumberOfPages==4,"four calibration pages");
                Check(pdf.GetPage(1).Text.Contains("X = 4 cale"),"Polish X ruler label");
                Near((double)pdf.GetPage(1).Width,mode==OrientationMode.Portrait?612:792,"calibration orientation");
            }
            string atomic=Path.Combine(folder,"cancel-retains.pdf"); File.WriteAllText(atomic,"previous user output");
            using(var cts=new CancellationTokenSource())
            {
                cts.Cancel();
                try { exporter.ExportAsync(guideSnapshot,atomic,"en",null,cts.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
                catch(OperationCanceledException) { Check(File.ReadAllText(atomic)=="previous user output","cancel preserves existing output"); }
            }
            try
            {
                var printers=PrintService.GetPrinters();
                Console.WriteLine("Installed printer queues: "+string.Join(", ",printers.Select(p=>p.Name)));
                Check(true,"Windows print queue discovery");
                using var server=new System.Printing.LocalPrintServer();
                foreach(var printer in printers.Where(p=>args.Contains("--printers")&&(p.Name.Contains("ET-8500")||p.Name.Contains("MF210"))))
                {
                    using var queue=new System.Printing.PrintQueue(server,printer.Name);
                    var caps=queue.GetPrintCapabilities();
                    Console.WriteLine(printer.Name+" | driver="+printer.Driver+" | borderless="+string.Join(",",caps.PageBorderlessCapability));
                    if(printer.Name=="EPSON ET-8500 Series")
                    {
                        foreach(var orient in new[]{System.Printing.PageOrientation.Portrait,System.Printing.PageOrientation.Landscape})
                        {
                            var ticket=new System.Printing.PrintTicket {PageOrientation=orient,PageMediaSize=new(System.Printing.PageMediaSizeName.NorthAmericaLetter),PageBorderless=System.Printing.PageBorderless.Borderless,PageScalingFactor=100,Duplexing=System.Printing.Duplexing.OneSided};
                            var validated=queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket,ticket).ValidatedPrintTicket;
                            var specific=queue.GetPrintCapabilities(validated); var area=specific.PageImageableArea;
                            Console.WriteLine($"  {orient}: validated={validated.PageOrientation};borderless={validated.PageBorderless};media={validated.PageMediaSize?.PageMediaSizeName};area={area?.OriginWidth},{area?.OriginHeight},{area?.ExtentWidth},{area?.ExtentHeight};scale={validated.PageScalingFactor}");
                        }
                    }
                }
            }
            catch(System.Printing.PrintSystemException ex) { Console.WriteLine("Printer discovery unavailable: "+ex.GetType().Name); }
            Console.WriteLine($"PASS: {_checks} output checks; artifacts: {folder}");
            Console.WriteLine($"Working set: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MiB");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static BitmapSource CreateFixture(string path)
    {
        const int width=1000,height=750;
        byte[] pixels=new byte[width*height*3];
        for(int y=0;y<height;y++) for(int x=0;x<width;x++)
        {
            int i=(y*width+x)*3;
            bool line=x%100<3 || y%100<3;
            pixels[i]=line?(byte)25:(byte)(x*255/width);
            pixels[i+1]=line?(byte)25:(byte)(y*255/height);
            pixels[i+2]=line?(byte)25:(byte)150;
        }
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Rgb24,null,pixels,width*3); source.Freeze(); SavePng(source,path); return source;
    }
    private static void SavePng(BitmapSource source,string path)
    { var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using var file=File.Create(path); encoder.Save(file); }
    private static void Check(bool value,string name) { if(!value) throw new Exception("FAIL: "+name); _checks++; }
    private static void Near(double actual,double expected,string name) => Check(Math.Abs(actual-expected)<.001,name+$" ({actual} vs {expected})");
}
