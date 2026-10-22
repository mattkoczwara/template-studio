using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ImageMagick;
using Mosaic.Imaging;

internal static class ProductionCandidate
{
    public static async Task RunAsync(string root)
    {
        string folder=Path.Combine(root,"production-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var images=new ImageService(Path.Combine(folder,"cache"));
        var source=await images.ImportAsync(Path.Combine(root,"compressed-illustration-640/input.jpg"));
        using var expected=new MagickImage(Path.Combine(root,"compressed-illustration-640/cunet-denoise1-4.png"));
        var records=new List<object>();
        foreach(bool cpu in new[]{false,true})
        {
            var service=new EnhancementService(Path.GetFullPath("vendor/ai"),Path.Combine(folder,cpu?"cpu":"gpu"));
            var sw=Stopwatch.StartNew();string output=await service.EnhanceAsync(source,null,CancellationToken.None,cpu);double seconds=sw.Elapsed.TotalSeconds;
            using var image=new MagickImage(output);double rmse=expected.Compare(image,ErrorMetric.RootMeanSquared);
            if(image.Width!=2560||image.Height!=1672||rmse>.015)throw new Exception("Production recipe differs from selected candidate");
            long peak=service.LastWorkerPeakBytes;
            string firstHash=ImageServiceHash(output);string cached=await service.EnhanceAsync(source,null,CancellationToken.None,cpu);
            if(cached!=output||ImageServiceHash(cached)!=firstHash||service.LastBackend!="cache")throw new Exception("Production cache reuse");
            records.Add(new{Backend=cpu?"CPU":"Vulkan",Seconds=seconds,WorkerPeakBytes=peak,CandidateRmse=rmse,Output=output,OutputWidth=image.Width,OutputHeight=image.Height,CacheReuse=true});
        }
        File.WriteAllText(Path.Combine(root,"production.json"),JsonSerializer.Serialize(new{Model=EnhancementService.ModelId,Records=records},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(records));
    }
    static string ImageServiceHash(string path)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}
