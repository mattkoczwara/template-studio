using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core;
using Mosaic.Storage;

string testRoot = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "storage-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}
async Task Reject(Func<Task> action, string code, string label)
{
    try { await action(); throw new Exception("Accepted invalid project: " + label); }
    catch (ProjectStorageException ex) { Check(ex.Code == code, label + " (" + ex.Code + ")"); }
}
var data = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf1sAAAAASUVORK5CYII=");
string original = Path.Combine(testRoot, "download.png");
string thumbnail = Path.Combine(testRoot, "preview.png");
await File.WriteAllBytesAsync(original, data);
await File.WriteAllBytesAsync(thumbnail, data);
string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
var source = new SourceInfo(original, original, hash, 1, 1, "png", data.Length, thumbnail);
var state = new ProjectState { Title = "Żółty kwiat", Size = new(60m, 31.625m), KeepProportions = false,
    LastDimension = "height", Composition = new(FitMode.Crop, 90, .25m, .75m),
    Paper = new(8.5m, 11m, .25m, OrientationMode.Landscape), Quality = QualityMode.Enhanced,
    PrinterProfile = "canon-mf210", PrinterName = "Test printer", Copies = 2, AssemblyGuide = true };
string project = Path.Combine(testRoot, "flower.mosaic");
var store = new ProjectStore(Path.Combine(testRoot, "cache"));
await store.SaveAsync(project, state, source);
File.Delete(original);
var peek = await store.PeekAsync(project);
Check(peek.State.Title == state.Title && peek.ThumbnailBytes!.SequenceEqual(data), "Lazy manifest/thumbnail and Polish title");
Check(!Directory.Exists(Path.Combine(testRoot, "cache")), "Peek does not extract original");
var reopened = await store.OpenAsync(project);
Check((await File.ReadAllBytesAsync(reopened.OriginalPath)).SequenceEqual(data), "Embedded source survives deletion of download");
Check(reopened.State.Size == state.Size && reopened.State.Composition == state.Composition &&
    reopened.State.Paper == state.Paper && reopened.State.Quality == state.Quality &&
    reopened.State.PrinterProfile == state.PrinterProfile && reopened.State.Copies == 2 &&
    reopened.State.KeepProportions == false && reopened.State.LastDimension == "height", "Editable state round trip");
Check(reopened.State.ResolvedOrientation == SheetOrientation.Landscape && reopened.State.SourceHash == hash,
    "Saved orientation snapshot and source identity");

