using ArmadaAnalyzer;
static class PeriodTrustTests
{
    public static void Run()
    {
        Program.Test("period_score_anchors_and_monotonicity",()=>{
            var start=new DateTime(2000,1,1);
            Program.Eq<double?>(0,PeriodTrustScore.Compute(start,start));
            Program.Assert(Math.Abs(PeriodTrustScore.Compute(start,start.AddDays(5*365.2425))!.Value-70)<1e-8,"5 years");
            Program.Assert(Math.Abs(PeriodTrustScore.Compute(start,start.AddDays(10*365.2425))!.Value-90)<1e-8,"10 years");
            double prior=0;
            for(int years=1;years<=200;years++){double score=PeriodTrustScore.Compute(start,start.AddDays(years*365.2425))!.Value;Program.Assert(score>=prior && score<=100,"bounded monotonic");prior=score;}
        });
        Program.Test("period_score_missing_and_reversed_dates",()=>{
            Program.Eq<double?>(null,PeriodTrustScore.Compute(null,DateTime.Today));
            Program.Eq<double?>(null,PeriodTrustScore.Compute(DateTime.Today,null));
            Program.Eq<double?>(null,PeriodTrustScore.Compute(DateTime.Today,DateTime.Today.AddDays(-1)));
        });
    }
}
