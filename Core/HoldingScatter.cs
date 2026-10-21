namespace ArmadaAnalyzer;

public readonly record struct HoldingPoint(long Duration, long Profit, long PriceMove, long DatasetId)
{
    public decimal? Pips {get;init;}
}
public sealed record HoldingScatterResult(HoldingPoint[] Points, long Total)
{
    public long PipCount {get;init;}
}
public sealed partial class Workspace
{
    /// <summary>Streams normalized rows, retains a deterministic reservoir plus extrema. Never creates a per-trade object array.</summary>
    public HoldingScatterResult LoadHoldingScatter(Filter filter, int limit = 6000, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (limit < 1 || limit > 16000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var sample = new HoldingPoint[limit];
        var extremes = new HoldingPoint[8];
        var random = new Random(42);
        long total = 0,pipCount=0;
        var sizes=ListDatasets().ToDictionary(d=>d.Id,d=>DukascopyPips.Size(d.Symbol));
        using var db = Open();
        using var query = db.Prepare("SELECT opened,closed,pnl,entry,exit,side,dataset FROM trades WHERE " + Where(filter) + " ORDER BY dataset,seq");
        while (query.Step())
        {
            if ((total & 4095) == 0)
                token.ThrowIfCancellationRequested();
            long dataset = query.Long(6);
            decimal weight = filter.Weights?.GetValueOrDefault(dataset, 1m) ?? 1m;
            if (weight < 0 || weight > 1000)
                throw new ArgumentException("ウェイトは0〜1000です");
            long profit = checked((long)decimal.Round(query.Long(2) * weight, 0, MidpointRounding.ToEven));
            long move = query.Long(5) == 1 ? checked(query.Long(4) - query.Long(3)) : checked(query.Long(3) - query.Long(4));
            decimal? pips=sizes.GetValueOrDefault(dataset) is decimal size?(decimal)move/Amount.Scale/size:null;
            if(pips!=null)pipCount++;
            var point = new HoldingPoint(checked(query.Long(1) - query.Long(0)), profit, move, dataset){Pips=pips};
            if (total == 0)
                Array.Fill(extremes, point);
            if (point.Duration < extremes[0].Duration)
                extremes[0] = point;
            if (point.Duration > extremes[1].Duration)
                extremes[1] = point;
            if (point.Profit < extremes[2].Profit)
                extremes[2] = point;
            if (point.Profit > extremes[3].Profit)
                extremes[3] = point;
            if (pips!=null ? extremes[4].Pips==null || pips<extremes[4].Pips : extremes[4].Pips==null && point.PriceMove<extremes[4].PriceMove)
                extremes[4] = point;
            if (pips!=null ? extremes[5].Pips==null || pips>extremes[5].Pips : extremes[5].Pips==null && point.PriceMove>extremes[5].PriceMove)
                extremes[5] = point;
            if (total == 0)
                extremes[6] = point;
            extremes[7] = point;
            if (total < limit)
                sample[(int)total] = point;
            else
            {
                long index = random.NextInt64(total + 1);
                if (index < limit)
                    sample[(int)index] = point;
            }
            total++;
        }
        token.ThrowIfCancellationRequested();
        return new(sample.Take((int)Math.Min(total, limit)).Concat(total > 0 ? extremes : []).Distinct().ToArray(), total){PipCount=pipCount};
    }
}
