namespace ArmadaAnalyzer;

public sealed record DrawdownOptions(long AmountThreshold = 250000L*Amount.Scale, decimal PipThreshold = 100m, decimal RelativePercent = 10m)
{
    internal void Validate()
    {
        if(AmountThreshold<=0 || PipThreshold<=0 || PipThreshold>1000000000m || decimal.Round(PipThreshold,8)!=PipThreshold || RelativePercent<=0 || RelativePercent>100 || decimal.Round(RelativePercent,8)!=RelativePercent)
            throw new ArgumentOutOfRangeException(nameof(DrawdownOptions),"DD基準は正の値（pipsは10億以下、相対DDは100%以下、小数8桁まで）で指定してください。");
    }
}

public sealed record DrawdownSummary(decimal Maximum,int Count,int Recovered,int TimedRecovered,decimal? AverageTicks,long? MinimumTicks,long? MaximumTicks,bool IsOngoing,long? OngoingStart,long? OngoingTicks);
public sealed record DrawdownStatistics(DrawdownSummary Money,DrawdownSummary? Relative,DrawdownSummary? Initial5,DrawdownSummary? Initial10,DrawdownSummary? Initial15);

/// <summary>Constant memory. Only completed episodes enter duration statistics; no invented initial timestamp.</summary>
internal sealed class DrawdownTracker(Int128 threshold,long initial = 0,long relativePercentScaled = 0)
{
    private struct Totals
    {
        public int Count,Recovered,Timed;
        public Int128 Duration;
        public long Min,Max;
        public bool Open;
        public long? Start,Elapsed;
        public void Include(long peakTime,long end,bool completed)
        {
            Count++;
            if(!completed){Open=true;Start=peakTime<0?null:peakTime;Elapsed=peakTime<0?null:end-peakTime;return;}
            Recovered++;
            if(peakTime<0)return;
            long ticks=end-peakTime;Duration+=ticks;
            if(Timed++==0)Min=ticks;else Min=Math.Min(Min,ticks);
            Max=Math.Max(Max,ticks);
        }
        public readonly DrawdownSummary Result(decimal maximum)=>new(maximum,Count,Recovered,Timed,Timed==0?null:(decimal)Duration/Timed,Timed==0?null:Min,Timed==0?null:Max,Open,Start,Elapsed);
    }
    private Int128 peak,depth,maximum;
    private long smallPeak,smallDepth,smallMaximum;
    private bool wide;
    private readonly long fixed5=(long)(((Int128)initial*5+99)/100),fixed10=(long)(((Int128)initial*10+99)/100),fixed15=(long)(((Int128)initial*15+99)/100);
    private long peakTime=-1,lastTime=-1;
    private Totals main,relative,five,ten,fifteen;
    private double maxRelative;
    public void Observe(long cumulative,long time)
    {
        if(wide){ObserveWide(cumulative,time);return;}
        if(time<lastTime)throw new ArgumentException("取引は決済時刻順である必要があります。");
        lastTime=time;
        if(cumulative>=smallPeak)
        {
            if(smallDepth>0)CloseSmall(time,true);
            smallPeak=cumulative;peakTime=time;smallDepth=0;
        }
        else
        {
            // Money already rejects DD overflow; pips can cross the signed64 range and promotes exactly.
            long d=unchecked(smallPeak-cumulative);
            if(d<0){ObserveWide(cumulative,time);return;}
            if(d>smallDepth)smallDepth=d;
            if(d>smallMaximum)smallMaximum=d;
        }
    }
    public void ObserveWide(Int128 cumulative,long time)
    {
        if(!wide){wide=true;peak=smallPeak;depth=smallDepth;maximum=smallMaximum;}
        if(time<lastTime)throw new ArgumentException("取引は決済時刻順である必要があります。");
        lastTime=time;
        if(cumulative>=peak)
        {
            if(depth>0)Close(time,true);
            peak=cumulative;peakTime=time;depth=0;
        }
        else
        {
            Int128 d=peak-cumulative;
            if(d>depth)depth=d;
            if(d>maximum)maximum=d;
        }
    }
    private void CloseSmall(long end,bool completed)
    {
        if(smallDepth>=threshold)main.Include(peakTime,end,completed);
        if(initial<=0)return;
        Int128 denominator=(Int128)initial+smallPeak;
        maxRelative=Math.Max(maxRelative,100.0*smallDepth/(double)denominator);
        if((Int128)smallDepth*100*Amount.Scale>=denominator*relativePercentScaled)relative.Include(peakTime,end,completed);
        if(smallDepth>=fixed5)five.Include(peakTime,end,completed);
        if(smallDepth>=fixed10)ten.Include(peakTime,end,completed);
        if(smallDepth>=fixed15)fifteen.Include(peakTime,end,completed);
    }
    private void Close(long end,bool completed)
    {
        if(depth>=threshold)main.Include(peakTime,end,completed);
        if(initial<=0)return;
        Int128 denominator=initial+peak;
        maxRelative=Math.Max(maxRelative,100.0*(double)depth/(double)denominator);
        if(depth*100*Amount.Scale>=denominator*relativePercentScaled)relative.Include(peakTime,end,completed);
        if(depth*100>=initial*(Int128)5)five.Include(peakTime,end,completed);
        if(depth*100>=initial*(Int128)10)ten.Include(peakTime,end,completed);
        if(depth*100>=initial*(Int128)15)fifteen.Include(peakTime,end,completed);
    }
    internal static decimal Unscale(Int128 value)=>(decimal)(value/Amount.Scale)+(decimal)(value%Amount.Scale)/Amount.Scale;
    // Finish once after the ordered scan.
    public DrawdownStatistics Finish()
    {
        if(!wide){if(smallDepth>0)CloseSmall(lastTime,false);maximum=smallMaximum;}
        else
        if(depth>0)Close(lastTime,false);
        decimal max=Unscale(maximum);
        return new(main.Result(max),initial<=0?null:relative.Result((decimal)maxRelative),initial<=0?null:five.Result((decimal)maximum*100/initial),initial<=0?null:ten.Result((decimal)maximum*100/initial),initial<=0?null:fifteen.Result((decimal)maximum*100/initial));
    }
}
