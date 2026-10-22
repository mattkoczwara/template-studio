using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;
using ImageMagick.Drawing;

// A deliberately small, deterministic, redistributable known-reference set.
// Scores only measure this degradation recipe, not unobserved recovered detail.
string root = Path.GetFullPath("artifacts/quality"); Directory.CreateDirectory(root);
string waifu = Path.GetFullPath(".tools/ai/extracted/waifu2x-ncnn-vulkan-20250915-windows");
string esrgan = Path.GetFullPath(".tools/quality/esrgan");
if(args.Contains("--production")){await ProductionCandidate.RunAsync(root);return;}
if(args.Contains("--evidence")){Evidence.Build(root);return;}
var records = new List<object>();
var prior = new Dictionary<string,(string Input,string Output)>();
foreach(string report in new[]{"results.json","real-results.json","corrected640-results.json"})
{
    string path=Path.Combine(root,report);if(!File.Exists(path))continue;
    using var json=JsonDocument.Parse(File.ReadAllText(path));
    foreach(var row in json.RootElement.GetProperty("Records").EnumerateArray())
        prior[row.GetProperty("Fixture").GetString()+"/"+row.GetProperty("Candidate").GetString()]=(row.GetProperty("InputSha256").GetString()!,row.GetProperty("OutputSha256").GetString()!);
}
foreach (var fixture in new[] { (Name:"clean-vector", W:320, H:209, Jpeg:false, Fine:false), (Name:"compressed-illustration-640", W:640, H:418, Jpeg:true, Fine:false), (Name:"fine-linework", W:320, H:209, Jpeg:false, Fine:true), (Name:"degraded-linework", W:320, H:209, Jpeg:true, Fine:true), (Name:"met-woodblock-640", W:640, H:418, Jpeg:true, Fine:true) }.Where(f=>args.Contains("--corrected-640")?f.Name=="compressed-illustration-640":args.Contains("--real-only")?f.Name.StartsWith("met-"):!f.Name.StartsWith("met-")))
{
    string folder=Path.Combine(root,fixture.Name);Directory.CreateDirectory(folder);
    uint width=(uint)fixture.W*4,height=(uint)fixture.H*4;
    string reference=Path.Combine(folder,"reference.png"), input=Path.Combine(folder,"input"+(fixture.Jpeg?".jpg":".png"));
    using(var image=new MagickImage(new MagickColor("#fcf5e5"),width,height))
    {
        // Coordinates proportional to the 1280x836 drawing, preserving fixture content.
        var d=new Drawables().Viewbox(0,0,1280,836).Scaling((double)width/1280,(double)height/836).StrokeColor(new MagickColor("#283f48")).StrokeWidth(6)
            .FillColor(new MagickColor("#efbd6b")).Circle(1005,175,1095,175)
            .FillColor(new MagickColor("#8aab8d")).Bezier(new PointD(90,750),new PointD(150,260),new PointD(400,150),new PointD(500,740))
            .FillColor(new MagickColor("#c97060")).RoundRectangle(690,400,1060,730,40,40)
            .FillColor(new MagickColor("#e2bd8a")).Polygon(new PointD(610,420),new PointD(875,190),new PointD(1140,420))
            .FillColor(new MagickColor("#6f939d")).Rectangle(825,540,920,730)
            .FillColor(MagickColors.None);
        for(int x=100;x<650;x+=100)
        {
            d.Bezier(new PointD(x,780),new PointD(x+120,580),new PointD(x-100,330),new PointD(x+60,120));
            for(int y=210;y<720;y+=120)d.FillColor(new MagickColor(y%240==210?"#b8ca94":"#d4ac65")).Ellipse(x+35,y,52,20,-35,325).FillColor(MagickColors.None);
        }
        d.StrokeWidth(fixture.Fine?1.5:3);
        for(int y=90;y<800;y+=fixture.Fine?17:67)d.Bezier(new PointD(40,y),new PointD(340,y-70),new PointD(530,y+90),new PointD(650,y));
        if(fixture.Fine)for(int x=710;x<1040;x+=14)d.Line(x,450,x+35,510);
        d.Draw(image);image.Write(reference);
        image.FilterType=FilterType.Lanczos;image.Resize((uint)fixture.W,(uint)fixture.H);
        if(fixture.Jpeg){image.Blur(0,.3);image.Quality=40;image.Settings.SetDefine(MagickFormat.Jpeg,"sampling-factor","2x2");}
        image.Write(input);
    }
    if(fixture.Name.StartsWith("met-"))
    {
        using var scan=new MagickImage(Path.GetFullPath(".tools/quality/met-45434.jpg"));
        uint cropHeight=(uint)(scan.Width*(double)height/width);
        if(cropHeight<=scan.Height)scan.Crop(new MagickGeometry(0,(int)(scan.Height-cropHeight)/2,scan.Width,cropHeight));
        else scan.Crop(new MagickGeometry((int)(scan.Width-scan.Height*(double)width/height)/2,0,(uint)(scan.Height*(double)width/height),scan.Height));
        scan.ResetPage();scan.FilterType=FilterType.Lanczos;scan.Resize(width,height);scan.Write(reference);
        scan.Resize((uint)fixture.W,(uint)fixture.H);scan.Blur(0,.3);scan.Quality=40;scan.Settings.SetDefine(MagickFormat.Jpeg,"sampling-factor","2x2");scan.Write(input);
    }
    var candidates=args.Contains("--finalists")?new[]{"lanczos","cunet2","cunet4","cunet-denoise1-4"}:new[]{"lanczos","cunet2","cunet-denoise1-2","cunet4","cunet-denoise1-4","animevideo4","anime6b4","hybrid4"};
    var outputs=new List<string>{reference};
    foreach(string candidate in candidates)
    {
        string output=Path.Combine(folder,candidate+".png"); var sw=Stopwatch.StartNew();long peak=0;
        string inputHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))).ToLowerInvariant();
        bool reused=candidate!="lanczos"&&args.Contains("--reuse")&&!args.Contains("--fresh")&&File.Exists(output)&&prior.TryGetValue(fixture.Name+"/"+candidate,out var identity)&&identity.Input==inputHash&&identity.Output==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant();
        if(candidate=="lanczos") {using var image=new MagickImage(input);image.FilterType=FilterType.Lanczos;image.Resize((uint)fixture.W*2,(uint)fixture.H*2);image.Write(output);}
        else if(!reused)
        {
            bool hybrid=candidate=="hybrid4";
            bool cunet=candidate.StartsWith("cunet")||hybrid;
            string executable=Path.Combine(cunet?waifu:esrgan,cunet?"waifu2x-ncnn-vulkan.exe":"realesrgan-ncnn-vulkan.exe");
            foreach(int stage in hybrid?new[]{0,1}:new[]{0})
            {
            var p=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true,WorkingDirectory=Path.GetDirectoryName(executable)!};
            string scale=hybrid||candidate.EndsWith("2")?"2":"4";
            string intermediate=Path.Combine(folder,"hybrid-stage1.png");
            foreach(string value in new[]{"-i",hybrid&&stage==1?intermediate:input,"-o",hybrid&&stage==0?intermediate:output,"-s",scale,"-t","128","-j","1:1:1","-f","png","-m",Path.Combine(cunet?waifu:esrgan,cunet?"models-cunet":"models"),"-n",cunet?(candidate.Contains("denoise1")||hybrid&&stage==0?"1":"-1"):(candidate=="anime6b4"?"realesrgan-x4plus-anime":"realesr-animevideov3")})p.ArgumentList.Add(value);
            using var process=Process.Start(p)!;
            var stderr=process.StandardError.ReadToEndAsync();var stdout=process.StandardOutput.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try {while(!process.HasExited){try{process.Refresh();peak=Math.Max(peak,process.PeakWorkingSet64);}catch(InvalidOperationException){break;}await Task.Delay(30,timeout.Token);}}
            catch{process.Kill(true);throw;}
            await process.WaitForExitAsync();
            if(process.ExitCode!=0)throw new Exception(candidate+": "+await stderr);
            await stdout;await stderr;
            }
        }
        else Console.WriteLine("Reusing "+output);
        double seconds=sw.Elapsed.TotalSeconds;
        using var target=new MagickImage(reference);using var result=new MagickImage(output);
        uint nativeWidth=result.Width;uint nativeHeight=result.Height;
        result.FilterType=FilterType.Lanczos;result.Resize(width,height);
        string matched=Path.Combine(folder,candidate+"-matched.png");result.Write(matched);outputs.Add(matched);
        double rmse=target.Compare(result,ErrorMetric.RootMeanSquared);
        double ssim=BlockSsim(target,result);
        double psnr=20*Math.Log10(1/rmse);
        records.Add(new{Fixture=fixture.Name,Candidate=candidate,NativeWidth=nativeWidth,NativeHeight=nativeHeight,MatchedWidth=width,MatchedHeight=height,Rmse=rmse,Psnr=psnr,BlockSsim8=ssim,Reused=reused,Seconds=reused?(double?)null:seconds,WorkerPeakBytes=reused?(long?)null:peak,InputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))).ToLowerInvariant(),OutputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant()});
        Console.WriteLine($"{fixture.Name} {candidate}: PSNR {psnr:F2} SSIM {ssim:F4}, {seconds:F2}s, {peak/1048576.0:F1}MiB");
    }
    // Same coordinate crop from every asset, no independent resizing or center selection.
    using var sheet=new MagickImage(MagickColors.White,480*3,(uint)(350*((outputs.Count+2)/3)));
    for(int i=0;i<outputs.Count;i++)
    {
        using var item=new MagickImage(outputs[i]);item.Crop(new MagickGeometry((int)(width*.54),(int)(height*.22),480,300));item.ResetPage();
        sheet.Composite(item,(i%3)*480,(i/3)*350+40,CompositeOperator.Over);
        new Drawables().Font("Segoe UI").FontPointSize(18).FillColor(MagickColors.Black).Text(i%3*480+8,i/3*350+27,i==0?"HR reference":candidates[i-1]).Draw(sheet);
    }
    sheet.Write(Path.Combine(folder,"matched-crops.png"));
}
File.WriteAllText(Path.Combine(root,args.Contains("--corrected-640")?"corrected640-results.json":args.Contains("--real-only")?"real-results.json":"results.json"),JsonSerializer.Serialize(new{GeneratedUtc=DateTime.UtcNow,FixtureVersion="full-canvas-v2",Method="Lanczos 4x downsample, optional blur sigma .3 and JPEG quality40 chroma2x2; Standard is Lanczos2x resampled to4x; compare all candidates on reference4x canvas; nominal24in width=106.67dpi for640px source",Records=records},new JsonSerializerOptions{WriteIndented=true}));

