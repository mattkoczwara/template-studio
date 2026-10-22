using System.IO;
using System.Text.Json;
using Mosaic.Core;
using Mosaic.Storage;

namespace Mosaic.App;

public partial class MainWindow
{
    // Exercises document changes from calibration without submitting printer jobs.
    internal async Task<int> RunReleaseAppChecksAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        int count = 0;
        var evidence = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Release app: " + message);
            count++; evidence.Add(message);
        }
        string previousLanguage = Lang.Language;
        try
        {
            foreach (string language in new[] { "en", "pl" })
            {
                string folder = Path.Combine(directory, language + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string input = Path.Combine(folder, "artwork.png");
                CreateArtwork(input);
                var probe = new MainWindow(["--checks", "--" + language], Path.Combine(folder, "state")) { Owner = this };
                var errors = new List<string>(); probe.ErrorPresenter = errors.Add; probe.Show();
                try
                {
                    await probe.ImportAsync(input);
                    probe.projectPath = Path.Combine(folder, "calibrated.mosaic");
                    Check(await probe.SaveAsync(false) && !probe.dirty, language + ": project starts clean");
                    var record = new CalibrationRecord { PrinterName = "Synthetic calibration printer", DriverSettings = "Synthetic settings" };
                    long beforeRevision = probe.revision;
                    probe.ApplyCalibrationRecord(record);
                    Check(probe.dirty && probe.revision > beforeRevision && probe.state.PrinterName == record.PrinterName,
                        language + ": selecting calibration printer marks document changed");
                    await probe.RecoverEditsAsync();
                    var recovered = await probe.projects.OpenAsync(probe.recovery.RecoveryPath);
                    Check(recovered.State.PrinterName == record.PrinterName, language + ": recovery records the selected printer");
                    await probe.ApplyHistoryAsync(false);
                    Check(probe.state.PrinterName == null, language + ": undo restores previous printer choice");
                    await probe.ApplyHistoryAsync(true);
                    Check(probe.state.PrinterName == record.PrinterName, language + ": redo restores calibration printer choice");
                    Check(await probe.SaveAsync(false), language + ": calibration document saves");
                    string project = probe.projectPath!;
                    Check(await probe.OpenProjectAsync(project) && !probe.dirty && probe.state.PrinterName == record.PrinterName,
                        language + ": reopened project retains calibration printer");
                    beforeRevision = probe.revision;
                    probe.ApplyCalibrationRecord(record);
                    Check(!probe.dirty && probe.revision == beforeRevision,
                        language + ": unchanged calibration does not create an artwork edit");
                    probe.ApplyCalibrationRecord(record with { EdgeClipping = true });
                    Check(probe.dirty && probe.state.Paper.MarginInches == .25m && probe.layout!.Paper == probe.state.Paper,
                        language + ": clipped calibration changes margins through the shared layout");
                    Check(await probe.SaveAsync(false) && await probe.OpenProjectAsync(project) &&
                        probe.state.PrinterName == record.PrinterName && probe.state.Paper.MarginInches == .25m,
                        language + ": printer and trim fallback persist together");
                    Check(errors.Count == 0, language + ": no editor errors during calibration persistence");
                }
                finally { await probe.FinishChecksAsync(); }
            }
            const string privatePath = @"C:\PrivateArtwork\personal-flower.png";
            var failure = new InvalidOperationException("Unable to open " + privatePath,
                new ObjectDisposedException(privatePath, "Input file: " + privatePath));
            string diagnostic = LocalLog.DescribeFailure(new ProjectStorageException("ProjectSaveFailed", failure));
            Check(!diagnostic.Contains(privatePath) && !diagnostic.Contains("personal-flower"),
                "diagnostics exclude exception messages and object names containing artwork paths");
            Check(diagnostic.Contains(typeof(InvalidOperationException).FullName!) &&
                diagnostic.Contains(typeof(ObjectDisposedException).FullName!) && diagnostic.Contains("ProjectSaveFailed") && diagnostic.Contains("HResult="),
                "diagnostics retain exception chain types, error codes and numeric failure data");
        }
        finally { Lang.Apply(previousLanguage); }
        File.WriteAllText(Path.Combine(directory, "release-app-checks.json"), JsonSerializer.Serialize(new
        { Passed = true, Checks = count, Evidence = evidence, PhysicalPrintVerified = false }, new JsonSerializerOptions { WriteIndented = true }));
        return count;
    }
}
