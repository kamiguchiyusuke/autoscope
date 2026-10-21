namespace ArmadaAnalyzer;

public sealed record CorrelationRow(long First, long Second, int Days, double? Value);
public static class Portfolio
{
    public static CorrelationRow[] Correlations(SeriesPoint[] points,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();int index=0;
        if (points.Length == 0) return [];
        long firstId = points[0].DatasetId;
        bool multiple = false;
        foreach (ref readonly var point in points.AsSpan())
        {
            if((index++&4095)==0)token.ThrowIfCancellationRequested();
            if (point.DatasetId != firstId) { multiple = true; break; }
        }
        if (!multiple) return [];
        long unionFrom = long.MaxValue, unionTo = -1;
        var daily = new Dictionary<long, Dictionary<long, long>>();
        foreach (ref readonly var p in points.AsSpan())
        {
            if((index++&4095)==0)token.ThrowIfCancellationRequested();
            if (!daily.TryGetValue(p.DatasetId, out var d))
            {
                if(daily.Count>=32)throw new InvalidOperationException("相関分析は一度に32戦略までです");
                daily[p.DatasetId] = d = new();
            }
            long date = p.Ticks - p.Ticks % TimeSpan.TicksPerDay;
            unionFrom = Math.Min(unionFrom, date);
            unionTo = Math.Max(unionTo, date);
            if((unionTo-unionFrom)/TimeSpan.TicksPerDay>=36600)throw new InvalidOperationException("相関分析の対象期間は100年以内です");
            d[date] = checked(d.GetValueOrDefault(date) + p.Profit);
        }
        if (daily.Count > 32)
            throw new InvalidOperationException("相関分析は一度に32戦略までです");
        var ids = daily.Keys.Order().ToArray();
        var result = new List<CorrelationRow>();
        for (int a = 0; a < ids.Length; a++)
            for (int b = a + 1; b < ids.Length; b++)
            {
                var x = daily[ids[a]];
                var y = daily[ids[b]];
                long from = unionFrom, to = unionTo;
                long days = to < from ? 0 : (to - from) / TimeSpan.TicksPerDay + 1;
                if (days > 36600)
                    throw new InvalidOperationException("相関分析の対象期間は100年以内です");
                double mx = 0, my = 0, c = 0, vx = 0, vy = 0;
                int n = 0;
                for (long t = from; t <= to; t += TimeSpan.TicksPerDay)
                {
                    if((n&4095)==0)token.ThrowIfCancellationRequested();
                    double xx = (double)x.GetValueOrDefault(t) / Amount.Scale, yy = (double)y.GetValueOrDefault(t) / Amount.Scale;
                    n++;
                    double dx = xx - mx, dy = yy - my;
                    mx += dx / n;
                    my += dy / n;
                    c += dx * (yy - my);
                    vx += dx * (xx - mx);
                    vy += dy * (yy - my);
                    if (t > to - TimeSpan.TicksPerDay)
                        break;
                }
                result.Add(new(ids[a], ids[b], n, n >= 2 && vx > 0 && vy > 0 ? Math.Clamp(c / Math.Sqrt(vx * vy), -1, 1) : null));
            }
        return result.ToArray();
    }
}
