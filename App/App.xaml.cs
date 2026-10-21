using System.Windows;
using System.IO;
namespace ArmadaAnalyzer.Desktop;

public partial class App : Application
{
    private Mutex? installerMutex;
    private static string UserDataRoot=>Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is string path && Path.IsPathFullyQualified(path)?path:Environment.GetEnvironmentVariable("LOCALAPPDATA")??"";
    protected override void OnStartup(StartupEventArgs e)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ja-JP");
        base.OnStartup(e);
        if(e.Args.FirstOrDefault()=="--tds-fast"){Shutdown(MT4BatchReporter.TdsStage.Run(e.Args[1..]));return;}
        installerMutex=new Mutex(false,@"Local\ArmadaAnalyzer.Application");
        DispatcherUnhandledException += (_, args) => { WriteError(args.Exception); MessageBox.Show(UserError.Message(args.Exception), "Auto Scope", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        try
        {
            bool smoke = e.Args.FirstOrDefault() is "--auto-smoke" or "--smoke" or "--smoke-large" or "--smoke-existing" or "--ui-performance" or "--smoke-mt5" or "--layout-audit" or "--chart-profile";
            using var startup=smoke?null:LatestInstance.Enter();
            string root = smoke ? e.Args[1] : e.Args.Length > 0 ? e.Args[0] : WorkspaceLocation.ResolveDefault(AppContext.BaseDirectory,UserDataRoot);
            var window = new MainWindow(root);
            MainWindow = window;
            if (smoke)
                window.Loaded += async (_, _) => { try { if(e.Args[0]=="--auto-smoke") await AutoBacktestSmoke.Run(window,e.Args[2],e.Args[3],e.Args.ElementAtOrDefault(4)); else if (e.Args[0] == "--chart-profile") await ChartProfile.Run(window,e.Args[2]); else if (e.Args[0] == "--layout-audit") await LayoutAudit.Run(window,e.Args[2]); else if (e.Args[0] == "--smoke-mt5") await Mt5UiSmoke.Run(window,e.Args[2],e.Args.Skip(3).ToArray()); else if (e.Args[0] == "--ui-performance") await UiPerformance.Run(window, e.Args[2]); else await UiSmoke.Run(window, e.Args[2], e.Args[0] == "--smoke-large", e.Args[0] == "--smoke-existing", e.Args.Length > 3 ? e.Args[3] : null); } catch (Exception error) { Directory.CreateDirectory(e.Args[2]); File.WriteAllText(Path.Combine(e.Args[2], "smoke.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = error.ToString() })); Shutdown(1); } };
            window.Show();
        }
        catch (Exception ex) { WriteError(ex); MessageBox.Show(UserError.Message(ex), "起動できません", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    internal static void WriteError(Exception ex)
    {
        try
        {
            string folder=Path.Combine(UserDataRoot,"ArmadaAnalyzer");Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "diagnostics.log");
            if (File.Exists(file) && new FileInfo(file).Length > 10485760)
                File.Delete(file);
            File.AppendAllText(file, $"{DateTimeOffset.Now:O} {ex}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
