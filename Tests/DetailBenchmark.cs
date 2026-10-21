using ArmadaAnalyzer;
using System.Diagnostics;
using System.Text.Json;
static class DetailBenchmark
{
    public static int Run(string root)
    {
        var w=new Workspace(root); var filter=new Filter(w.ListDatasets().Select(d=>d.Id).ToArray());
        var snapshot=w.LoadSeries(filter); var times=new List<double>();
        for(int i=0;i<5;i++) { var watch=Stopwatch.StartNew(); var a=w.LoadTradeDetails(filter,snapshot.Points.Length); times.Add(watch.Elapsed.TotalMilliseconds); Program.Eq(snapshot.Points.Length,a.Count); }
        var durationTimes=new List<double>();
        for(int i=0;i<5;i++) { var watch=Stopwatch.StartNew(); var a=w.LoadTradeDetails(filter,snapshot.Points.Length,includePips:false); durationTimes.Add(watch.Elapsed.TotalMilliseconds); Program.Eq(snapshot.Points.Length,a.Count); }
        Console.WriteLine(JsonSerializer.Serialize(new {count=snapshot.Points.Length,duration_only_runs_ms=durationTimes,duration_only_median_ms=durationTimes.Order().ElementAt(2),runs_ms=times,median_ms=times.Order().ElementAt(2),memory_for_duration_array_bytes=snapshot.Points.Length*8L,method="full DB scan, all pip and holding metrics, in-place median; excludes initial normalized series load and UI; five runs, no result cache"}));return 0;
    }
}
