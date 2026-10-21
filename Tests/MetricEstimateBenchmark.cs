using ArmadaAnalyzer;
using System.Diagnostics;
using System.Text.Json;

static class MetricEstimateBenchmark
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var random=new Random(17);
        var points=Enumerable.Range(0,1000000).Select(i=>new SeriesPoint(i,random.Next(-500,751)*Amount.Scale,1)).ToArray();
        long capital=Amount.Parse("1000000");var measured=Analytics.Analyze(points,capital);
        var watch=Stopwatch.StartNew();long allocated=GC.GetTotalAllocatedBytes(true);
        var result=MetricEstimates.Run(points,measured,capital,new());
        long bytes=GC.GetTotalAllocatedBytes(true)-allocated;watch.Stop();double ms=watch.Elapsed.TotalMilliseconds;
        using var cancel=new CancellationTokenSource();cancel.CancelAfter(20);watch.Restart();bool canceled=false;
        try{MetricEstimates.Run(points,measured,capital,new(),cancel.Token);}catch(OperationCanceledException){canceled=true;}
        watch.Stop();
        File.WriteAllText(Path.Combine(output,"metric-estimate-benchmark.json"),JsonSerializer.Serialize(new{
            count=points.Length,trials=result.Trials,workers=2,milliseconds=ms,allocated_bytes=bytes,
            result.BlockLength,result.EffectiveSets,result.ProfitFactor,result.DrawdownPercent,
            cancellation_observed=canceled,cancellation_ms=watch.Elapsed.TotalMilliseconds,
            method="managed full-history weighted circular blocks; model data preparation excluded; not GPU frame timing"
        },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(File.ReadAllText(Path.Combine(output,"metric-estimate-benchmark.json")));
        return result.Trials==1000 && canceled?0:1;
    }
}
