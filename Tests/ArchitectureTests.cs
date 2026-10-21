using ArmadaAnalyzer;
static class ArchitectureTests
{
    public static void Run()
    {
        Program.Test("calendar_fast_path_matches_unsorted_reference",()=>{
            var rng=new Random(529);var points=Enumerable.Range(0,1200).Select(i=>new SeriesPoint(new DateTime(1998,1,1).AddDays(rng.Next(0,14000)).AddMinutes(rng.Next(0,1440)).Ticks,rng.Next(-100,200),1)).ToArray();
            foreach(var ordered in new[]{points,points.OrderBy(p=>p.Ticks).ToArray()})
            {
                for(int mode=0;mode<4;mode++)
                {
                    int Key(SeriesPoint p){var t=new DateTime(p.Ticks);return mode switch{0=>t.Year,1=>t.Year*100+t.Month,2=>(int)t.DayOfWeek,_=>t.Hour};}
                    var expected=ordered.GroupBy(Key).OrderBy(g=>g.Key).ToArray();var actual=Grouping.Compute(ordered,mode);Program.Eq(expected.Length,actual.Length);
                    for(int i=0;i<actual.Length;i++){Program.Eq(expected[i].Sum(p=>p.Profit),actual[i].Profit);Program.Eq(expected[i].Count(p=>p.Profit>0),actual[i].Wins);Program.Eq(expected[i].Count(),actual[i].Count);}
                }
                foreach(var period in Enum.GetValues<DashboardPeriod>())
                {
                    int Key(SeriesPoint p){var d=new DateTime(p.Ticks);return period switch{DashboardPeriod.Year=>d.Year,DashboardPeriod.Month=>d.Month,DashboardPeriod.Day=>d.Day,DashboardPeriod.Weekday=>(int)d.DayOfWeek,DashboardPeriod.Hour=>d.Hour,_=>d.Minute};}
                    var actual=DashboardAggregation.Compute(ordered,period);
                    foreach(var g in ordered.GroupBy(Key))
                    {
                        string label=period==DashboardPeriod.Weekday?new[]{"日","月","火","水","木","金","土"}[g.Key]:g.Key.ToString();
                        var row=actual.Single(x=>x.Label==label);long balance=0,peak=0,max=0;foreach(var p in g){balance+=p.Profit;peak=Math.Max(peak,balance);max=Math.Max(max,peak-balance);}
                        Program.Eq(balance,row.Profit);Program.Eq(max,row.Drawdown);
                    }
                }
                foreach(var year in DashboardAggregation.Months(ordered))for(int m=1;m<=12;m++)Program.Eq(ordered.Where(p=>new DateTime(p.Ticks).Year==year.Year && new DateTime(p.Ticks).Month==m).Sum(p=>p.Profit),year.Profit[m-1]);
            }
        });
        Program.Test("all_background_aggregates_cancel_and_correlation_bounds",()=>{
            using var c=new CancellationTokenSource();c.Cancel();SeriesPoint[] p=[new(1,1,1),new(2,2,2)];
            Program.Throws<OperationCanceledException>(()=>Grouping.Compute(p,1,c.Token));Program.Throws<OperationCanceledException>(()=>DashboardAggregation.Compute(p,DashboardPeriod.Year,c.Token));Program.Throws<OperationCanceledException>(()=>DashboardAggregation.Months(p,c.Token));Program.Throws<OperationCanceledException>(()=>Portfolio.Correlations(p,c.Token));
            Program.Throws<InvalidOperationException>(()=>Portfolio.Correlations(Enumerable.Range(1,33).Select(i=>new SeriesPoint(i,1,i)).ToArray()));
            Program.Throws<InvalidOperationException>(()=>Portfolio.Correlations([new(new DateTime(1900,1,1).Ticks,1,1),new(new DateTime(2020,1,1).Ticks,1,2)]));
        });
        Program.Test("workspace_outside_program_with_legacy_precedence",()=>{
            string root=Path.Combine(Path.GetTempPath(),"ArmadaPathTest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string app=Path.Combine(root,"program"),data=Path.Combine(root,"userdata");
            Program.Eq(Path.Combine(data,"ArmadaAnalyzer","Workspaces","Default"),WorkspaceLocation.ResolveDefault(app,data));
            string old=Path.Combine(app,"Workspaces","Default");Directory.CreateDirectory(old);File.WriteAllText(Path.Combine(old,"keep.txt"),"user data");
            Program.Eq(old,WorkspaceLocation.ResolveDefault(app,data));Program.Eq("user data",File.ReadAllText(Path.Combine(old,"keep.txt")));
        });
    }
}
