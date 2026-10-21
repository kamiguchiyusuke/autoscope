using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace ArmadaAnalyzer;

public sealed record SlippageResult(decimal? Pips,string Detail,int Count=0);

// Constant additional round-trip adverse pips per selected settlement, original lots/weights.
// Money/pip is calibrated from reported price P/L, not net P/L including swap/commission.
public sealed class SlippageAccumulator
{
    private decimal cost,calibrationProfit,calibrationExposure,zeroVolume;
    public decimal Net {get;private set;}
    public int Count {get;private set;}
    private bool invalid;
    public void Add(Trade trade,decimal pipSize,decimal weight=1)
    {
        Count++;Net+=decimal.Round(trade.Profit*weight,0,MidpointRounding.ToEven);
        if(trade.PriceProfit is not long gross || trade.Volume<=0 || pipSize<=0 || weight<0){invalid=true;return;}
        decimal pips=DukascopyPips.Move(trade.EntryPrice,trade.ExitPrice,trade.Side,pipSize);
        decimal lots=(decimal)trade.Volume/Amount.Scale;
        if(pips==0)
        {
            if(gross!=0)invalid=true;
            zeroVolume+=lots*weight;return;
        }
        decimal moneyPerPip=gross/pips;
        if(moneyPerPip<=0){invalid=true;return;}
        cost+=moneyPerPip*weight;
        calibrationProfit+=Math.Abs((decimal)gross);calibrationExposure+=Math.Abs(pips)*lots;
    }
    public decimal? CostPerPip=>invalid || (zeroVolume>0 && calibrationExposure<=0)?null:
        cost+(zeroVolume>0?zeroVolume*calibrationProfit/calibrationExposure:0);
    public static SlippageResult Finish(decimal net,int count,decimal? cost)
    {
        if(count<=0 || net<=0)return new(null,"総純益が正で、決済取引が必要です。");
        if(cost is not >0)return new(null,"価格差から1pipあたりの金額を推定できない取引があります。");
        return new(net*.25m/cost.Value,"総純益×25%÷全対象取引の1pip損失額合計。新規・決済を合わせた追加スリップを全取引に同量適用する近似。取引ごとの元ロットとウェイトを反映。手数料・スワップは既存の純損益に含み、二重加算しません。価格損益÷売買方向調整済みpipsで換算し、価格差ゼロの取引のみ同じデータのロット当たり換算値で補完。約定・換算レート・ロットは変化しない仮定です。",count);
    }
}

