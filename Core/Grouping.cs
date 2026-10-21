namespace ArmadaAnalyzer;

public sealed record GroupRow(string Period, int Count, int Wins, int Losses, int Zeros, long Profit)
{
    public string Net => Amount.Display(Profit); public string WinRate => Count == 0 ? "—" : $"{100.0 * Wins / Count:F2}%";
}
public static class Grouping
{
    private sealed class Bucket { public int Count,Wins,Losses,Zeros; public long Profit; }
    public static GroupRow[] Compute(SeriesPoint[] points, int mode,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();int index=0;
        var buckets = new SortedDictionary<int,Bucket>();
        var calendar=new CalendarBucket();int previous=-1;Bucket? current=null;
        foreach (ref readonly var p in points.AsSpan())
        {
            if((index++&4095)==0)token.ThrowIfCancellationRequested();
            int key = mode switch
            {
                0 => calendar.Get(p.Ticks,false),
                1 => calendar.Get(p.Ticks,true),
                2 => (int)new DateTime(p.Ticks).DayOfWeek,
                _ => new DateTime(p.Ticks).Hour
            };
            if(current==null || key!=previous){if(!buckets.TryGetValue(key,out current))buckets[key]=current=new();previous=key;}
            var b = current;
            b.Count++;
            if (p.Profit > 0)
                b.Wins++;
            else if (p.Profit < 0)
                b.Losses++;
            else
                b.Zeros++;
            b.Profit = checked(b.Profit + p.Profit);
        }
        return buckets.Select(x => new GroupRow(mode switch { 0 => x.Key + "年", 1 => $"{x.Key / 100:D4}/{x.Key % 100:D2}", 2 => new[] { "日曜日", "月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日" }[x.Key], _ => x.Key + "時" }, x.Value.Count, x.Value.Wins, x.Value.Losses, x.Value.Zeros, x.Value.Profit)).ToArray();
    }
}
