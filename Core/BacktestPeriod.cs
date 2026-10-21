using System.Globalization;
using System.Text.RegularExpressions;
namespace ArmadaAnalyzer;

// Display metadata only; never used to filter trades or change analytical results.
public sealed record BacktestPeriod(long Start,long End)
{
    public double Years => (End-Start)/(double)TimeSpan.TicksPerDay/365.2425;
    public string Label => $"（テスト期間 {Years.ToString("F2",CultureInfo.InvariantCulture)}年）";
    public static BacktestPeriod? Covering(IEnumerable<BacktestPeriod?> periods, long? from=null,long? to=null)
    {
        var list=periods.ToArray();
        if(from.HasValue && to.HasValue)return to>=from?new(from.Value,to.Value):null;
        if(list.Length==0 || list.Any(p=>p is null))return null;
        long start=list.Min(p=>p!.Start),end=list.Max(p=>p!.End);
        if(from.HasValue)start=Math.Max(start,from.Value);
        if(to.HasValue)end=Math.Min(end,to.Value);
        return end>=start?new(start,end):null;
    }
    public static BacktestPeriod? Parse(string text)
    {
        // MT4/MT5 Period cells, including optional intraday time. No trade-date fallback.
        var dates=Regex.Matches(text,@"\d{4}[./-]\d{2}[./-]\d{2}(?:\s+\d{2}:\d{2}(?::\d{2})?)?",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(dates.Count!=2)return null;
        bool Read(string value,out DateTime date)=>DateTime.TryParseExact(value.Replace('/','.').Replace('-','.'),["yyyy.MM.dd","yyyy.MM.dd HH:mm","yyyy.MM.dd HH:mm:ss"],CultureInfo.InvariantCulture,DateTimeStyles.None,out date);
        return Read(dates[0].Value,out var start)&&Read(dates[1].Value,out var end)&&end>=start?new(start.Ticks,end.Ticks):null;
    }
    internal static BacktestPeriod? ReadHeader(string path)
    {
        try
        {
            using var rows=new HtmlRows(path);
            Span<Range> cells=stackalloc Range[32];
            for(int i=0;i<512 && rows.Next(out var row);i++)
            {
                int count=HtmlImporter.Cells(row,cells);
                for(int c=0;c<count-1;c++)
                {
                    string label=HtmlImporter.Clean(row[cells[c]]).Trim().TrimEnd(':','：');
                    if(label is "期間" or "テスト期間" || label.Equals("Period",StringComparison.OrdinalIgnoreCase))
                        return Parse(HtmlImporter.Clean(row[cells[c+1]]));
                }
            }
        }
        catch(Exception e) when(e is IOException or FormatException or System.Text.DecoderFallbackException or RegexMatchTimeoutException) { }
        return null;
    }
}

public sealed partial class Workspace
{
    static void SavePeriod(Db db,long id,BacktestPeriod? period)
    {
        if(period is null)return;
        using var s=db.Prepare("INSERT OR REPLACE INTO dataset_periods(dataset,started,ended) VALUES(?1,?2,?3)");
        s.Bind(1,id).Bind(2,period.Start).Bind(3,period.End).Step();
    }
    public BacktestPeriod? DisplayPeriod(Filter filter,CancellationToken token=default)
    {
        using var db=Open();
        using var s=db.Prepare("SELECT started,ended FROM dataset_periods WHERE dataset=?1");
        var periods=new List<BacktestPeriod?>();
        foreach(long id in filter.DatasetIds.Distinct())
        {
            s.Bind(1,id);BacktestPeriod? period=s.Step()?new(s.Long(0),s.Long(1)):null;s.Reset();
            if(period is null)
            {
                // Existing source metadata has priority only if it is still the imported file.
                token.ThrowIfCancellationRequested();
                using(var source=db.Prepare("SELECT source,hash FROM datasets WHERE id=?1"))
                {
                    source.Bind(1,id);
                    if(source.Step())
                    {
                        try
                        {
                            var candidate=BacktestPeriod.ReadHeader(source.Text(0));
                            if(candidate!=null)
                            {
                                using var file=File.OpenRead(source.Text(0));
                                using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                                byte[] buffer=new byte[65536];int count;
                                while((count=file.Read(buffer))>0){token.ThrowIfCancellationRequested();hash.AppendData(buffer,0,count);}
                                if(Convert.ToHexString(hash.GetHashAndReset())==source.Text(1))period=candidate;
                            }
                        }
                        catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
                    }
                }
                // One-time migration for existing workspaces, performed on the analysis worker.
                using var trades=db.Prepare("SELECT MIN(opened),MAX(closed),COUNT(*) FROM trades WHERE dataset=?1");
                trades.Bind(1,id);
                if(period is null && trades.Step() && trades.Long(2)>0)period=new(trades.Long(0),trades.Long(1));
                SavePeriod(db,id,period);
            }
            periods.Add(period);
        }
        return BacktestPeriod.Covering(periods,filter.From,filter.To);
    }
}