public sealed partial class Workspace
{
    public SlippageResult EstimateSlippage(Filter filter,string seriesKey,long expectedNet,int expectedCount,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(expectedNet<=0 || expectedCount<=0)return SlippageAccumulator.Finish(expectedNet,expectedCount,null);
        string cache=Path.Combine(Root,"cache","slippage-v2-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seriesKey)))+".json");
        try {if(File.Exists(cache) && JsonSerializer.Deserialize<SlippageResult>(File.ReadAllText(cache)) is { Pips:>0 } hit && hit.Count==expectedCount)return hit;}
        catch(Exception e) when(e is IOException or JsonException) { }
        decimal sumCost=0,sumNet=0;int sumCount=0;
        var metadata=ListDatasets().ToDictionary(d=>d.Id);
        var formats=LoadReportMetadata(filter,token).ToDictionary(d=>d.DatasetId,d=>d.Fields.GetValueOrDefault("形式"));
        using var db=Open();
        foreach(long id in filter.DatasetIds.Distinct())
        {
            token.ThrowIfCancellationRequested();
            if(!metadata.TryGetValue(id,out var dataset) || DukascopyPips.Size(dataset.Symbol) is not decimal size)
                return new(null,"銘柄のpip単位が未確認です。");
            // Native MT4 importer stores price P/L directly as pnl; MT5 needs its separate
            // price P/L persisted at import so fees and swaps are never used as pip value.
            decimal weight=filter.Weights?.GetValueOrDefault(id,1m)??1m;
            bool legacyMt4=formats.GetValueOrDefault(id)=="MT4";
            using(var origin=db.Prepare("SELECT identity FROM datasets WHERE id=?1")){
                origin.Bind(1,id);legacyMt4 &= origin.Step() && origin.Text(0).EndsWith("|parser1",StringComparison.Ordinal);
            }
            var stored=new SlippageAccumulator();bool complete=true;
            using(var rows=db.Prepare("SELECT t.seq,t.ticket,t.part,t.opened,t.closed,t.volume,t.entry,t.exit,t.pnl,t.side,t.source_row,p.profit,p.seq IS NOT NULL FROM (SELECT * FROM trades WHERE "+Where(filter with{DatasetIds=[id]})+") t LEFT JOIN trade_price_profit p ON p.dataset=t.dataset AND p.seq=t.seq ORDER BY t.seq")){
                while(rows.Step()){
                    token.ThrowIfCancellationRequested();
                    if(rows.Long(12)==0 && !legacyMt4){complete=false;break;}
                    stored.Add(new Trade(rows.Long(0),rows.Long(1),(int)rows.Long(2),rows.Long(3),rows.Long(4),rows.Long(5),rows.Long(6),rows.Long(7),rows.Long(8),(int)rows.Long(9),id,rows.Long(10)){PriceProfit=rows.Long(12)!=0?rows.Long(11):rows.Long(8)},size,weight);
                }
            }
            if(complete){
                if(stored.CostPerPip is not decimal storedCost)return SlippageAccumulator.Finish(expectedNet,expectedCount,null);
                sumCost+=storedCost;sumNet+=stored.Net;sumCount+=stored.Count;continue;
            }
            using var source=db.Prepare("SELECT source,hash FROM datasets WHERE id=?1");source.Bind(1,id);
            if(!source.Step())return new(null,"元データがありません。");
            string path=source.Text(0);
            try
            {
                // Keep a read lock while hashing/parsing so changed reports cannot be substituted.
                using var file=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer=new byte[65536];int bytes;
                while((bytes=file.Read(buffer))>0){token.ThrowIfCancellationRequested();hash.AppendData(buffer,0,bytes);}
                if(Convert.ToHexString(hash.GetHashAndReset())!=source.Text(1))return new(null,"元レポートが取込時から変更されています。再取込してください。");

                SlippageAccumulator Parse(bool cp932)
                {
                    var accumulator=new SlippageAccumulator();
                    HtmlImporter.Parse(path,t=>{if(Matches(t,filter))accumulator.Add(t,size,weight);},new(InitialBalance:dataset.InitialBalance),ResourceLimits.DefaultMemoryMiB,token,null,cp932);
                    return accumulator;
                }
                SlippageAccumulator values;
                try {values=Parse(false);} catch(DecoderFallbackException){values=Parse(true);}
                if(values.CostPerPip is not decimal cost)return SlippageAccumulator.Finish(expectedNet,expectedCount,null);
                sumCost+=cost;sumNet+=values.Net;sumCount+=values.Count;
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or FormatException or OverflowException)
            {return new(null,"元レポートの価格損益を確認できません。取込時のHTMLが必要です。");}
        }
        if(sumCount!=expectedCount || sumNet!=expectedNet)return new(null,"元レポートと分析対象の取引・純損益が一致しません。");
        var result=SlippageAccumulator.Finish(sumNet,sumCount,sumCost);
        if(result.Pips.HasValue)
        {
            token.ThrowIfCancellationRequested();
            try {Directory.CreateDirectory(Path.GetDirectoryName(cache)!);File.WriteAllText(cache,JsonSerializer.Serialize(result));}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
    private static bool Matches(Trade trade,Filter filter)
    {
        if(filter.From is long from && trade.CloseTicks<from || filter.To is long to && trade.CloseTicks>=to || filter.Side!=0 && trade.Side!=filter.Side)return false;
        if(filter.Calendar is not EntryCalendar c)return true;
        var date=new DateTime(trade.OpenTicks);
        return (c.Months & (1<<(date.Month-1)))!=0 && (c.Days & (1<<(date.Day-1)))!=0 &&
            (c.Weekdays & (1<<(int)date.DayOfWeek))!=0 && (c.Hours & (1<<date.Hour))!=0 && (c.Minutes & (1L<<date.Minute))!=0;
    }
}
