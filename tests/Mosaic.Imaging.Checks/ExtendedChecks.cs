using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ImageMagick;
using ImageMagick.Drawing;
using Mosaic.Core;
using Mosaic.Imaging;
using Mosaic.Output;

internal static class ExtendedChecks
{
    public static async Task RunAsync(string root, ImageService images)
    {
        string folder=Path.Combine(root,"large-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string fixture=Path.Combine(folder,"illustration-1200x900.png");
        using(var image=new MagickImage(new MagickColor("#fbf7ed"),1200,900))
        {
            var draw=new Drawables().StrokeColor(new MagickColor("#335b50")).StrokeWidth(2.5).FillColor(MagickColors.None);
            for(int x=50;x<1200;x+=160)
            {
                draw.Bezier(new PointD(x,900),new PointD(x-150,600),new PointD(x+160,350),new PointD(x,50));
                for(int y=100;y<850;y+=130)
                {
                    draw.FillColor(new MagickColor(y%260==100?"#bdd0ad":"#e1be85"))
                        .Ellipse(x+30,y,44,18,-35,325).FillColor(MagickColors.None);
                }
            }
            draw.StrokeWidth(.8);
            for(int y=70;y<900;y+=101)draw.Line(0,y,1200,900-y);
            draw.Draw(image);image.Write(fixture);
        }
        var source=await images.ImportAsync(fixture);
        if((long)source.Width*source.Height<1_000_000)throw new Exception("large fixture below1MP");
        var gpu=new EnhancementService(Path.GetFullPath("vendor/ai"),Path.Combine(folder,"gpu"));
        var cpu=new EnhancementService(Path.GetFullPath("vendor/ai"),Path.Combine(folder,"cpu"));
        var sw=Stopwatch.StartNew();
        string gpuPath=await gpu.EnhanceAsync(source,new InlineProgress(p=>{}),CancellationToken.None);double gpuSeconds=sw.Elapsed.TotalSeconds;
        Console.WriteLine($"LargeGPU complete: {gpuSeconds:F2}s");
        sw.Restart();string cpuPath=await cpu.EnhanceAsync(source,null,CancellationToken.None,true);double cpuSeconds=sw.Elapsed.TotalSeconds;
        using var gpuImage=new MagickImage(gpuPath);using var cpuImage=new MagickImage(cpuPath);
        double difference=gpuImage.Compare(cpuImage,ErrorMetric.RootMeanSquared);
        if(gpuImage.Width!=2400||gpuImage.Height!=1800||difference>.015)throw new Exception("large AI output consistency");
        // Flat strips adjacent to internal128px/64px tile boundaries may differ by real art;
        // compare GPU/CPU on those coordinates rather than mistaking real linework for a seam.
        double edgeDifference=0;int edges=0;
        for(int x=256;x<2400;x+=256)
        {
            using var a=gpuImage.Clone();using var b=cpuImage.Clone();
            a.Crop(new MagickGeometry(x-2,0,4,1800));b.Crop(new MagickGeometry(x-2,0,4,1800));
            edgeDifference+=a.Compare(b,ErrorMetric.RootMeanSquared);edges++;
        }
        edgeDifference/=edges;
        if(edgeDifference>.02)throw new Exception("inference tile boundary consistency");
        var state=new ProjectState{Title="Large AI illustration",Size=new(60,45),Quality=QualityMode.Enhanced};
        var layout=LayoutEngine.Calculate(state.Size,state.Paper);
        var snapshot=new RenderSnapshot(source,gpuPath,state,layout);
        string pdf=Path.Combine(folder,"60-inch-enhanced.pdf");sw.Restart();
        var result=await new PdfExporter().ExportAsync(snapshot,pdf);double pdfSeconds=sw.Elapsed.TotalSeconds;
        if(layout.PageCount!=36||layout.Orientation!=SheetOrientation.Landscape||!File.Exists(pdf))throw new Exception("large 60-inch export");
        var summary=new{Passed=true,InputWidth=source.Width,InputHeight=source.Height,OutputWidth=gpuImage.Width,OutputHeight=gpuImage.Height,
            GpuSeconds=gpuSeconds,CpuSeconds=cpuSeconds,GpuWorkerPeakBytes=gpu.LastWorkerPeakBytes,CpuWorkerPeakBytes=cpu.LastWorkerPeakBytes,
            GpuCpuRmse=difference,BoundaryRmse=edgeDifference,PdfSeconds=pdfSeconds,PdfPages=layout.PageCount,PdfBytes=new FileInfo(pdf).Length,
            TempBytes=Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories).Sum(f=>new FileInfo(f).Length),
            HostPeakBytes=Process.GetCurrentProcess().PeakWorkingSet64,Pdf=pdf};
        File.WriteAllText(Path.Combine(root,"large-result.json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(summary));
    }
}