static double BlockSsim(MagickImage a,MagickImage b)
{
    using var ga=a.Clone();using var gb=b.Clone();ga.ColorSpace=ColorSpace.Gray;gb.ColorSpace=ColorSpace.Gray;ga.Depth=8;gb.Depth=8;
    byte[] x=ga.ToByteArray(MagickFormat.Gray),y=gb.ToByteArray(MagickFormat.Gray);int w=(int)a.Width,h=(int)a.Height;
    double total=0;int blocks=0;
    for(int top=0;top+8<=h;top+=8)for(int left=0;left+8<=w;left+=8)
    {
        double mx=0,my=0;for(int j=0;j<8;j++)for(int i=0;i<8;i++){int p=(top+j)*w+left+i;mx+=x[p];my+=y[p];}mx/=64;my/=64;
        double vx=0,vy=0,cov=0;for(int j=0;j<8;j++)for(int i=0;i<8;i++){int p=(top+j)*w+left+i;double dx=x[p]-mx,dy=y[p]-my;vx+=dx*dx;vy+=dy*dy;cov+=dx*dy;}vx/=63;vy/=63;cov/=63;
        total+=((2*mx*my+6.5025)*(2*cov+58.5225))/((mx*mx+my*my+6.5025)*(vx+vy+58.5225));blocks++;
    }
    return total/blocks;
}
