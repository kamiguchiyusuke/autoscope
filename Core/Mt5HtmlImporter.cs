using System.Globalization;
namespace ArmadaAnalyzer;

// Tester deals lack position IDs. Overlapping entries are only matched when a
// simultaneous full-close batch uniquely satisfies a common positive profit factor.
internal static class Mt5HtmlImporter
{
    internal static bool IsHeader(ReadOnlySpan<char> row) =>
        (row.Contains("新規・決済",StringComparison.Ordinal) && row.Contains("約定",StringComparison.Ordinal)) ||
        (row.Contains("Direction",StringComparison.OrdinalIgnoreCase) && row.Contains("Commission",StringComparison.OrdinalIgnoreCase) && row.Contains("Deal",StringComparison.OrdinalIgnoreCase));
    internal static long Number(string value) => Amount.Parse(value.Replace(" ","").Replace("\u00a0","").Replace("\u202f",""));
    sealed record Open(long Ticket,long Time,long Volume,long Price,long Costs,int Side,int Part);
    sealed record Exit(long Time,long Volume,long Price,long Costs,long Swap,long Profit,int Side,long Row,decimal Rounding);
    static Open[] MatchBatch(Open[] entries,List<Exit> exits,CancellationToken token)
    {
        // No fixed contract size/currency is assumed. For a simultaneous close batch,
        // find the UNIQUE matching that admits one positive gross-P/L conversion
        // factor within the precision of each printed profit. Different fill prices
        // are supported. Multiple possible matches fail instead of assuming FIFO.
        if(entries.Length>8)throw new FormatException("ポジションIDなしのMT5同時決済照合は8件までです");
        var used=new bool[entries.Length];var current=new Open[entries.Length];Open[]? result=null;
        int solutions=0,steps=0;
        void Search(int index,decimal low,decimal high)
        {
            token.ThrowIfCancellationRequested();
            if(++steps>10000)throw new FormatException("MT5同時決済の照合候補が多すぎます");
            if(solutions>1)return;
            if(index==exits.Count){solutions++;result=(Open[])current.Clone();return;}
            var e=exits[index];
            for(int i=0;i<entries.Length;i++)
            {
                var o=entries[i];if(used[i] || o.Volume!=e.Volume || o.Side==e.Side || o.Time>e.Time)continue;
                decimal nextLow=low,nextHigh=high;
                decimal exposure=(o.Side==1?(decimal)e.Price-o.Price:(decimal)o.Price-e.Price)*e.Volume;
                if(exposure==0){if(e.Profit!=0)continue;}
                else
                {
                    decimal a=(e.Profit-e.Rounding)/exposure,b=(e.Profit+e.Rounding)/exposure;
                    nextLow=Math.Max(low,Math.Min(a,b));nextHigh=Math.Min(high,Math.Max(a,b));
                    if(nextHigh<=0 || nextLow>nextHigh)continue;
                }
                used[i]=true;current[index]=o;Search(index+1,nextLow,nextHigh);used[i]=false;
                if(solutions>1)return;
            }
        }
        Search(0,0,decimal.MaxValue);
        if(solutions!=1)throw new FormatException("MT5同時決済の価格差・数量・損益から対応を一意に確定できません。推測で対応付けしません");
        return result!;
    }
    internal static ImportMetadata Parse(HtmlRows rows,ReadOnlySpan<char> header,ReadOnlySpan<Range> headerRanges,long rowId,long initial,ImportOptions options,Action<Trade> emit,CancellationToken token,IProgress<WorkProgress>? progress)
    {
        var names=new string[headerRanges.Length];for(int i=0;i<names.Length;i++)names[i]=HtmlImporter.Clean(header[headerRanges[i]]);
        int Find(params string[] labels)=>Array.FindIndex(names,n=>labels.Any(l=>n.Equals(l,StringComparison.OrdinalIgnoreCase)));
        int time=Find("Time","時間"),deal=Find("Deal","約定"),symbol=Find("Symbol","銘柄"),type=Find("Type","タイプ"),direction=Find("Direction","新規・決済"),volume=Find("Volume","数量"),price=Find("Price","価格"),commission=Find("Commission","手数料"),swap=Find("Swap","スワップ"),profit=Find("Profit","損益"),fee=Find("Fee","Fees","料金");
        int[] required=[time,deal,symbol,type,direction,volume,price,commission,swap,profit];
        if(required.Any(i=>i<0))throw new FormatException("MT5約定表の必須列（時刻・約定・銘柄・売買・新規決済・数量・価格・手数料・スワップ・損益）が不足しています");
        int lastColumn=Math.Max(required.Max(),fee);
        var active=new List<Open>(); List<Exit>? pending=null;Open[]? closing=null;
        string instrument="";long sequence=0,lastDeal=-1,lastTime=0;bool cashSeen=false;int matchedBatches=0;
        void CloseBatch()
        {
            if(pending==null || closing==null || pending.Count!=closing.Length)return;
            var matched=MatchBatch(closing,pending,token);
            for(int i=0;i<pending.Count;i++)
            {
                var o=matched[i];var e=pending[i];
                emit(new(sequence++,o.Ticket,o.Part,o.Time,e.Time,e.Volume,o.Price,e.Price,checked(e.Profit+e.Swap+e.Costs+o.Costs),o.Side,0,e.Row){PriceProfit=e.Profit});
                active.Remove(o);
            }
            pending=null;closing=null;matchedBatches++;
        }
        Span<Range> ranges=stackalloc Range[32];var watch=System.Diagnostics.Stopwatch.StartNew();
        while(rows.Next(out var row))
        {
            token.ThrowIfCancellationRequested();rowId++;
            int count=HtmlImporter.Cells(row,ranges);
            if(count==0)continue;
            // Totals and empty footer rows have no time. Date-shaped malformed rows fail below.
            string first=HtmlImporter.Clean(row[ranges[Math.Min(time,count-1)]]);
            if(first.Length==0)
            {
                string availableDeal=deal<count?HtmlImporter.Clean(row[ranges[deal]]):"";
                string availableType=type<count?HtmlImporter.Clean(row[ranges[type]]).ToLowerInvariant():"";
                if((count>lastColumn && (availableDeal.Length>0 || availableType.Length>0)) ||
                    (long.TryParse(availableDeal,NumberStyles.None,CultureInfo.InvariantCulture,out long missingTimeId) && missingTimeId>0) ||
                    availableType is "buy" or "sell" or "balance" or "credit")
                    throw new FormatException($"MT5 HTML行{rowId}: 約定時刻がありません");
                continue;
            }
            if(count<=lastColumn)throw new FormatException($"MT5 HTML行{rowId}: 約定セルが不足しています");
            long ticks=HtmlImporter.ParseTime(first,rowId).Ticks;
            long id=long.Parse(HtmlImporter.Clean(row[ranges[deal]]),NumberStyles.None,CultureInfo.InvariantCulture);
            if(id<0 || id<=lastDeal || ticks<lastTime)throw new FormatException($"MT5 HTML行{rowId}: 約定番号/時刻の重複または並び順が不正です。約定を時系列で出力してください");
            lastDeal=id;lastTime=ticks;
            string kind=HtmlImporter.Clean(row[ranges[type]]).ToLowerInvariant();
            long costs=checked(Number(HtmlImporter.Clean(row[ranges[commission]]))+(fee<0?0:Number(HtmlImporter.Clean(row[ranges[fee]]))));
            long swapValue=Number(HtmlImporter.Clean(row[ranges[swap]])),gain=Number(HtmlImporter.Clean(row[ranges[profit]]));
            if(kind is "balance" or "credit")
            {
                if(pending!=null)throw new FormatException("MT5同時決済の途中に入出金があり、対応を特定できません");
                if(costs!=0 || swapValue!=0)throw new FormatException($"MT5 HTML行{rowId}: 入出金に付随する費用を取引へ対応付けできません");
                if(!cashSeen && sequence==0 && active.Count==0 && kind=="balance" && initial==0 && options.InitialBalance==null)initial=gain;
                cashSeen=true;continue;
            }
            if(kind is not ("buy" or "sell"))throw new FormatException($"MT5 HTML行{rowId}: 未対応の約定種別 {kind} です。独立した手数料等を推測で配賦しません");
            string name=HtmlImporter.Clean(row[ranges[symbol]]);
            if(name.Length==0)throw new FormatException($"MT5 HTML行{rowId}: 銘柄がありません");
            if(instrument.Length==0)instrument=name;
            else if(instrument!=name)throw new FormatException("複数銘柄を含むMT5レポートは銘柄別に出力してください（pipsの誤集計を防ぐため）");
            long lots=Number(HtmlImporter.Clean(row[ranges[volume]])),quote=Number(HtmlImporter.Clean(row[ranges[price]]));
            if(lots<=0 || quote<=0)throw new FormatException($"MT5 HTML行{rowId}: 数量/価格が不正です");
            int side=kind=="buy"?1:2;string entry=HtmlImporter.Clean(row[ranges[direction]]).ToLowerInvariant();
            if(entry=="in")
            {
                if(pending!=null)throw new FormatException($"MT5 HTML行{rowId}: 同時決済が完了する前に新規約定があり、対応を特定できません");
                if(active.Count>=100000)throw new FormatException("MT5未決済約定が上限10万件を超えました");
                if(gain!=0)throw new FormatException($"MT5 HTML行{rowId}: 新規約定に実現損益があり、対応を特定できません");
                active.Add(new(id,ticks,lots,quote,checked(costs+swapValue),side,0));
            }
            else if(entry is "out" or "in/out")
            {
                string printed=HtmlImporter.Clean(row[ranges[profit]]);int point=printed.IndexOf('.');
                int digits=point<0?0:printed.Length-point-1;decimal quantum=Amount.Scale;for(int i=0;i<digits;i++)quantum/=10;
                var exit=new Exit(ticks,lots,quote,costs,swapValue,gain,side,rowId,quantum/2);
                bool reversal=entry=="in/out";
                if(pending!=null)
                {
                    var batchHead=pending[0];
                    if(reversal || ticks!=batchHead.Time || side!=batchHead.Side)
                        throw new FormatException($"MT5 HTML行{rowId}: 複数ポジションの決済条件が異なり、ポジションIDなしでは対応を特定できません");
                    pending.Add(exit);CloseBatch();continue;
                }
                var candidates=active.Where(o=>o.Side!=side).ToArray();
                if(candidates.Length>1)
                {
                    if(reversal || candidates.Length>8 || candidates.Any(o=>o.Time>ticks))throw new FormatException("複数ポジションの部分決済/反転は、MT5ポジションIDなしでは対応を特定できません");
                    closing=candidates;pending=[exit];continue;
                }
                if(candidates.Length!=1)throw new FormatException($"MT5 HTML行{rowId}: 決済に対応する新規約定がありません");
                var o=candidates[0];
                if(ticks<o.Time || (reversal?lots<=o.Volume:lots>o.Volume))throw new FormatException($"MT5 HTML行{rowId}: 決済/反転数量が不正です");
                long closed=reversal?o.Volume:lots;
                long entryCost=closed==o.Volume?o.Costs:checked((long)decimal.Round((decimal)o.Costs*closed/o.Volume,0,MidpointRounding.ToEven));
                long exitCost=reversal?checked((long)decimal.Round((decimal)costs*closed/lots,0,MidpointRounding.ToEven)):costs;
                emit(new(sequence++,o.Ticket,o.Part,o.Time,ticks,closed,o.Price,quote,checked(gain+swapValue+entryCost+exitCost),o.Side,0,rowId){PriceProfit=gain});
                active.Remove(o);
                if(reversal)active.Add(new(id,ticks,lots-closed,quote,costs-exitCost,side,0));
                else if(closed<o.Volume)active.Add(o with {Volume=o.Volume-closed,Costs=o.Costs-entryCost,Part=o.Part+1});
            }
            else throw new FormatException($"MT5 HTML行{rowId}: 未対応の新規・決済区分 {entry} です（close-by等は対応関係が必要です）");
            if(watch.ElapsedMilliseconds>=100){progress?.Report(new(Math.Min(.99,(double)rows.Position/Math.Max(1,rows.Length)),$"MT5 {sequence:N0}取引を正規化中"));watch.Restart();}
        }
        token.ThrowIfCancellationRequested();
        if(pending!=null)throw new FormatException("MT5同時決済の約定が不足し、対応を特定できません");
        if(sequence==0)throw new FormatException("MT5約定表に対応付けできる決済取引がありません");
        string warning="MT5約定から再計算。入出金を除外し、新規・決済の手数料/スワップを含みます。";
        if(active.Count>0)warning+=$" 未決済注文{active.Count}件と未配賦の新規費用は統計に含めていません。";
        if(matchedBatches>0)warning+=$" 同時決済{matchedBatches}組は、同時刻・対応数量・価格差/損益の整合性を検証して対応付けしました。";
        return new(instrument,initial,sequence,active.Count,warning){Format="MT5"};
    }
}
