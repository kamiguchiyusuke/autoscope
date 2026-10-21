using ArmadaAnalyzer;
using System.Text.Json;
static class Program
{
    static int failures, passed;
    public static void Assert(bool x, string message)
    {
        if (!x)
            throw new Exception(message);
    }
    public static void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected {expected}, actual {actual}");
    }
    public static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T) { return; }
        throw new Exception("expected " + typeof(T).Name);
    }
    public static void Test(string name, Action action)
    {
        try
        {
            action();
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static int Main(string[] args)
    {
        if(args.FirstOrDefault()=="metric-estimate-benchmark")return MetricEstimateBenchmark.Run(args[1]);
        if (args.FirstOrDefault() == "detail-benchmark") return DetailBenchmark.Run(args[1]);
        if (args.FirstOrDefault() == "aggregation-benchmark") return AggregationBenchmark.Run();
        if (args.FirstOrDefault() == "conditions-performance")
            return ConditionsPerformance.Run(args[1], args[2]);
        if (args.FirstOrDefault() == "data-performance")return Performance.Run(Path.GetFullPath(args[1]),true);
        if (args.FirstOrDefault() == "performance")
            return Performance.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "ArmadaPerformance-" + Guid.NewGuid().ToString("N")));
        if (args.FirstOrDefault() == "profile")
            return Performance.Profile(Path.GetFullPath(args[1]));
        if (args.FirstOrDefault() == "calibrate")
            return Performance.Calibrate();
        Test("indexed_chart_sampling_is_identical_with_duplicates_gaps_and_ties",()=>{
            var rng=new Random(42);long ticks=0;
            var points=Enumerable.Range(0,100003).Select(i=>new SeriesPoint(ticks+=i%1000==0?100000:rng.Next(0,4),0,1)).ToArray();
            var values=points.Select(p=>(long)rng.Next(-10,11)).ToArray();var index=new ChartRangeIndex(values);
            foreach(int start in new[]{0,1,255,256,257,50000})foreach(int width in new[]{1,10,333,1000,16000}){
                int count=values.Length-start;Assert(ChartSampling.SampleTimed(values,points,start,count,width).SequenceEqual(ChartSampling.SampleTimed(values,points,start,count,width,index)),"sampling must preserve exact extrema, endpoints, order and first-index ties");}
        });
        SlippageTests.Run();
        BacktestPeriodTests.Run(); Mt5ImportTests.Run(); PeriodTrustTests.Run(); DeleteDatasetTests.Run(); ChartDrawdownTests.Run(); ChartHeadingTests.Run();
        DrawdownTests.Run();
        ArchitectureTests.Run();
        ConvergenceTests.Run();
        WinRateConvergenceTests.Run();
        MetricEstimateTests.Run();
        Test("statistical_trust_score_clamps_and_distinguishes_unavailable", () => {
            Eq<decimal?>(87.5m,StatisticalTrustScore.Compute(12.5m));
            Eq<decimal?>(100m,StatisticalTrustScore.Compute(0m));
            Eq<decimal?>(0m,StatisticalTrustScore.Compute(100m));
            Eq<decimal?>(0m,StatisticalTrustScore.Compute(125m));
            Eq<decimal?>(100m,StatisticalTrustScore.Compute(-5m));
            Eq<decimal?>(null,StatisticalTrustScore.Compute(null));
            Eq<decimal?>(87.654321m,StatisticalTrustScore.Compute(12.345679m));
        });
        Test("cumulative_peak_and_zero", () => { var a = Analytics.Analyze([new(1, 100, 1), new(2, 100, 1), new(3, -50, 1), new(4, 0, 1)], 1000); Eq(50L, a.Drawdown); Eq(150L, a.Net); Eq(2, a.Wins); Eq(1, a.Losses); Eq(1, a.Zeros); Eq(2, a.WinStreak); Eq(4.0, a.ProfitFactor!.Value); });
        Test("trade_metric_run_averages_and_breakeven", () => {
            var a = Analytics.Analyze([new(1,10,1),new(2,20,1),new(3,-5,1),new(4,0,1),new(5,-15,1),new(6,30,1)],1000);
            Eq(2.0,a.PayoffRatio!.Value); Eq(2.0,a.RecoveryFactor!.Value);
            Assert(Math.Abs(a.SampleDeviation!.Value-Math.Sqrt(276.6666666666667))<1e-10,"sample deviation wrong");
            Eq(2,a.WinRuns); Eq(2,a.LossRuns); Eq(1.5,a.AverageWinStreak!.Value); Eq(1.0,a.AverageLossStreak!.Value);
            Eq(20m,a.AverageProfit!.Value); Eq(-10m,a.AverageLoss!.Value);
            Assert(Math.Abs(a.BreakEvenWinRate!.Value-100.0/3)<1e-12,"wrong break-even formula");
            var empty = Analytics.Analyze([],1000); Eq<double?>(null,empty.AverageWinStreak); Eq<double?>(null,empty.BreakEvenWinRate);
            var zero = Analytics.Analyze([new(1,0,1)],1000); Eq<decimal?>(null,zero.AverageProfit); Eq<decimal?>(null,zero.AverageLoss);
            Eq<double?>(null,Analytics.Analyze([new(1,10,1)],1000).BreakEvenWinRate);
        });
        Test("dukascopy_pip_units_and_direction", () => {
            Eq<decimal?>(0.01m,DukascopyPips.Size("EURJPY")); Eq<decimal?>(0.01m,DukascopyPips.Size("usd/jpy"));
            Eq<decimal?>(0.01m,DukascopyPips.Size("USDJPY_TDS_UTC"));
            Eq<decimal?>(0.1m,DukascopyPips.Size("XAUUSD_TDS_UTC"));
            Eq<decimal?>(null,DukascopyPips.Size("UNKNOWN_TDS_UTC"));
            Eq<decimal?>(0.0001m,DukascopyPips.Size("GBPUSD")); Eq<decimal?>(0.1m,DukascopyPips.Size("XAU/USD"));
            Eq(6m,DukascopyPips.Move(Amount.Parse("2000"),Amount.Parse("2000.6"),1,DukascopyPips.Size("XAUUSD")!.Value));
            Eq<decimal?>(null,DukascopyPips.Size("EURUSD.a")); Eq<decimal?>(null,DukascopyPips.Size("不明"));
            Eq(10m,DukascopyPips.Move(Amount.Parse("1.1000"),Amount.Parse("1.1010"),1,0.0001m));
            Eq(10m,DukascopyPips.Move(Amount.Parse("150"),Amount.Parse("149.90"),2,0.01m));
            Eq(-10m,DukascopyPips.Move(Amount.Parse("2000"),Amount.Parse("1999.90"),1,0.01m));
        });
        Test("pip_sums_promote_without_overflow_or_partial_totals",()=>{
            var sizes=new Dictionary<long,int>{{1,10000}};
            var a=PipStatistics.Compute([new(1,1,1,long.MaxValue),new(2,1,1,long.MaxValue)],sizes);
            Eq<decimal?>((decimal)long.MaxValue/Amount.Scale*20000m,a.Total);
            Eq(PipStatistics.Unavailable,PipStatistics.Compute([new(1,1,1,100),new(2,1,2,100)],sizes));
            Eq(PipStatistics.Unavailable,PipStatistics.Compute([new(1,1,1)],sizes));
            Eq<decimal?>(0m,PipStatistics.Compute([new(1,-1,1,-long.MaxValue),new(2,-1,1,-long.MaxValue),new(3,1,1,long.MaxValue),new(4,1,1,long.MaxValue)],sizes).Total);
            var mixed=PipStatistics.Compute([new(1,1,1,100000),new(2,-1,2,1000000)],new Dictionary<long,int>{{1,10000},{2,100}});
            var sparse=PipStatistics.Compute([new(1,1,1000000,100000),new(2,-1,9000000,1000000)],new Dictionary<long,int>{{1000000,10000},{9000000,100}});
            Eq(mixed,sparse);
            Eq<decimal?>(11m,mixed.Total); Eq<decimal?>(5.5m,mixed.Average); Eq<decimal?>(10m,mixed.Maximum); Eq<decimal?>(1m,mixed.Minimum);

        });
        Test("overflow_is_rejected", () => Throws<OverflowException>(() => Analytics.Analyze([new(1, long.MaxValue, 1), new(2, 1, 1)], 0)));
        Test("fixed_precision", () => { Eq(123456789L, Amount.Parse("1.23456789")); Throws<FormatException>(() => Amount.Parse("1.000000001")); Throws<OverflowException>(() => Amount.Parse("999999999999999999")); });
        Test("nonzero_precision_tail_never_rounds_away", () => Throws<FormatException>(() => Amount.Parse("1.00000000000000000000000000001")));
        Test("monthly_groups_preserve_net", () => { var g = Grouping.Compute([new(new DateTime(2020, 1, 1).Ticks, 100, 1), new(new DateTime(2020, 1, 2).Ticks, -20, 1), new(new DateTime(2020, 2, 1).Ticks, 0, 1)], 1); Eq(2, g.Length); Eq(80L, g[0].Profit); Eq(1, g[1].Zeros); });
        Test("sampling_extrema_and_bound", () => { long[] p = Enumerable.Range(0, 10000).Select(i => (long)(i % 137 == 0 ? -10000 : i)).ToArray(); var s = ChartSampling.Sample(p, 0, p.Length, 100); Assert(s.Length <= 202, "too many points"); Eq(0, s[0].Index); Eq(9999, s[^1].Index); for (int b = 0; b < 100; b++) { var bucket = s.Where(x => x.Index >= b * 100 && x.Index < (b + 1) * 100).Select(x => x.Value); Assert(bucket.Contains(p.AsSpan(b * 100, 100).ToArray().Min()), "missing trough"); Assert(bucket.Contains(p.AsSpan(b * 100, 100).ToArray().Max()), "missing peak"); } });
        Test("dashboard_bucket_drawdown_and_month_matrix", () => { long t = new DateTime(2020, 1, 1).Ticks; SeriesPoint[] points = [new(t, 100, 1), new(t + 1, -50, 1), new(t + 2, 75, 1), new(new DateTime(2021, 1, 1).Ticks, -20, 1), new(new DateTime(2021, 3, 1).Ticks, 30, 1)]; var buckets = DashboardAggregation.Compute(points, DashboardPeriod.Month); Eq(12, buckets.Length); Eq(105L, buckets[0].Profit); Eq(50L, buckets[0].Drawdown); Eq(175L, buckets[0].GrossProfit); Eq(70L, buckets[0].GrossLoss); var matrix = DashboardAggregation.Months(points); Eq(2, matrix.Length); Eq(125L, matrix[0].Profit[0]); Eq(3, matrix[0].Count[0]); Eq(10L, matrix[1].Total); Eq(0L, matrix[1].Profit[1]); });
        Test("dashboard_all_periods_preserve_total", () => { SeriesPoint[] p = [new(new DateTime(2020, 2, 29, 23, 59, 0).Ticks, 123, 1), new(new DateTime(2021, 1, 1).Ticks, -23, 2)]; foreach (var period in Enum.GetValues<DashboardPeriod>()) { var b = DashboardAggregation.Compute(p, period); Eq(100L, b.Sum(x => x.Profit)); Eq(2, b.Sum(x => x.Count)); } Throws<OverflowException>(() => DashboardAggregation.Compute([new(1, long.MaxValue, 1), new(2, 1, 1)], DashboardPeriod.Year)); });
        Test("chart_spacing_uses_elapsed_time_including_inactive_gaps", () => { long t = new DateTime(2020, 1, 1).Ticks; SeriesPoint[] p = [new(t, 1, 1), new(t + TimeSpan.TicksPerDay, 1, 1), new(t + 1000 * TimeSpan.TicksPerDay, 1, 2)]; Assert(Math.Abs(ChartSampling.TimeFraction(p, 0, 3, 1) - 0.001) < 1e-12, "gap collapsed into trade index spacing"); Eq(1.0, ChartSampling.TimeFraction(p, 0, 3, 2)); });
        Test("timed_sampling_preserves_balance_immediately_before_gap",()=>{long t=new DateTime(2020,1,1).Ticks;long[] values=[0,100,0,50,200];SeriesPoint[] times=[new(t,0,1),new(t+1,0,1),new(t+2,0,1),new(t+3,0,1),new(t+1000*TimeSpan.TicksPerDay,0,2)];var sample=ChartSampling.SampleTimed(values,times,0,5,2);Assert(sample.Any(p=>p.Index==3&&p.Value==50),"last settlement before gap lost");Assert(sample.Length<=10,"unbounded timestamp sampling");});
        if (args.FirstOrDefault() != "core")
            ImportTests.Run();
        if (args.FirstOrDefault() == "imports")
            RealReports.Run();
        if (args.FirstOrDefault() == "mc" || args.FirstOrDefault() == "all")
            MCTests.Run();
        if (args.FirstOrDefault() == "plugins" || args.FirstOrDefault() == "all")
            PluginTests.Run();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            passed,
            failures
        }));
        return failures == 0 ? 0 : 1;
    }
}
