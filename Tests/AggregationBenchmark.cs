using ArmadaAnalyzer;
using System.Diagnostics;
using System.Text.Json;
static class AggregationBenchmark
{
    public static int Run()
    {
        var points = new SeriesPoint[1000000];
        long start = new DateTime(2006,1,1).Ticks;
        for(int i=0;i<points.Length;i++) points[i]=new(start+i*TimeSpan.TicksPerMinute,(i%7-2)*100,1+i%3,(i%7-2)*100);
        var rows=new List<object>();
        void Measure(string name, Action action) { action(); var times=new List<double>();long allocated=GC.GetAllocatedBytesForCurrentThread(); for(int i=0;i<5;i++){var w=Stopwatch.StartNew();action();times.Add(w.Elapsed.TotalMilliseconds);}times.Sort();rows.Add(new{name,median_ms=times[2],average_allocated_bytes=(GC.GetAllocatedBytesForCurrentThread()-allocated)/5}); }
        Measure("trade_statistics_1000000",()=>Analytics.Analyze(points,Amount.Parse("1000000")));
        var snapshot=new Snapshot("benchmark",points,false){PipFactors=new Dictionary<long,int>{{1,10000},{2,100},{3,10000}}};
        Measure("trade_statistics_with_pips_1000000",()=>Analytics.Analyze(snapshot,Amount.Parse("1000000")));
        Measure("full_analysis_pipeline_1000000",()=>{Analytics.Analyze(snapshot,Amount.Parse("1000000"));Grouping.Compute(points,1);DashboardAggregation.Months(points);DashboardAggregation.Compute(points,DashboardPeriod.Year);Portfolio.Correlations(points);});
        Measure("pips6_three_datasets_1000000",()=>PipStatistics.Compute(points,snapshot.PipFactors));
        Measure("group_month",()=>Grouping.Compute(points,1));
        Measure("dashboard_year",()=>DashboardAggregation.Compute(points,DashboardPeriod.Year));
        Measure("monthly_matrix",()=>DashboardAggregation.Months(points));
        Measure("correlations_three",()=>Portfolio.Correlations(points));
        for(int i=0;i<points.Length;i++) points[i]=points[i] with { DatasetId=1 };
        Measure("correlations_single",()=>Portfolio.Correlations(points));
        Measure("pips6_single_dataset_1000000",()=>PipStatistics.Compute(points,new Dictionary<long,int>{{1,10000}}));
        Console.WriteLine(JsonSerializer.Serialize(rows));return 0;
    }
}
