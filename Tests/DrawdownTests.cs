using ArmadaAnalyzer;
static class DrawdownTests
{
    static long Day(int n)=>new DateTime(2020,1,1).AddDays(n).Ticks;
    static Snapshot Data(params int[] values)=>new("dd",values.Select((v,i)=>new SeriesPoint(Day(i),v*Amount.Scale,1,v*10000L)).ToArray(),false){PipFactors=new Dictionary<long,int>{{1,10000}}};
    public static void Run()
    {
        Program.Test("drawdown_default_amount_and_relative",()=>{
            var options=new DrawdownOptions();Program.Eq(250000L*Amount.Scale,options.AmountThreshold);Program.Eq(10m,options.RelativePercent);
        });
        Program.Test("drawdown_episode_threshold_and_recovery",()=>{
            // Equity100,60,40,70,100,80,110,60: 60-deep recovery over4d,20-deep over2d,50-deep open.
            var a=Analytics.Analyze(Data(100,-40,-20,30,30,-20,30,-50),1000*Amount.Scale,new DrawdownOptions(50*Amount.Scale,50,5));
            var m=a.Recovery!.Money; Program.Eq(2,m.Count);Program.Eq(1,m.Recovered);Program.Eq(1,m.TimedRecovered);
            Program.Eq<decimal?>(4*TimeSpan.TicksPerDay,m.AverageTicks);Program.Eq<long?>(Day(6),m.OngoingStart);Program.Eq<long?>(TimeSpan.TicksPerDay,m.OngoingTicks);
            Program.Eq(60m,a.Pips.Recovery!.Maximum);Program.Eq(2,a.Pips.Recovery.Count);Program.Eq(1,a.Recovery.Relative!.Count);
            Program.Eq(2,a.Recovery.Initial5!.Count);Program.Eq(0,a.Recovery.Initial10!.Count);
        });
        Program.Test("drawdown_first_loss_and_equal_high",()=>{
            var a=Analytics.Analyze(Data(-10,10,0,-10,10),100*Amount.Scale,new DrawdownOptions(10*Amount.Scale,10,10));
            var d=a.Recovery!.Money;Program.Eq(2,d.Count);Program.Eq(2,d.Recovered);Program.Eq(1,d.TimedRecovered);
            Program.Eq<decimal?>(2*TimeSpan.TicksPerDay,d.AverageTicks);Program.Eq<long?>(null,d.OngoingStart);
            var open=Analytics.Analyze(Data(-10,-10),100*Amount.Scale,new DrawdownOptions(10*Amount.Scale,10,10)).Recovery!.Money;
            Program.Eq(1,open.Count);Program.Eq(true,open.IsOngoing);Program.Eq<long?>(null,open.OngoingTicks);
        });
        Program.Test("drawdown_zero_empty_threshold_missing_pips",()=>{
            var a=Analytics.Analyze(Data(0,0,1),100,new DrawdownOptions(1,1,1));Program.Eq(0,a.Recovery!.Money.Count);Program.Eq<decimal?>(null,a.Recovery.Money.AverageTicks);
            Program.Eq<DrawdownSummary?>(null,Analytics.Analyze(new Snapshot("x",[new(Day(0),-1,1)],false),100).Pips.Recovery);
            Program.Throws<ArgumentOutOfRangeException>(()=>Analytics.Analyze(Data(1),100,new DrawdownOptions(0,1,1)));
            Program.Throws<ArgumentOutOfRangeException>(()=>Analytics.Analyze(Data(1),100,new DrawdownOptions(1,-1,1)));
            Program.Throws<ArgumentOutOfRangeException>(()=>Analytics.Analyze(Data(1),100,new DrawdownOptions(1,1,101)));
            var empty=Analytics.Analyze(Data(),100);Program.Eq(0,empty.Recovery!.Money.Count);
        });
        Program.Test("drawdown_mixed_units_timestamp_order_cancel",()=>{
            var points=new[]{new SeriesPoint(Day(0),10,1,10000),new SeriesPoint(Day(0),-5,2,-500000),new SeriesPoint(Day(1000),5,1,5000)};
            var s=new Snapshot("mixed",points,false){PipFactors=new Dictionary<long,int>{{1,10000},{2,100}}};
            var a=Analytics.Analyze(s,100,new DrawdownOptions(5,0.5m,1)); Program.Eq(0.5m,a.Pips.Recovery!.Maximum);Program.Eq<decimal?>(1000m*TimeSpan.TicksPerDay,a.Pips.Recovery.AverageTicks);
            Program.Throws<ArgumentException>(()=>Analytics.Analyze(s with{Points=points.Reverse().ToArray()},100));
            using var stop=new CancellationTokenSource();stop.Cancel();Program.Throws<OperationCanceledException>(()=>Analytics.Analyze(s,100,token:stop.Token));
        });
        Program.Test("drawdown_random_decimal_reference",()=>{
            var random=new Random(8492);
            for(int run=0;run<80;run++)
            {
                var values=Enumerable.Range(0,200).Select(_=>random.Next(-30,31)).ToArray();
                var result=Analytics.Analyze(Data(values),1000*Amount.Scale,new DrawdownOptions(20*Amount.Scale,20,20));
                decimal sum=0,peak=0,max=0;int count=0,recovered=0;var times=new List<long>();int? at=null;int i=0;
                while(i<values.Length)
                {
                    sum+=values[i];
                    if(sum>=peak){peak=sum;at=i;i++;continue;}
                    decimal deepest=peak-sum;int start=i;
                    while(sum<peak && ++i<values.Length){sum+=values[i];deepest=Math.Max(deepest,peak-sum);}
                    max=Math.Max(max,deepest);
                    if(deepest>=20){count++;if(i<values.Length){recovered++;if(at.HasValue)times.Add((i-at.Value)*TimeSpan.TicksPerDay);}}
                    if(i<values.Length){peak=sum;at=i;i++;}
                }
                var d=result.Pips.Recovery!;Program.Eq(max,d.Maximum);Program.Eq(count,d.Count);Program.Eq(recovered,d.Recovered);Program.Eq(times.Count,d.TimedRecovered);
                Program.Eq<decimal?>(times.Count==0?null:times.Sum(v=>(decimal)v)/times.Count,d.AverageTicks);
                Program.Eq(result.Recovery!.Money,d);
            }
        });
        Program.Test("pip_drawdown_wide_accumulator",()=>{
            var s=new Snapshot("wide",[new(1,0,1,long.MaxValue),new(2,0,1,-long.MaxValue),new(3,0,1,-long.MaxValue),new(4,0,1,long.MaxValue)],false){PipFactors=new Dictionary<long,int>{{1,10000}}};
            var d=Analytics.Analyze(s,100).Pips.Recovery!;Program.Eq(2m*long.MaxValue/10000m,d.Maximum);Program.Eq(1,d.Count);Program.Eq(true,d.IsOngoing);
        });
        Program.Test("pip_drawdown_depth_overflow_keeps_promoted_state",()=>{
            var s=new Snapshot("wide-depth",[new(1,0,1,long.MaxValue),new(2,0,1,-long.MaxValue),new(3,0,1,-1),new(4,0,1,-5),new(5,0,1,6),new(6,0,1,long.MaxValue)],false){PipFactors=new Dictionary<long,int>{{1,1}}};
            var d=Analytics.Analyze(s,100).Pips.Recovery!;Program.Eq(((decimal)long.MaxValue+6)/Amount.Scale,d.Maximum);Program.Eq(1,d.Recovered);Program.Eq<decimal?>(5,d.AverageTicks);Program.Eq(false,d.IsOngoing);
        });
    }
}
