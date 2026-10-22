using System.Windows;
using System.IO;

namespace Mosaic.App;

public partial class App : Application
{
    internal static void FitOnScreen(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.Width = Math.Min(window.Width, area.Width);
        window.Height = Math.Min(window.Height, area.Height);
        if (!double.IsNaN(window.Left)) window.Left = Math.Clamp(window.Left, area.Left, Math.Max(area.Left, area.Right - window.Width));
        if (!double.IsNaN(window.Top)) window.Top = Math.Clamp(window.Top, area.Top, Math.Max(area.Top, area.Bottom - window.Height));
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            if (args.Handled) return;
            LocalLog.Failure("ui_failure", args.Exception);
            MessageBox.Show(Lang.T("Unexpected"), Lang.T("AppName"), MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        if (MainWindow != null) return; // A dispatcher acceptance host may already own the application window.
        int verifyIndex = Array.IndexOf(e.Args, "--verify-install");
        if (verifyIndex >= 0 && verifyIndex + 1 < e.Args.Length)
        {
            string output = Path.GetFullPath(e.Args[verifyIndex + 1]);
            Directory.CreateDirectory(output);
            var verificationWindow = new MainWindow(["--checks", "--en"], Path.Combine(output, "local-state-" + Guid.NewGuid().ToString("N")));
            verificationWindow.ErrorPresenter = message => throw new InvalidOperationException(message);
            MainWindow = verificationWindow;
            verificationWindow.ContentRendered += async (_, _) =>
            {
                int exitCode = 1;
                try
                {
                    int checks = await verificationWindow.RunAcceptanceChecksAsync(output);
                    File.WriteAllText(Path.Combine(output, "installed-runtime.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed = true, Checks = checks, Runtime = Environment.Version.ToString(), ApplicationDirectory = AppContext.BaseDirectory, PhysicalPrintVerified = false }));
                    exitCode = 0;
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); }
                finally { await verificationWindow.FinishChecksAsync(); Shutdown(exitCode); }
            };
            verificationWindow.Show();
            return;
        }
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
}

internal static class LocalLog
{
    // Keep the operation, exception chain and stack, without arbitrary I/O messages
    // that can contain a user's artwork filenames or output paths.
    public static void Failure(string stage, Exception error)
    {
        var details = new System.Text.StringBuilder();
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            details.AppendLine($"{current.GetType().FullName} HResult=0x{current.HResult:X8}");
            if (current is ObjectDisposedException disposed) details.AppendLine("Object: " + disposed.ObjectName);
            else if (current is InvalidOperationException) details.AppendLine(current.Message);
            if (current is Mosaic.Storage.ProjectStorageException storage) details.AppendLine("Code: " + storage.Code);
            details.AppendLine(current.StackTrace);
        }
        Write(stage, details.ToString());
    }

    public static void Write(string stage, string kind = "")
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosaicTemplateStudio", "logs");
            Directory.CreateDirectory(dir);
            foreach (var file in new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(5)) file.Delete();
            var path = Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTime.UtcNow:O} {stage} {kind}\n");
        }
        catch { /* Diagnostics must never prevent editing or recovery. */ }
    }
}
