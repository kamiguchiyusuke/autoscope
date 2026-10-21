using ArmadaAnalyzer;

static class MetricEstimateTests
{
    static void Near(double expected,double? actual,double tolerance=1e-9)=>Program.Assert(actual is double x && Math.Abs(expected-x)<tolerance,$"expected {expected}, actual {actual}");
    static (SeriesPoint[],AnalysisResult) Data(int count=100)=>Build(Enumerable.Range(0,count).Select(i=>i%10<5?10L:-6L).ToArray());
    static (SeriesPoint[],AnalysisResult) Build(long[] values){var p=values.Select((x,i)=>new SeriesPoint(i,x,1)).ToArray();return(p,Analytics.Analyze(p,1000));}
    public static void Run()
    {
        Program.Test("bootstrap_exact_legacy_parity_across_blocks_seeds_scales",()=>{
            foreach(int count in new[]{97,1000,4000})foreach(int run in new[]{1,5,23})foreach(uint seed in new uint[]{0,42,1234567})
            {
                var rng=new Random(123);var p=Enumerable.Range(0,count).Select(i=>new SeriesPoint(i,(i/run%2==0?1:-1)*(long)rng.Next(1,500000000)*1234567,1)).ToArray();
                long capital=90000000000000000;var measured=Analytics.Analyze(p,capital);
                var options=new MetricBootstrapOptions(32,seed,2);
                var reference=ReferenceMetricEstimates.Run(p,measured,capital,options);
                var actual=MetricEstimates.Run(p,measured,capital,options);
                Program.Eq(reference.ProfitFactor,actual.ProfitFactor);Program.Eq(reference.DrawdownPercent,actual.DrawdownPercent);Program.Eq(reference.DrawdownAmount,actual.DrawdownAmount);Program.Eq(reference.EffectiveSets,actual.EffectiveSets);
            }
        });
        Program.Test("bootstrap_weights_preserve_raw_effective_sets_and_original_exposure",()=>{
            foreach(double m in new[]{30,30.5,40,100/.6,200})
            {
                var p=MetricEstimates.CreatePlan(100,m);
                double sum=(p.Blocks-1)*p.CommonWeight+p.LastWeight;
                double squares=(p.Blocks-1)*p.CommonWeight*p.CommonWeight+p.LastWeight*p.LastWeight;
                Near(m,sum);Near(m,sum*sum/squares);Near(100,sum*p.BlockLength*p.Scale);
                Program.Assert(p.CommonWeight>=0 && p.LastWeight>=0,"weights must remain nonnegative");
            }
        });
        Program.Test("simulated_dd_uses_actual_order_and_peak_balance_percentage",()=>{
            var a=MetricEstimates.EvaluatePath([10,-5,-5],100);
            var b=MetricEstimates.EvaluatePath([-5,10,-5],100);
            Near(1,a.ProfitFactor);Near(1,b.ProfitFactor);
            Near(10,a.Drawdown);Near(5,b.Drawdown);
            Near(1000.0/110,a.DrawdownPercent);Near(5,b.DrawdownPercent);
        });
        Program.Test("metric_conditional_means_use_median_sides_not_normal_coefficient",()=>{
            var r=MetricEstimates.ConditionalMeans([1,2,3,8]);Near(1.5,r.Lower);Near(5.5,r.Upper);Near(2.5,r.Center);
            r=MetricEstimates.ConditionalMeans([4,4,4,4]);Near(4,r.Lower);Near(4,r.Upper);
            Program.Assert(MetricEstimates.ConditionalMeans([1,double.NaN,3]).Reason!=null,"undefined trials must not be discarded");
            Program.Assert(MetricEstimates.ConditionalMeans([0,0,0,10]).Reason!=null,"missing conditional side must be explained");
        });
        Program.Test("bootstrap_is_deterministic_joint_and_does_not_mutate_source",()=>{
            var (p,a)=Data();var original=(SeriesPoint[])p.Clone();var equity=(long[])a.Equity.Clone();
            var one=MetricEstimates.Run(p,a,1000,new(128,42,1));
            var two=MetricEstimates.Run(p,a,1000,new(128,42,2));
            Program.Eq(one.ProfitFactor,two.ProfitFactor);Program.Eq(one.DrawdownPercent,two.DrawdownPercent);
            Program.Assert(one.ProfitFactor.Lower>0 && one.ProfitFactor.Upper>one.ProfitFactor.Lower,"joint PF samples missing");
            Program.Assert(one.DrawdownPercent.Upper>=one.DrawdownPercent.Lower,"DD samples missing");
            Program.Assert(p.SequenceEqual(original)&&a.Equity.SequenceEqual(equity),"source mutated");
            var negative=a with{Net=-200};var result=MetricEstimates.Run(p,negative,1000,new(128,42,1));
            Program.Eq(one.ProfitFactor,result.ProfitFactor);
        });
        Program.Test("bootstrap_no_loss_trials_are_unavailable_without_censoring_dd",()=>{
            var (p,a)=Build(Enumerable.Range(0,100).Select(i=>i==99?-1L:1L).ToArray());
            var r=MetricEstimates.Run(p,a,1000,new(256,42,1));
            Program.Eq<double?>(null,r.ProfitFactor.Lower);
            Program.Assert(r.ProfitFactor.Reason?.Contains("損失0")==true,"zero-loss PF reason missing");
            Program.Assert(r.DrawdownPercent.Lower.HasValue,"PF undefined must not discard DD distribution");
        });
        Program.Test("bootstrap_validates_resources_missing_data_and_cancellation",()=>{
            var (p,a)=Data();using var cts=new CancellationTokenSource();cts.Cancel();
            Program.Throws<OperationCanceledException>(()=>MetricEstimates.Run(p,a,1000,new(),cts.Token));
            Program.Assert(MetricEstimates.Run(p,a,0,new()).DrawdownPercent.Reason!=null,"invalid capital");
            Program.Assert(MetricEstimates.Run(p,a with{WinRuns=0},1000,new()).ProfitFactor.Reason!=null,"missing runs");
            Program.Assert(MetricEstimates.Run(p,a,1000,new(0)).ProfitFactor.Reason!=null,"invalid trials");
            var (large,summary)=Data(250000);
            Program.Assert(MetricEstimates.Run(large,summary,1000,new(10000)).ProfitFactor.Reason?.Contains("作業予算")==true,"work cap not enforced");
        });
    }
}