async Task AlterArchive(string destination, Action<ZipArchive> change)
{
    File.Copy(project, destination, overwrite: true);
    using var zip = ZipFile.Open(destination, ZipArchiveMode.Update);
    change(zip);
    await Task.CompletedTask;
}
string corruptHash = Path.Combine(testRoot, "corrupt-source.mosaic");
await AlterArchive(corruptHash, zip =>
{
    var sourceEntry = zip.GetEntry("source/original.png")!;
    sourceEntry.Delete();
    using var output = zip.CreateEntry("source/original.png").Open();
    output.Write(Encoding.UTF8.GetBytes("not the immutable source"));
});
await Reject(() => store.OpenAsync(corruptHash), "ProjectIntegrityFailed", "Modified source rejected");
string traversal = Path.Combine(testRoot, "traversal.mosaic");
await AlterArchive(traversal, zip =>
{
    zip.GetEntry("assets/thumbnail.image")!.Delete();
    using var output = zip.CreateEntry("../escape.png").Open();
    output.Write(data);
});
await Reject(() => store.OpenAsync(traversal), "ProjectInvalid", "Archive traversal rejected without extraction");
Check(!File.Exists(Path.Combine(testRoot, "escape.png")), "Traversal created no outside file");
string future = Path.Combine(testRoot, "future.mosaic");
await AlterArchive(future, zip =>
{
    var entry = zip.GetEntry("manifest.json")!;
    string json;
    using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
    var node = JsonNode.Parse(json)!;
    node["SchemaVersion"] = 99;
    entry.Delete();
    using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
    writer.Write(node.ToJsonString());
});
await Reject(() => store.OpenAsync(future), "ProjectVersionUnsupported", "Future schema rejected safely");
string badGeometry = Path.Combine(testRoot, "bad-geometry.mosaic");
await AlterArchive(badGeometry, zip =>
{
    var entry = zip.GetEntry("manifest.json")!;
    string json;
    using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
    var node = JsonNode.Parse(json)!;
    node["Size"]!["WidthInches"] = 0;
    entry.Delete();
    using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
    writer.Write(node.ToJsonString());
});
await Reject(() => store.OpenAsync(badGeometry), "ProjectInvalid", "Invalid physical geometry rejected");
string duplicate = Path.Combine(testRoot, "duplicate.mosaic");
await AlterArchive(duplicate, zip =>
{
    zip.GetEntry("assets/thumbnail.image")!.Delete();
    using var output = zip.CreateEntry("manifest.json").Open();
    output.Write(data);
});
await Reject(() => store.OpenAsync(duplicate), "ProjectInvalid", "Duplicate manifest rejected");
string extraSource = Path.Combine(testRoot, "extra-source.mosaic");
await AlterArchive(extraSource, zip =>
{
    zip.GetEntry("assets/thumbnail.image")!.Delete();
    using var output = zip.CreateEntry("source/original.jpg").Open();
    output.Write(data);
});
await Reject(() => store.OpenAsync(extraSource), "ProjectInvalid", "Unexpected alternate source rejected");
string invalidEnum = Path.Combine(testRoot, "invalid-enum.mosaic");
await AlterArchive(invalidEnum, zip =>
{
    var entry = zip.GetEntry("manifest.json")!;
    string json;
    using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
    var node = JsonNode.Parse(json)!;
    node["Composition"]!["Mode"] = 900;
    entry.Delete();
    using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
    writer.Write(node.ToJsonString());
});
await Reject(() => store.OpenAsync(invalidEnum), "ProjectInvalid", "Unknown composition mode rejected");
string bomb = Path.Combine(testRoot, "bomb.mosaic");
await AlterArchive(bomb, zip =>
{
    zip.GetEntry("source/original.png")!.Delete();
    using var output = zip.CreateEntry("source/original.png", CompressionLevel.SmallestSize).Open();
    output.Write(new byte[4 * 1024 * 1024]);
});
await Reject(() => store.OpenAsync(bomb), "ProjectTooLarge", "Extreme compression ratio rejected");
byte[] savedBefore = await File.ReadAllBytesAsync(project);
await File.WriteAllBytesAsync(original, data);
await Reject(() => store.SaveAsync(original, state, source), "SourceChanged", "Save cannot overwrite immutable source");
Check((await File.ReadAllBytesAsync(original)).SequenceEqual(data), "Source overwrite refusal preserves original bytes");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { await store.SaveAsync(project, state, source, cancellation.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { Check(true, "Save cancellation observed"); }
}
Check((await File.ReadAllBytesAsync(project)).SequenceEqual(savedBefore), "Cancelled save preserves prior project");
Check(!Directory.EnumerateFiles(testRoot, "*.tmp").Any(), "Cancelled writes leave no temporary files");
await File.WriteAllBytesAsync(original, Encoding.UTF8.GetBytes("modified"));
await Reject(() => store.SaveAsync(project, state, source), "SourceChanged", "Changed source cannot overwrite project");
Check((await File.ReadAllBytesAsync(project)).SequenceEqual(savedBefore), "Failed save preserves prior project");
var prefsStore = new PreferencesStore(Path.Combine(testRoot, "settings"));
var prefs = prefsStore.Load() with { Language = "pl", CalibrationPromptDismissed = true,
    Calibrations = [new CalibrationRecord { PrinterName = "Epson", MeasuredXInches = 3.95m, MeasuredYInches = 4.02m }] };
prefsStore.Save(prefs);
for (int i = 0; i < 12; i++) prefsStore.AddRecent(Path.Combine(testRoot, $"recent-{i}.mosaic"));
var loadedPrefs = prefsStore.Load();
Check(loadedPrefs.Language == "pl" && loadedPrefs.CalibrationPromptDismissed && loadedPrefs.Calibrations.Count == 1,
    "Language and per-printer calibration persisted");
Check(loadedPrefs.RecentProjects.Count == 8 && loadedPrefs.RecentProjects[0].EndsWith("recent-11.mosaic"), "Recent projects bounded and ordered");
await File.WriteAllTextAsync(Path.Combine(prefsStore.RootPath, "settings.json"), "broken json");
Check(prefsStore.Load().Language == "en" && File.Exists(Path.Combine(prefsStore.RootPath, "settings.json.damaged")),
    "Corrupt preferences retain backup and restore usable defaults");
string sentinel = Path.Combine(testRoot, "cache", "personal-note.txt");
string projectSentinel = Path.Combine(testRoot, "cache", new string('a',64) + ".mosaic");
await File.WriteAllTextAsync(sentinel, "Keep unknown files");
await File.WriteAllTextAsync(projectSentinel, "Keep personal projects even inside a cache folder");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await store.ClearCacheAsync(cancelled.Token); throw new Exception("Cache cancellation ignored"); }
    catch (OperationCanceledException) { Check(File.Exists(reopened.OriginalPath), "Cancelled cache cleanup changes nothing"); }
}
long removed = await store.ClearCacheAsync();
Check(removed > 0 && !File.Exists(reopened.OriginalPath) && !File.Exists(reopened.ThumbnailPath), "Cache cleanup removes extracted SHA assets");
Check(File.Exists(sentinel) && File.Exists(projectSentinel) && (await File.ReadAllBytesAsync(project)).SequenceEqual(savedBefore),
    "Cache cleanup preserves unknown files and all portable projects");
Console.WriteLine($"Storage checks: {checks} passed. Evidence: {testRoot}");
