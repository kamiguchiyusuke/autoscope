namespace ArmadaAnalyzer;

public static class Analytics
{
    public static AnalysisResult Analyze(Snapshot snapshot,long initial,DrawdownOptions? options = null,CancellationToken token = default) => Analyze(snapshot.Points,initial,snapshot.PipFactors,options,token);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static AnalysisResult Analyze(SeriesPoint[] points, long initial,IReadOnlyDictionary<long,int>? pipFactors = null,DrawdownOptions? options = null,CancellationToken token = default)
    {
        options ??= new(); options.Validate(); token.ThrowIfCancellationRequested();
        var recovery=new DrawdownTracker(options.AmountThreshold,initial,(long)(options.RelativePercent*Amount.Scale));
        var chartDrawdowns=new ChartDrawdownTracker(initial);
        long net = 0, gross = 0, loss = 0, peak = 0, dd = 0;
        int wins = 0, losses = 0, zeros = 0, ws = 0, ls = 0, maxw = 0, maxl = 0, winRuns = 0, lossRuns = 0;
        double mean = 0, squaredDeviations = 0;
        long[] equity = new long[points.Length], drawdowns = new long[points.Length];
        checked
        {
            for (int i = 0; i < points.Length; i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                long x = points[i].Profit;
                double delta = x - mean; mean += delta / (i+1); squaredDeviations += delta * (x - mean);
                net += x;
                recovery.Observe(net,points[i].Ticks);
                chartDrawdowns.Observe(net,i);
                if (x > 0)
                {
                    gross += x;
                    wins++;
                    if (ws == 0) winRuns++;
                    ws++;
                    ls = 0;
                }
                else if (x < 0)
                {
                    loss += x;
                    losses++;
                    if (ls == 0) lossRuns++;
                    ls++;
                    ws = 0;
                }
                else
                {
                    zeros++;
                    ls = ws = 0;
                }
                maxw = Math.Max(maxw, ws);
                maxl = Math.Max(maxl, ls);
                peak = Math.Max(peak, net);
                long d = peak - net;
                dd = Math.Max(dd, d);
                equity[i] = net;
                drawdowns[i] = d;
            }
        }
        var episodes=recovery.Finish();
        return new(points.Length, net, gross, loss, dd, wins, losses, zeros, maxw, maxl, loss == 0 ? null : -(double)gross / loss, episodes.Relative is {} relative?(double)relative.Maximum:null, equity, drawdowns) { ChartDrawdowns=chartDrawdowns.Finish(points.Length-1), Recovery=episodes, Pips = PipStatistics.Compute(points,pipFactors,options.PipThreshold,token), WinRuns = winRuns, LossRuns = lossRuns, SampleDeviation = points.Length<2 ? null : Math.Sqrt(Math.Max(0,squaredDeviations)/(points.Length-1)) };
    }
}
