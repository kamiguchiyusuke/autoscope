using ArmadaAnalyzer;

static class WinRateConvergenceTests
{
    static void Near(double expected,double? actual)=>Program.Assert(actual.HasValue && Math.Abs(expected-actual.Value)<1e-10,$"expected {expected}, actual {actual}");
    public static void Run()
    {
        Program.Test("win_rate_convergence_uses_conditional_mean_and_effective_sets",()=>{
            var r=WinRateConvergence.Compute(100,50,40);
            Near(43.69216870129681,r.LowerPercent);Near(56.30783129870319,r.UpperPercent);
            Program.Eq<string?>(null,r.Reason);
        });
        Program.Test("win_rate_convergence_preserves_raw_sets_and_clamps_probability",()=>{
            var r=WinRateConvergence.Compute(100,40,100/.6);
            double delta=.79788456*Math.Sqrt(.4*.6/(100/.6))*100;
            Near(40-delta,r.LowerPercent);Near(40+delta,r.UpperPercent);
            Program.Eq<double?>(0,WinRateConvergence.Compute(100,1,30).LowerPercent);
            Program.Eq<double?>(100,WinRateConvergence.Compute(100,99,30).UpperPercent);
        });
        Program.Test("win_rate_convergence_rejects_missing_small_and_boundary_inputs",()=>{
            foreach(var n in new double?[]{null,0,-1,29.9,double.NaN,double.PositiveInfinity})
            {
                var r=WinRateConvergence.Compute(100,50,n);
                Program.Eq<double?>(null,r.LowerPercent);Program.Eq<double?>(null,r.UpperPercent);
                Program.Assert(!string.IsNullOrWhiteSpace(r.Reason),"reason missing");
            }
            foreach(var pair in new[]{(0,0),(29,10),(100,0),(100,100),(100,101),(100,-1)})
                Program.Eq<double?>(null,WinRateConvergence.Compute(pair.Item1,pair.Item2,100).LowerPercent);
        });
        Program.Test("win_rate_convergence_is_independent_of_net_and_does_not_change_existing_width",()=>{
            var a=Analytics.Analyze(Enumerable.Range(0,100).Select(i=>new SeriesPoint(i,i%10<5?10:-15,1)).ToArray(),1000);
            var equity=(long[])a.Equity.Clone();var dd=(long[])a.Drawdowns.Clone();
            var original=ConvergenceWidth.Compute(a);
            Program.Eq<decimal?>(null,original.Percent);
            var r=WinRateConvergence.Compute(a.Count,a.Wins,original.EffectiveCount);
            Near(43.69216870129681,r.LowerPercent);
            Program.Assert(equity.SequenceEqual(a.Equity)&&dd.SequenceEqual(a.Drawdowns),"win-rate display altered existing analysis");
            Program.Eq(original,ConvergenceWidth.Compute(a));
        });
    }
}
