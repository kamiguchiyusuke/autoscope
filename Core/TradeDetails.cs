namespace ArmadaAnalyzer;

public sealed record TradeDetails(int Count, decimal TotalDuration, long? MinDuration, long? MaxDuration,
    decimal? MedianDuration, decimal? TotalPips, decimal? MinPips, decimal? MaxPips, decimal? WinPips, decimal? LossPips)
{
    public PositionConcurrency Positions { get; init; }
}

public sealed partial class Workspace
{
    public TradeDetails LoadTradeDetails(Filter filter, int expectedCount, CancellationToken token = default, IProgress<WorkProgress>? progress = null, bool includePips = true)
    {
        if (expectedCount < 0 || expectedCount > 8000000) throw new InvalidOperationException("詳細分析は800万件までです。期間を絞ってください。");
        token.ThrowIfCancellationRequested();
        string where = Where(filter);
        var sizes = includePips ? ListDatasets().ToDictionary(d => d.Id, d => DukascopyPips.Size(d.Symbol)) : null;
        var durations = new long[expectedCount];
        var positions = new PositionInterval[expectedCount];
        decimal totalDuration = 0, total = 0, win = 0, loss = 0;
        decimal? min = null, max = null;
        int count = 0, wins = 0, losses = 0;
        long? minDuration = null, maxDuration = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool allKnown = includePips;
        using var db = Open();
        using var query = db.Prepare((includePips ? "SELECT opened,closed,entry,exit,pnl,side,dataset,ticket FROM trades WHERE " : "SELECT opened,closed,0,0,0,side,dataset,ticket FROM trades WHERE ") + where);
        while (query.Step())
        {
            if ((count & 8191) == 0) { token.ThrowIfCancellationRequested(); if (watch.ElapsedMilliseconds >= 100) { progress?.Report(new((double)count / Math.Max(1,expectedCount), "取引明細の詳細指標を計算中")); watch.Restart(); } }
            if (count >= durations.Length) throw new InvalidOperationException("履歴が変更されています。再分析してください。");
            long duration = checked(query.Long(1) - query.Long(0));
            positions[count] = new(query.Long(6),query.Long(7),query.Long(0),query.Long(1),(int)query.Long(5));
            durations[count++] = duration; totalDuration += duration;
            minDuration = minDuration is null ? duration : Math.Min(minDuration.Value,duration); maxDuration = maxDuration is null ? duration : Math.Max(maxDuration.Value,duration);
            if (!includePips) continue;
            long dataset = query.Long(6);
            if (sizes!.GetValueOrDefault(dataset) is decimal size)
            {
                decimal pips = DukascopyPips.Move(query.Long(2),query.Long(3),(int)query.Long(5),size);
                total += pips; min = min is null ? pips : Math.Min(min.Value,pips); max = max is null ? pips : Math.Max(max.Value,pips);
                decimal weight = filter.Weights?.GetValueOrDefault(dataset,1m) ?? 1m;
                long pnl = checked((long)decimal.Round(query.Long(4)*weight,0,MidpointRounding.ToEven));
                if (pnl > 0) { wins++; win += pips; }
                else if (pnl < 0) { losses++; loss += pips; }
            }
            else allKnown = false;
        }
        if (count != expectedCount) throw new InvalidOperationException("履歴が変更されています。再分析してください。");
        token.ThrowIfCancellationRequested();
        // Cancellable in-place selection: no full sort or array copy for the median.
        decimal? median = count == 0 ? null : Select(durations,count/2,token);
        if (count>0 && count%2==0) median = (median + Select(durations,count/2-1,token))/2;
        return new(count,totalDuration,minDuration,maxDuration,median,
            allKnown && count>0 ? total : null,allKnown?min:null,allKnown?max:null,
            allKnown && wins>0 ? win/wins : null, allKnown && losses>0 ? loss/losses : null)
            { Positions = ConcurrentPositions.Calculate(positions,count,token) };
    }
    private static long Select(long[] values,int rank,CancellationToken token)
    {
        int left=0,right=values.Length-1;
        while(left<right)
        {
            token.ThrowIfCancellationRequested();
            long pivot=values[left+(right-left)/2]; int low=left,i=left,high=right;
            while(i<=high)
            {
                if ((i&8191)==0) token.ThrowIfCancellationRequested();
                if(values[i]<pivot) { (values[i],values[low])=(values[low],values[i]); i++; low++; }
                else if(values[i]>pivot) { (values[i],values[high])=(values[high],values[i]); high--; }
                else i++;
            }
            if(rank<low) right=low-1; else if(rank>high) left=high+1; else return pivot;
        }
        return values[left];
    }
}
