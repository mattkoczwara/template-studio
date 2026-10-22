using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mosaic.Core;
using Mosaic.Imaging;

namespace Mosaic.App;

public partial class MainWindow
{
    /// <summary>Real enhancement/save/reopen journeys, including explicit recipe migration in both languages.</summary>
    internal async Task<int> RunEnhancementProjectChecksAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        int count = 0;
        var evidence = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Enhancement project: " + message);
            count++; evidence.Add(message);
        }
        foreach (string language in new[] { "en", "pl" })
        {
            string folder = Path.Combine(directory, "recipe-" + language + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string input = Path.Combine(folder, "small-illustration.png");
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Ivory, null, new Rect(0, 0, 160, 120));
                dc.DrawEllipse(Brushes.DarkSeaGreen, new Pen(Brushes.DarkSlateGray, 1.3), new Point(80, 56), 52, 40);
                dc.DrawLine(new Pen(Brushes.DarkSlateGray, 1), new Point(10, 100), new Point(142, 14));
            }
            var raster = new RenderTargetBitmap(160, 120, 96, 96, PixelFormats.Pbgra32); raster.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(raster));
            using (var stream = File.Create(input)) encoder.Save(stream);
            string originalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input)));
            var probe = new MainWindow(["--checks", "--" + language], Path.Combine(folder, "state")) { Owner = this };
            var errors = new List<string>(); probe.ErrorPresenter = errors.Add; probe.Show();
            try
            {
                Check(new ProjectState().EnhancementModel == EnhancementService.ModelId, language + ": new project defaults to the current recipe");
                await probe.ImportAsync(input);
                Check(probe.source != null, language + ": small artwork imports");
                var expectedSize = new ArtworkSize(24.125m, 18.09375m);
                probe.state = probe.state with { Size = expectedSize, KeepProportions = false, Composition = new(FitMode.Fit, 90),
                    EnhancementModel = "waifu2x-cunet-20250915-scale2x-no-denoise" };
                probe.SyncControls();
                var expectedLayout = probe.layout!;
                await probe.EnhanceAsync();
                Check(errors.Count == 0 && probe.state.Quality == QualityMode.Enhanced && probe.enhancedPath != null,
                    language + ": real enhancement succeeds");
                Check(probe.state.EnhancementModel == EnhancementService.ModelId, language + ": successful enhancement records the actual recipe");
                Check(probe.state.Size == expectedSize && probe.state.Composition.Rotation == 90 && probe.layout!.PageCount == expectedLayout.PageCount,
                    language + ": enhancement preserves exact size, rotation and page count");
                var selectedSnapshot = await probe.SnapshotAsync(CancellationToken.None);
                Check(selectedSnapshot.State.EnhancementModel == EnhancementService.ModelId && File.Exists(selectedSnapshot.ImagePath),
                    language + ": output snapshot identifies the selected recipe");
                string currentProject = Path.Combine(folder, "current.mosaic"); probe.projectPath = currentProject;
                Check(await probe.SaveAsync(false), language + ": enhanced project saves");
                var current = await probe.projects.OpenAsync(currentProject);
                Check(current.State.EnhancementModel == EnhancementService.ModelId && current.State.Size == expectedSize &&
                    current.State.Composition.Rotation == 90 && current.State.Quality == QualityMode.Enhanced,
                    language + ": portable manifest preserves actual recipe and exact geometry");
                Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(current.OriginalPath))) == originalHash,
                    language + ": portable project retains immutable original bytes");
                File.Delete(input);
                Check(await probe.OpenProjectAsync(currentProject) && !probe.dirty && probe.state.EnhancementModel == EnhancementService.ModelId,
                    language + ": current recipe reopens cleanly after the download is removed");

                string legacyProject = Path.Combine(folder, "legacy.mosaic");
                var legacy = current.State with { EnhancementModel = "waifu2x-cunet-20250915-scale2x-no-denoise" };
                await probe.projects.SaveAsync(legacyProject, legacy, probe.source!);
                Check(await probe.OpenProjectAsync(legacyProject), language + ": legacy Enhanced project opens");
                Check(probe.state.EnhancementModel == EnhancementService.ModelId && probe.state.Quality == QualityMode.Enhanced && probe.dirty,
                    language + ": migration updates the recipe and marks the changed project unsaved");
                Check(probe.Status.Text == Lang.T("AiRecipeUpdated") && probe.Status.Text != "AiRecipeUpdated",
                    language + ": migration explains the new result with localized compare/save guidance");
                Check(probe.state.Size == expectedSize && probe.state.Composition == legacy.Composition &&
                    probe.layout!.PageCount == expectedLayout.PageCount && probe.source!.Sha256.Equals(originalHash, StringComparison.OrdinalIgnoreCase),
                    language + ": migration retains exact geometry, composition and original identity");
                Check(await probe.SaveAsync(false), language + ": migrated project saves without a new destination");
                var migrated = await probe.projects.OpenAsync(legacyProject);
                Check(migrated.State.EnhancementModel == EnhancementService.ModelId && migrated.State.Size == expectedSize &&
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(migrated.OriginalPath))) == originalHash,
                    language + ": resaved migration records the current recipe and original bytes");
                Check(await probe.OpenProjectAsync(legacyProject) && !probe.dirty && probe.Status.Text != Lang.T("AiRecipeUpdated"),
                    language + ": subsequent reopen avoids a repeated migration notice");
                Check(errors.Count == 0, language + ": no editor errors during recipe persistence and migration");
            }
            finally { await probe.FinishChecksAsync(); }
        }
        File.WriteAllText(Path.Combine(directory, "enhancement-project-checks.json"), JsonSerializer.Serialize(new
        { Passed = true, Checks = count, Model = EnhancementService.ModelId, Evidence = evidence, PhysicalPrintVerified = false }, new JsonSerializerOptions { WriteIndented = true }));
        return count;
    }
}
