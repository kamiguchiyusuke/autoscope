using ArmadaAnalyzer;
using System.Diagnostics;
using System.Runtime.CompilerServices;
internal static class ReferenceMetricEstimates
{
    public const long MaximumVisits=2_000_000_000;
    private static MetricEstimateResult Unavailable(string reason)=>new(new(null,null,null,reason),new(null,null,null,reason),new(null,null,null,reason),0,0,null,0);

    public static MetricBootstrapPlan CreatePlan(int count,double effectiveSets)
    {
        if(count<1 || !double.IsFinite(effectiveSets) || effectiveSets<1 || effectiveSets>int.MaxValue)
            throw new ArgumentException("補正セット数が範囲外です。");
        int blocks=(int)Math.Ceiling(effectiveSets);
        int length=(int)Math.Clamp(Math.Round(count/effectiveSets,MidpointRounding.AwayFromZero),1,count);
        double common=1,last=1;
        if(blocks>1 && effectiveSets!=blocks)
        {
            double k=blocks,m=effectiveSets;
            common=m/k+Math.Sqrt(m*(k-m)/(k*k*(k-1)));
            last=Math.Max(0,m-(k-1)*common);
        }
        return new(length,blocks,effectiveSets,common,last,count/(effectiveSets*length),(long)blocks*length);
    }

    public static ConditionalMetricRange ConditionalMeans(double[] values,string undefinedReason="非有限の試行値があるため算出できません。")
    {
        if(values.Length==0 || values.Any(x=>!double.IsFinite(x))) return new(null,null,null,undefinedReason);
        Array.Sort(values);
        int half=values.Length/2;
        double center=values.Length%2==0?values[half-1]/2+values[half]/2:values[half];
        if(values[0]==values[^1]) return new(center,center,center,null);
        double lower=0,upper=0;int lowCount=0,highCount=0;
        foreach(double x in values)
        {
            if(x<center) lower+=(x-lower)/++lowCount;
            else if(x>center) upper+=(x-upper)/++highCount;
        }
        if(lowCount==0 || highCount==0) return new(null,null,center,"中央値の片側に試行値が不足し、条件付き平均を算出できません。");
        return new(lower,upper,center,null);
    }

    public static (double? ProfitFactor,double Drawdown,double DrawdownPercent) EvaluatePath(ReadOnlySpan<long> pnl,long initial)
    {
        if(initial<=0)throw new ArgumentException("初期資本は正の値が必要です。");
        var path=new PathTotals(initial);
        foreach(long p in pnl) path.Add(p);
        return (path.Loss>0?path.Gain/path.Loss:null,path.Drawdown,path.Ratio*100);
    }

    public static MetricEstimateResult Run(SeriesPoint[] points,AnalysisResult observed,long initial,MetricBootstrapOptions options,
        CancellationToken token=default,IProgress<WorkProgress>? progress=null)
    {
        token.ThrowIfCancellationRequested();
        if(initial<=0 || points.Length!=observed.Count || options.Trials is <32 or >10000 || options.Workers is <1 or >2)
            return Unavailable("初期資本、入力件数または推定設定が範囲外です。");
        double? sets=ConvergenceWidth.Compute(observed).EffectiveCount;
        if(observed.Count<ConvergenceWidth.MinimumCount || sets is not double m || m<ConvergenceWidth.MinimumCount || !double.IsFinite(m))
            return Unavailable("総取引回数・補正用の連勝連敗記録・丸め前の総セット数が不足しています（件数とセット数は30以上）。");
        MetricBootstrapPlan plan;
        try{plan=CreatePlan(points.Length,m);}catch(ArgumentException e){return Unavailable(e.Message);}
        if(plan.VisitsPerTrial>MaximumVisits/options.Trials)
            return Unavailable("全件を使用した推定が作業予算（20億件評価）を超えます。期間・試行数を減らしてください。");
        long required=points.LongLength*64+options.Trials*24L+16777216;
        if(required>ResourceLimits.MemoryMiB(options.MemoryMiB)*1048576L)
            return Unavailable("推定が分析メモリ予算を超えます。期間を絞り込んでください。");
        var watch=Stopwatch.StartNew();
        var factors=new double[options.Trials];var amounts=new double[options.Trials];var percentages=new double[options.Trials];
        int completed=0,undefinedPf=0;long lastProgress=0;
        progress?.Report(new(0,"PF・DD推定を開始"));
        Parallel.For(0,options.Trials,new ParallelOptions{MaxDegreeOfParallelism=options.Workers,CancellationToken=token},trial=>{
            var random=new BootstrapRandom(options.Seed,trial,(uint)points.Length);
            var totals=new PathTotals(initial);int visited=0;
            for(int block=0;block<plan.Blocks;block++)
            {
                double scale=plan.Scale*(block==plan.Blocks-1?plan.LastWeight:plan.CommonWeight);
                int index=(int)random.Uniform();
                for(int j=0;j<plan.BlockLength;j++)
                {
                    if((visited++&1023)==0)token.ThrowIfCancellationRequested();
                    totals.Add(points[index].Profit*scale);
                    if(++index==points.Length)index=0;
                }
            }
            factors[trial]=totals.Loss>0?totals.Gain/totals.Loss:double.NaN;
            if(totals.Loss==0)Interlocked.Increment(ref undefinedPf);
            amounts[trial]=totals.Drawdown;percentages[trial]=totals.Ratio*100;
            int done=Interlocked.Increment(ref completed);
            long now=watch.ElapsedMilliseconds,previous=Interlocked.Read(ref lastProgress);
            if(now-previous>=100 && Interlocked.CompareExchange(ref lastProgress,now,previous)==previous)
                progress?.Report(new((double)done/options.Trials,$"PF・DD推定 {done:N0}/{options.Trials:N0}試行"));
        });
        token.ThrowIfCancellationRequested();
        var pf=ConditionalMeans(factors,$"損失0などでPFが定義できない試行があります（損失0: {undefinedPf}/{options.Trials}）。試行を除外せず推定範囲を算出不可とします。");
        var dd=ConditionalMeans(percentages);var amount=ConditionalMeans(amounts);
        token.ThrowIfCancellationRequested();watch.Stop();
        progress?.Report(new(1,"PF・DD推定完了"));
        token.ThrowIfCancellationRequested();
        return new(pf,dd,amount,options.Trials,plan.BlockLength,m,watch.Elapsed.TotalMilliseconds);
    }

    private struct PathTotals
    {
        public double Gain,Loss,Drawdown,Ratio;
        private double balance,peak;
        public PathTotals(double initial){balance=peak=initial;Gain=Loss=Drawdown=Ratio=0;}
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(double profit)
        {
            if(profit>=0)Gain+=profit;else Loss-=profit;
            balance+=profit;
            if(balance>=peak){peak=balance;return;}
            double depth=peak-balance;
            if(depth>Drawdown)Drawdown=depth;
            // Divide only on a new relative-DD record, not once per trade.
            if(depth>Ratio*peak)Ratio=depth/peak;
        }
    }
    private struct BootstrapRandom
    {
        private uint state;
        private readonly uint bound,threshold;
        public BootstrapRandom(uint seed,int trial,uint bound){this.bound=bound;threshold=unchecked(0u-bound)%bound;state=unchecked(seed+(uint)trial*747796405u);if(state==0)state=0xA341316Cu;}
        private uint Next(){unchecked{state^=state<<13;state^=state>>17;state^=state<<5;return state;}}
        public uint Uniform(){uint x;do{x=Next();}while(x<threshold);return x%bound;}
    }
}
