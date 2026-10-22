using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;
using ImageMagick.Drawing;
using Mosaic.Imaging;
using Mosaic.Core;

var root = Path.GetFullPath("artifacts/ai-smoke"); Directory.CreateDirectory(root);
if(args.Contains("--long-path-only")){await LongPathChecks.RunAsync(root);return;}
if(args.Contains("--cache-only")){await CacheBudgetChecks.RunAsync(root);return;}
await SelectionChecks.RunAsync(root); if(args.Contains("--selection-only"))return;
var runId = Guid.NewGuid().ToString("N");
var images = new ImageService(Path.Combine(root,"cache"));
string fixture = Path.Combine(root,"artwork.png");
using(var image = new MagickImage(MagickColors.White,320,240))
{
    new Drawables().FillColor(new MagickColor("#d8e5d1")).Rectangle(0,0,320,240)
        .FillColor(new MagickColor("#f2c56b")).Circle(235,70,265,70)
        .FillColor(new MagickColor("#6e987a")).Polygon(new PointD(0,190),new PointD(90,60),new PointD(180,190))
        .FillColor(new MagickColor("#47756a")).Polygon(new PointD(100,220),new PointD(200,105),new PointD(320,220))
        .StrokeColor(new MagickColor("#183b35")).StrokeWidth(2).FillColor(MagickColors.None)
        .Bezier(new PointD(20,210),new PointD(80,80),new PointD(160,260),new PointD(300,90))
        .Line(0,128,320,128).Line(128,0,128,240).Draw(image);
    image.Write(fixture);
}
var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)));
var source = await images.ImportAsync(fixture);
if(source.Width!=320||source.Height!=240)throw new Exception("dimensions");
if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)))!=before)throw new Exception("original changed");
var preview=await images.GetPreviewAsync(source.NormalizedPath,90);
if(!preview.IsFrozen||preview.PixelWidth!=240||preview.PixelHeight!=320)throw new Exception("preview");
var standard=await images.GetOutputImageAsync(source);
var si = new MagickImageInfo(standard);
if(si.Width!=640||si.Height!=480)throw new Exception("standard upscale");
foreach(var ext in new[]{"jpg","webp","bmp"})
{
    using var image=new MagickImage(fixture);string path=Path.Combine(root,"format."+ext);image.Write(path);
    var imported=await images.ImportAsync(path);
    if(imported.Width!=320||imported.Height!=240)throw new Exception(ext);
}
using(var oriented = new MagickImage(MagickColors.Red,40,20))
{
    oriented.Orientation=Orientation.RightTop;
    var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)6); oriented.SetProfile(exif);
    string path=Path.Combine(root,"orientation.jpg");oriented.Write(path);
    var imported=await images.ImportAsync(path);
    if(imported.Width!=20||imported.Height!=40)throw new Exception("EXIF orientation");
}
using(var image = new MagickImage(MagickColors.Transparent,16,16))
{
    string alpha=Path.Combine(root,"alpha.png");image.Write(alpha);var ai=await images.ImportAsync(alpha);
    using var normalized=new MagickImage(ai.NormalizedPath);
    if(normalized.HasAlpha)throw new Exception("alpha not flattened");
    using var pixels=normalized.GetPixels();var color=pixels.GetPixel(0,0).ToColor();
    if(color==null||color.R!=255||color.G!=255||color.B!=255)throw new Exception("alpha background not white");
}
using(var profiled = new MagickImage(new MagickColor(200,100,50),16,16))
{
    profiled.SetProfile(ColorProfiles.AdobeRGB1998);
    string path=Path.Combine(root,"icc.png");profiled.Write(path);
    var imported=await images.ImportAsync(path);
    using var normalized=new MagickImage(imported.NormalizedPath);
    using var pixels=normalized.GetPixels();var color=pixels.GetPixel(0,0).ToColor();
    // The PNG encoder may replace the standard ICC profile with equivalent sRGB/gamma chunks.
    if(normalized.ColorSpace!=ColorSpace.sRGB)throw new Exception("ICC sRGB metadata missing");
    if(color==null||Math.Abs(color.R-227)>1||Math.Abs(color.G-100)>1||Math.Abs(color.B-42)>1)throw new Exception("ICC conversion was not applied");
}
var enhancer=new EnhancementService(Path.GetFullPath("vendor/ai"),Path.Combine(root,"gpu-cache-"+runId));
enhancer.VerifyAssets();
var sw=Stopwatch.StartNew();
var enhanced=await enhancer.EnhanceAsync(source,new Progress<JobProgress>(p=>{}),CancellationToken.None);
double gpu=sw.Elapsed.TotalSeconds;
var ei=new MagickImageInfo(enhanced);int factor=EnhancementService.OutputScale(source);if(ei.Width!=source.Width*factor||ei.Height!=source.Height*factor)throw new Exception("enhanced dimensions");
File.Copy(enhanced,Path.Combine(root,"enhanced-gpu.png"),true);
File.Copy(standard,Path.Combine(root,"standard.png"),true);
await images.CreateDetailComparisonAsync(source,enhanced);
var cpu=new EnhancementService(Path.GetFullPath("vendor/ai"),Path.Combine(root,"cpu-cache-"+runId));
sw.Restart();var cpuPath=await cpu.EnhanceAsync(source,null,CancellationToken.None,true);double cpuSeconds=sw.Elapsed.TotalSeconds;
using var gpuImage=new MagickImage(enhanced);using var cpuImage=new MagickImage(cpuPath);
double cpuDifference=gpuImage.Compare(cpuImage,ErrorMetric.RootMeanSquared);
// Cancellation must terminate an actual CPU inference promptly and leave no published artifact.
var cancel=new EnhancementService(Path.GetFullPath("vendor/ai"),Path.Combine(root,"cancel-cache-"+runId));
using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(20));bool cancelled=false;var cancelLatency=new Stopwatch();
var cancelProgress=new InlineProgress(p=>{if(p.Fraction>0&&!cts.IsCancellationRequested){cancelLatency.Start();cts.Cancel();}});
try{await cancel.EnhanceAsync(source,cancelProgress,cts.Token,true);}catch(OperationCanceledException){cancelled=true;}
if(!cancelled)throw new Exception("cancel not observed");
bool cancelledDuringInference=cancelLatency.IsRunning;cancelLatency.Stop();if(!cancelledDuringInference)throw new Exception("cancel was not during inference");
if(cancelLatency.ElapsedMilliseconds>5000)throw new Exception("cancel too slow");
// A corrupt raster must be rejected and the known source must remain usable afterwards.
string broken=Path.Combine(root,"broken.png");File.WriteAllText(broken,"not an image");bool rejected=false;
try{await images.ImportAsync(broken);}catch(ImagePipelineException){rejected=true;}
if(!rejected)throw new Exception("corrupt import accepted");
// Cache corruption is detected by source SHA and derived-image sidecars, never silently printed.
File.WriteAllText(source.NormalizedPath,"corrupted cache");File.WriteAllText(source.PreviewPath,"corrupted preview");
source=await images.ImportAsync(fixture);
using(var repaired=new MagickImage(source.NormalizedPath)){if(repaired.Width!=320)throw new Exception("cache repair");}
File.WriteAllText(standard,"corrupted standard");standard=await images.GetOutputImageAsync(source);
using(var repaired=new MagickImage(standard)){if(repaired.Width!=640)throw new Exception("standard cache repair");}
byte[] cachedOriginal=File.ReadAllBytes(source.OriginalPath);File.WriteAllText(source.OriginalPath,"corrupted immutable source");
bool originalRejected=false;
try{await images.ImportAsync(fixture);}catch(ImagePipelineException){originalRejected=true;}
finally{File.WriteAllBytes(source.OriginalPath,cachedOriginal);}
if(!originalRejected)throw new Exception("corrupt source hash ignored");
string badAssets=Path.Combine(root,"bad-assets-"+runId);Directory.CreateDirectory(badAssets);
foreach(string item in Directory.EnumerateFiles("vendor/ai","*",SearchOption.AllDirectories))
{
    string relative=Path.GetRelativePath("vendor/ai",item),to=Path.Combine(badAssets,relative);Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(item,to);
}
using(var corrupt=File.OpenWrite(Path.Combine(badAssets,"models-cunet","scale2.0x_model.bin"))){corrupt.WriteByte(0xff);}
bool integrityRejected=false;try{new EnhancementService(badAssets).VerifyAssets();}catch(EnhancementException e)when(e.Code=="aiIntegrity"){integrityRejected=true;}
if(!integrityRejected)throw new Exception("modified executable/model accepted");
await images.ClearCacheAsync(source.Sha256);
if(!File.Exists(source.OriginalPath)||!File.Exists(source.NormalizedPath))throw new Exception("cache cleanup lost current artwork");
var summary=new{Passed=true,GpuSeconds=gpu,CpuSeconds=cpuSeconds,GpuCpuRmse=cpuDifference,GpuWorkerPeakBytes=enhancer.LastWorkerPeakBytes,CpuWorkerPeakBytes=cpu.LastWorkerPeakBytes,InputWidth=source.Width,InputHeight=source.Height,OutputWidth=ei.Width,OutputHeight=ei.Height,PreviewFrozen=preview.IsFrozen,Cancellation=cancelled,CancellationAfterInferenceMs=cancelLatency.ElapsedMilliseconds,CacheIntegrity=integrityRejected,SourceHashValidation=originalRejected,Formats=new[]{"PNG","JPEG","WebP","BMP"},PeakWorkingSet=Process.GetCurrentProcess().PeakWorkingSet64};
File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(summary));
await CacheBudgetChecks.RunAsync(root);
await LongPathChecks.RunAsync(root);
if(args.Contains("--large"))await ExtendedChecks.RunAsync(root,images);

sealed class InlineProgress(Action<JobProgress> callback):IProgress<JobProgress>{public void Report(JobProgress value)=>callback(value);}
