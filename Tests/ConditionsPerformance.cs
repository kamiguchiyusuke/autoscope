using ArmadaAnalyzer;
using System.Diagnostics;
using System.Text.Json;
static class ConditionsPerformance
{
    public static int Run(string workspaceRoot, string output)
    {
        var workspace = new Workspace(workspaceRoot);
        var ids = workspace.ListDatasets().Select(d => d.Id).ToArray();
        var rows = new List<object>();
        foreach (var pair in new[] { ("minute0", new EntryCalendar(Minutes: 1)), ("january", new EntryCalendar(Months: 1)) })
        {
            var watch = Stopwatch.StartNew();
            var snapshot = workspace.LoadSeries(new(ids, Calendar: pair.Item2));
            watch.Stop();
            rows.Add(new
            {
                filter = pair.Item1,
                count = snapshot.Points.Length,
                ms = watch.Elapsed.TotalMilliseconds,
                cache_hit = snapshot.CacheHit
            });
        }
        Program.Throws<InvalidOperationException>(() => workspace.LoadSeries(new(ids), 64));
        var timer = Stopwatch.StartNew();
        var scatter = workspace.LoadHoldingScatter(new(ids));
        timer.Stop();
        if (scatter.Total != 1000000 || scatter.Points.Length > 6008)
            throw new Exception("scatter bound/count mismatch");
        rows.Add(new
        {
            filter = "holding_all",
            count = scatter.Total,
            points = scatter.Points.Length,
            ms = timer.Elapsed.TotalMilliseconds
        });
        string result = JsonSerializer.Serialize(new
        {
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            measurements = rows,
            peak_working_set = Process.GetCurrentProcess().PeakWorkingSet64,
            low_memory_budget_rejected = true,
            method = "existing normalized million-trade workspace; one observed read per filter, cache_hit indicates whether payload was reused; no import time"
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, result);
        Console.WriteLine(result);
        return 0;
    }
}
