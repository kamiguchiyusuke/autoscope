using ArmadaAnalyzer;
static class ChartDrawdownTests
{
    public static void Run()
    {
        Program.Test("chart_drawdowns_top_three_distinct_episodes",()=>{
            long[] cumulative=[100,80,60,100,150,100,80,150,180,170,180,200,100,120];
            long prev=0;var points=cumulative.Select((v,i)=>{var p=new SeriesPoint(new DateTime(2020,1,1).AddDays(i).Ticks,(v-prev)*Amount.Scale,1);prev=v;return p;}).ToArray();
            var a=Analytics.Analyze(points,1000*Amount.Scale);var b=a.ChartDrawdowns;
            Program.Eq(3,b.Length);Program.Eq(100*Amount.Scale,b[0].Depth);Program.Assert(!b[0].Recovered,"ongoing largest");
            Program.Eq(70*Amount.Scale,b[1].Depth);Program.Eq(4,b[1].Start);Program.Eq(7,b[1].End);Program.Eq(6,b[1].Trough);
            Program.Eq(40*Amount.Scale,b[2].Depth);Program.Eq(0,b[2].Start);Program.Eq(3,b[2].End);
            Program.Eq(7m,b[1].InitialPercent);Program.Assert(Math.Abs(b[1].BalancePercent!.Value-70m/1150*100)<0.00001m,"peak denominator");
        });
        Program.Test("chart_initial_loss_has_unknown_peak_date_and_no_fake_recovery",()=>{
            var a=Analytics.Analyze([new(1,-10*Amount.Scale,1),new(2,10*Amount.Scale,1)],100*Amount.Scale);
            Program.Eq(-1,a.ChartDrawdowns[0].Start);Program.Assert(a.ChartDrawdowns[0].Recovered,"recovered");
            Program.Eq(0,Analytics.Analyze([new(1,10,1),new(2,20,1)],100).ChartDrawdowns.Length);
        });
    }
}
