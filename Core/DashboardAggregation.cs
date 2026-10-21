namespace ArmadaAnalyzer;

public enum DashboardPeriod
{
    Year, Month, Day, Weekday, Hour, Minute
}
public sealed record DashboardBucket(string Label, int Count, int Wins, int Losses, int Zeros, long Profit, long GrossProfit, long GrossLoss, long Drawdown);
public sealed record MonthlyRow(int Year, long[] Profit, int[] Count, long Total);

/// <summary>Small aggregate buffers only; no copies of the trade series or per-trade objects.</summary>
public static class DashboardAggregation
{
    private sealed class Bucket
    {
        public int Count, Wins, Losses, Zeros;
        public long Profit, GrossProfit, GrossLoss, Peak, Drawdown;
    }
    public static DashboardBucket[] Compute(SeriesPoint[] points, DashboardPeriod period,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();int index=0;
        var groups = new SortedDictionary<int, Bucket>();
        int size = period switch
        {
            DashboardPeriod.Month => 12,
            DashboardPeriod.Day => 31,
            DashboardPeriod.Weekday => 7,
            DashboardPeriod.Hour => 24,
            DashboardPeriod.Minute => 60,
            _ => 0
        };
        int start = period is DashboardPeriod.Month or DashboardPeriod.Day ? 1 : 0;
        for (int i = start; i < start + size; i++)
            groups[i] = new();
        var direct=size==0?null:new Bucket[size+start];
        if(direct!=null)foreach(var pair in groups)direct[pair.Key]=pair.Value;
        var calendar=new CalendarBucket();int previous=-1;Bucket? current=null;
        checked
        {
            foreach (ref readonly var point in points.AsSpan())
            {
                if((index++&4095)==0)token.ThrowIfCancellationRequested();
                var date = new DateTime(point.Ticks);
                int key = period switch
                {
                    DashboardPeriod.Year => calendar.Get(point.Ticks,false),
                    DashboardPeriod.Month => date.Month,
                    DashboardPeriod.Day => date.Day,
                    DashboardPeriod.Weekday => (int)date.DayOfWeek,
                    DashboardPeriod.Hour => date.Hour,
                    _ => date.Minute
                };
                Bucket bucket;
                if(direct!=null)bucket=direct[key];
                else {if(current==null || previous!=key){if(!groups.TryGetValue(key,out current))groups[key]=current=new();previous=key;}bucket=current;}
                bucket.Count++;
                bucket.Profit += point.Profit;
                if (point.Profit > 0)
                {
                    bucket.Wins++;
                    bucket.GrossProfit += point.Profit;
                }
                else if (point.Profit < 0)
                {
                    bucket.Losses++;
                    bucket.GrossLoss -= point.Profit;
                }
                else
                    bucket.Zeros++;
                bucket.Peak = Math.Max(bucket.Peak, bucket.Profit);
                bucket.Drawdown = Math.Max(bucket.Drawdown, bucket.Peak - bucket.Profit);
            }
        }
        return groups.Select(x => new DashboardBucket(period == DashboardPeriod.Weekday ? new[] { "日", "月", "火", "水", "木", "金", "土" }[x.Key] : x.Key.ToString(), x.Value.Count, x.Value.Wins, x.Value.Losses, x.Value.Zeros, x.Value.Profit, x.Value.GrossProfit, x.Value.GrossLoss, x.Value.Drawdown)).ToArray();
    }
    public static MonthlyRow[] Months(SeriesPoint[] points,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();int index=0;
        var years = new Dictionary<int, MonthlyRow>();
        var calendar=new CalendarBucket();int previous=-1,month=0;MonthlyRow? current=null;
        checked
        {
            foreach (ref readonly var point in points.AsSpan())
            {
                if((index++&4095)==0)token.ThrowIfCancellationRequested();
                int key=calendar.Get(point.Ticks,true);
                if(current==null || previous!=key){int year=key/100;month=key%100-1;if(!years.TryGetValue(year,out current))years[year]=current=new(year,new long[12],new int[12],0);previous=key;}
                current.Profit[month] += point.Profit;
                current.Count[month]++;
            }
            return years.OrderBy(x => x.Key).Select(x => x.Value).Select(row => row with { Total = row.Profit.Aggregate(0L, (total, value) => checked(total + value)) }).ToArray();
        }
    }
}
