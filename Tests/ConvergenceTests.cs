using ArmadaAnalyzer;

static class ConvergenceTests
{
    static AnalysisResult Runs(int cycles=10,int run=5,int win=10,int loss=-6) => Analytics.Analyze(
        Enumerable.Range(0,cycles*run*2).Select(i=>new SeriesPoint(i,i%(run*2)<run?win:loss,1)).ToArray(),1000000);
    static void Near(double want,decimal? actual)=>Program.Assert(actual.HasValue && Math.Abs(want-(double)actual.Value)<1e-9,$"expected {want}, actual {actual}");
    public static void Run()
    {
        Program.Test("convergence_uses_original_count_and_conditional_mean_variation",()=>{
            var result=ConvergenceWidth.Compute(Runs());
            Program.Eq(2.5,result.Multiplier!.Value);Program.Eq(40.0,result.EffectiveCount!.Value);
            Near(200+100*.79788456*Math.Sqrt(160.0/99),result.UpperTotal);
            Near(50*.79788456*Math.Sqrt(160.0/99),result.Percent);
            Program.Eq<string?>(null,result.Reason);
        });
        Program.Test("convergence_uses_larger_factor_without_floor_and_rounds_only_display",()=>{
            var a=Analytics.Analyze(Enumerable.Range(0,100).Select(i=>new SeriesPoint(i,i%5 is 0 or 3?10:i%5==2?0:-6,1)).ToArray(),1000000);
            var r=ConvergenceWidth.Compute(a);
            Program.Assert(Math.Abs(.6-r.Multiplier!.Value)<1e-12,"must select larger factor, without floor at one");
            Program.Assert(Math.Abs(100/.6-r.EffectiveCount!.Value)<1e-10,"must retain unrounded count for standard error");
            Program.Eq<long?>(167,r.TotalSets);
            double se=Math.Sqrt((40*Math.Pow(10-1.6,2)+40*Math.Pow(-6-1.6,2)+20*Math.Pow(1.6,2))/99)/Math.Sqrt(100/.6);
            Near(160+100*.79788456*se,r.UpperTotal);
            Near(.79788456*se/160*10000,r.Percent);
        });
        Program.Test("total_sets_selects_either_larger_factor_and_is_independent_of_net_or_width_guards",()=>{
            var a=Runs();
            foreach(var item in new[]{a with{WinRuns=20},a with{LossRuns=20}})
            {
                var r=ConvergenceWidth.Compute(item);
                Program.Eq<double?>(2.5,r.Multiplier);Program.Eq<double?>(40,r.EffectiveCount);Program.Eq<long?>(40,r.TotalSets);
                Near(50*.79788456*Math.Sqrt(160.0/99),r.Percent);
            }
            foreach(var net in new[]{0L,-200L})
            {
                var r=ConvergenceWidth.Compute(a with{Net=net});
                Program.Eq<long?>(40,r.TotalSets);Program.Eq<decimal?>(null,r.Percent);
            }
            var small=ConvergenceWidth.Compute(Runs(1));
            Program.Eq<long?>(4,small.TotalSets);Program.Eq<decimal?>(null,small.Percent);
            var half=ConvergenceWidth.Compute(a with{Count=6,Wins=2,Losses=4,Zeros=0,WinRuns=2,LossRuns=1});
            Program.Eq<double?>(4.5,half.EffectiveCount);Program.Eq<long?>(5,half.TotalSets);
            var thirds=Analytics.Analyze([new(1,10,1),new(2,-1,1),new(3,-1,1)],0);
            Program.Eq<long?>(5,ConvergenceWidth.Compute(thirds).TotalSets);
            Program.Eq<long?>(null,ConvergenceWidth.Compute(a with{WinRuns=0}).TotalSets);
            Program.Eq<long?>(null,ConvergenceWidth.Compute(Analytics.Analyze([],0)).TotalSets);
        });
        Program.Test("convergence_unavailable_for_nonpositive_net_missing_runs_or_insufficient_data",()=>{
            foreach(var a in new[]{Runs(win:6,loss:-6),Runs(win:6,loss:-10),Runs(1),Runs(2,50),
                Analytics.Analyze([],1000000),Analytics.Analyze(Enumerable.Range(0,100).Select(i=>new SeriesPoint(i,1,1)).ToArray(),1000000),
                Runs() with{WinRuns=0},Runs() with{SampleDeviation=double.NaN},Runs() with{SampleDeviation=double.PositiveInfinity}})
            {
                var r=ConvergenceWidth.Compute(a);
                Program.Eq<decimal?>(null,r.Percent);Program.Assert(!string.IsNullOrWhiteSpace(r.Reason),"unavailable reason missing");
            }
        });
        Program.Test("convergence_is_read_only_and_scenario_engine_is_withdrawn",()=>{
            var a=Runs();var equity=(long[])a.Equity.Clone();var dd=(long[])a.Drawdowns.Clone();
            for(int i=0;i<100;i++) ConvergenceWidth.Compute(a);
            Program.Eq(200L,a.Net);Program.Assert(equity.SequenceEqual(a.Equity)&&dd.SequenceEqual(a.Drawdowns),"display indicator changed curve");
            Program.Eq<Type?>(null,typeof(Analytics).Assembly.GetType("ArmadaAnalyzer.BlockPnlScenarioGenerator"));
            Program.Eq<Type?>(null,typeof(Analytics).Assembly.GetType("ArmadaAnalyzer.NormalExpectationEstimator"));
        });
    }
}
