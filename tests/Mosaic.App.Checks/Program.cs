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
        var window = new MainWindow(["--checks", "--en"], Path.Combine(output, "local-state-" + Guid.NewGuid().ToString("N")));
        application.MainWindow = window;
        window.ContentRendered += async (_, _) =>
        {
            try { int count = await window.RunAcceptanceChecksAsync(output); Console.WriteLine($"PASS: {count} integrated editor checks; rendered EN/PL views at 100/125/150%."); exitCode = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); }
            finally { window.FinishChecks(); application.Shutdown(); }
        };
        window.Show(); application.Run(); return exitCode;
    }
}
