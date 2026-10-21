using ArmadaAnalyzer;
using System.Text.Json;
static class RealReports
{
    public static void Run()
    {
        string? reportDirectory = Environment.GetEnvironmentVariable("ARMADA_REPORT_DIR"), referenceFile = Environment.GetEnvironmentVariable("ARMADA_REFERENCE_JSON");
        if (reportDirectory == null || referenceFile == null)
        {
            Console.WriteLine("SKIP private reports: set ARMADA_REPORT_DIR and ARMADA_REFERENCE_JSON for optional local fixtures");
            return;
        }
        string folder = Path.Combine(Path.GetTempPath(), "ArmadaReal-" + Guid.NewGuid().ToString("N"));
        var w = new Workspace(folder);
        foreach (var row in JsonDocument.Parse(File.ReadAllText(referenceFile)).RootElement.EnumerateArray())
        {
            string name = row.GetProperty("file").GetString()!;
            long count = row.GetProperty("closed").GetInt64(), net = row.GetProperty("net_minor").GetInt64() * 1000000;
            Program.Test("real_" + name, () => { var r = w.Import(Path.Combine(reportDirectory, name), new("JPY")); Program.Eq(count, r.Dataset.Count); var q = w.LoadSeries(new([r.Dataset.Id])); var s = Analytics.Analyze(q, r.Dataset.InitialBalance); Program.Eq(net, s.Net); Program.Eq(row.GetProperty("wins").GetInt32(), s.Wins); Program.Eq(row.GetProperty("losses").GetInt32(), s.Losses); Program.Eq(row.GetProperty("max_balance_dd_minor").GetInt64() * 1000000, s.Drawdown);
                if(row.TryGetProperty("pips_total",out var pips))Program.Eq<decimal?>(decimal.Parse(pips.GetString()!,System.Globalization.CultureInfo.InvariantCulture),s.Pips.Total);
                if(row.TryGetProperty("symbol",out var symbol))Program.Eq(symbol.GetString(),r.Dataset.Symbol);
            });
        }
    }
}
