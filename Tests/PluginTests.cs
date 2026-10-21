using ArmadaAnalyzer;
using ArmadaAnalyzer.Contracts;
static class PluginTests
{
    public static void Run()
    {
        var manager = new PluginManager();
        Program.Test("public_plugin_contract_discovery_and_execution", () => { manager.Load(Path.Combine(AppContext.BaseDirectory, "ArmadaAnalyzer.SamplePlugins.dll")); Program.Eq(6, manager.Plugins.Count); var metrics = manager.Plugins.OfType<IAnalysisPlugin>().Single(); var output = manager.Run(metrics, new([new(1, 100000000, 1), new(2, -50000000, 1)], [new(1, "sample", "EURUSD", "USD", 10000000000)], new Dictionary<string, string>()), default); Program.Assert(output.Metrics.Any(x => x.Name == "取引数" && x.Value == "2"), "sample plugin did not run"); });
        Program.Test("report_reader_plugin_reaches_workspace", () => { string root = Path.Combine(Path.GetTempPath(), "ArmadaPlugin-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); string file = Path.Combine(root, "sample.armadacsv"); File.WriteAllText(file, "Open,Close,Side,Volume,Entry,Exit,Profit,Ticket\n2020-01-01 00:00:00,2020-01-01 01:00:00,buy,0.1,1.1,1.2,25,1\n"); var workspace = new Workspace(Path.Combine(root, "workspace")); var result = workspace.ImportPlugin(file, manager.Plugins.OfType<IReportReaderPlugin>().Single(), new("USD")); var points = workspace.LoadSeries(new([result.Dataset.Id])); Program.Eq(Amount.Parse("25"), points.Points[0].Profit); });
    }
}
