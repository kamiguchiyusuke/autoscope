using System.Runtime.CompilerServices;
namespace ArmadaAnalyzer;

public sealed record PipStatistics(decimal? Total, decimal? Average, decimal? Maximum, decimal? Minimum, decimal? WinningAverage, decimal? LosingAverage)
{
    public DrawdownSummary? Recovery { get; init; }
    public static readonly PipStatistics Unavailable = new(null,null,null,null,null,null);
    private static decimal Unscale(Int128 value) => (decimal)(value/Amount.Scale) + (decimal)(value%Amount.Scale)/Amount.Scale;
    // Keep the normal path in64-bit arithmetic; promote on overflow without losing precision.
    private struct WideSum
    {
        private long small; private Int128 large; private bool wide;
        public readonly Int128 Value => wide ? large : small;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(long value)
        {
            if(wide) { large+=value; return; }
            long sum=unchecked(small+value);
            if(((small^sum)&(value^sum))<0) { large=(Int128)small+value; wide=true; }
            else small=sum;
        }
    }
    private sealed class Bucket(int factor)
    {
        public readonly int Factor=factor;
        public readonly long MinRaw=long.MinValue/factor,MaxRaw=long.MaxValue/factor;
        public WideSum Sum,Win,Loss;
        public long Min=long.MaxValue,Max=long.MinValue;
        public int Count,Wins,Losses;
    }
    private sealed class PipDrawdown(decimal threshold)
    {
        private long sum;
        private Int128 large;
        private bool wide;
        private readonly DrawdownTracker tracker=new((Int128)(threshold*Amount.Scale));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(long move,Bucket bucket,long time)
        {
            if(!wide && move>=bucket.MinRaw && move<=bucket.MaxRaw)
            {
                long value=unchecked(move*bucket.Factor),next=unchecked(sum+value);
                if(((sum^next)&(value^next))>=0){sum=next;tracker.Observe(sum,time);return;}
            }
            if(!wide){wide=true;large=sum;}
            large+=(Int128)move*bucket.Factor;tracker.ObserveWide(large,time);
        }
        public DrawdownSummary Finish()=>tracker.Finish().Money;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(Bucket b,in SeriesPoint point)
    {
        long move=point.PriceMove;
        b.Count++; b.Sum.Add(move); b.Min=Math.Min(b.Min,move); b.Max=Math.Max(b.Max,move);
        if(point.Profit>0) { b.Wins++; b.Win.Add(move); } else if(point.Profit<0) { b.Losses++; b.Loss.Add(move); }
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static PipStatistics Compute(SeriesPoint[] points,IReadOnlyDictionary<long,int>? factors,decimal threshold = 100m,CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if(threshold<=0 || threshold>1000000000m || decimal.Round(threshold,8)!=threshold)throw new ArgumentOutOfRangeException(nameof(threshold));
        if(points.Length==0 || factors==null) return Unavailable;
        var recovery=new PipDrawdown(threshold);int index=0;
        var buckets=factors.Where(x=>x.Value>0).ToDictionary(x=>x.Key,x=>new Bucket(x.Value));
        if(buckets.Count==1)
        {
            var only=buckets.First();
            foreach(ref readonly var point in points.AsSpan())
            {
                if((index++&4095)==0)token.ThrowIfCancellationRequested();
                if(point.PriceMove==long.MinValue || point.DatasetId!=only.Key) return Unavailable;
                Add(only.Value,in point);
                recovery.Add(point.PriceMove,only.Value,point.Ticks);
            }
        }
        else if(buckets.Count>0 && buckets.Keys.All(id=>id>0 && id<=4096))
        {
            // Bounded lookup table for ordinary workspace IDs; sparse/large IDs use the dictionary below.
            var table=new Bucket?[checked((int)buckets.Keys.Max()+1)];
            foreach(var pair in buckets) table[(int)pair.Key]=pair.Value;
            foreach(ref readonly var point in points.AsSpan())
            {
                if((index++&4095)==0)token.ThrowIfCancellationRequested();
                long id=point.DatasetId;
                if(point.PriceMove==long.MinValue || id<=0 || id>=table.Length || table[(int)id] is not Bucket b) return Unavailable;
                Add(b,in point);
                recovery.Add(point.PriceMove,b,point.Ticks);
            }
        }
        else foreach(ref readonly var point in points.AsSpan())
        {
            if((index++&4095)==0)token.ThrowIfCancellationRequested();
            if(point.PriceMove==long.MinValue || !buckets.TryGetValue(point.DatasetId,out var b)) return Unavailable;
            Add(b,in point);
            recovery.Add(point.PriceMove,b,point.Ticks);
        }
        Int128 total=0,win=0,loss=0,min=Int128.MaxValue,max=Int128.MinValue; int wins=0,losses=0;
        foreach(var b in buckets.Values)
        {
            if(b.Count==0) continue;
            total+=b.Sum.Value*b.Factor; win+=b.Win.Value*b.Factor; loss+=b.Loss.Value*b.Factor;
            min=Int128.Min(min,(Int128)b.Min*b.Factor); max=Int128.Max(max,(Int128)b.Max*b.Factor);
            wins+=b.Wins; losses+=b.Losses;
        }
        decimal sum=Unscale(total);
        return new(sum,sum/points.Length,Unscale(max),Unscale(min),wins==0?null:Unscale(win)/wins,losses==0?null:Unscale(loss)/losses){Recovery=recovery.Finish()};
    }
}
