using System.IO;
using System.Windows;
using Mosaic.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/app-checks");
        Directory.CreateDirectory(output);
        int exitCode = 1;
        var application = new App(); application.InitializeComponent();
        var dispatcherErrors = new List<Exception>();
        application.DispatcherUnhandledException += (_, e) => { dispatcherErrors.Add(e.Exception); Console.Error.WriteLine(e.Exception); e.Handled = true; };
        var window = new MainWindow(["--checks", "--en"], Path.Combine(output, "local-state-" + Guid.NewGuid().ToString("N")));
        application.MainWindow = window;
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                int count = args.Contains("--release-app-only") ? await window.RunReleaseAppChecksAsync(output) : args.Contains("--enhancement-project-only") ? await window.RunEnhancementProjectChecksAsync(output) : args.Contains("--comparison-only") ? await ComparisonChecks.RunAsync(window, output) : args.Contains("--lifecycle") ? await window.RunLifecycleChecksAsync(output) : await window.RunAcceptanceChecksAsync(output);
                await application.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (dispatcherErrors.Count > 0) throw new AggregateException("Unhandled dispatcher exceptions during checks", dispatcherErrors);
                Console.WriteLine($"PASS: {count} {(args.Contains("--lifecycle") ? "lifecycle" : "integrated editor")} checks; no unhandled dispatcher exceptions.");
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); }
            finally
            {
                try { await window.FinishChecksAsync(); }
                catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
                application.Shutdown();
            }
        };
        window.Show(); application.Run(); return dispatcherErrors.Count > 0 ? 1 : exitCode;
    }
}
