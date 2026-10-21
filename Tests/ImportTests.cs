using ArmadaAnalyzer;
static partial class ImportTests
{
    static string Row(params string[] cells) => "<tr>" + string.Join("", cells.Select(x => "<td>" + x + "</td>")) + "</tr>";
    static string Fixture() => "<html><table>" + Row("通貨ペア", "EURJPY (Test)") + Row("初期証拠金", "1000.00") + "</table><table>" + Row("1", "2020.01.01 00:00", "buy", "1", "0.20", "123.456", "0", "0", "") + Row("2", "2020.01.01 01:00", "close", "1", "0.10", "123.500", "0", "0", "100.12345678", "1100") + Row("3", "2020.01.01 02:00", "close", "1", "0.10", "123.400", "0", "0", "-50", "1050") + "</table></html>";
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArmadaTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "fixture.htm");
        File.WriteAllText(file, Fixture());
        Program.Test("concurrent_positions_merge_partial_closes_and_portfolio_tickets",()=>{
            var w=new Workspace(Path.Combine(root,"positions"));
            var a=w.Import(file,new()).Dataset;
            string second=Path.Combine(root,"positions.htm");
            File.WriteAllText(second,"<table>"+Row("通貨ペア","USDJPY")+
                Row("1","2020.01.01 00:30","sell","1","0.1","150","0","0","")+
                Row("2","2020.01.01 01:30","close","1","0.1","149","0","0","10")+"</table>");
            var b=w.Import(second,new()).Dataset;
            var single=w.LoadTradeDetails(new([a.Id]),2,includePips:false);
            Program.Eq(1,single.Positions.Maximum);Program.Eq(1,single.Positions.LongMaximum);Program.Eq(0,single.Positions.ShortMaximum);
            var both=w.LoadTradeDetails(new([a.Id,b.Id],Weights:new(){[a.Id]=5m,[b.Id]=2m}),3,includePips:false);
            Program.Eq(2,both.Positions.Maximum);Program.Eq(1,both.Positions.LongMaximum);Program.Eq(1,both.Positions.ShortMaximum);
            var empty=w.LoadTradeDetails(new([a.Id],Calendar:new(Minutes:0)),0);
            Program.Eq(0,empty.Positions.Maximum);
        });
        Program.Test("concurrent_positions_match_independent_timestamp_sweep",()=>{
            var w=new Workspace(Path.Combine(root,"positions-random"));
            var rng=new Random(71);var rows=new List<(int Open,int Close,int Side)>();
            for(int i=0;i<300;i++){int start=rng.Next(120);rows.Add((start,start+rng.Next(30),rng.Next(1,3)));}
            var report=new System.Text.StringBuilder("<table>"+Row("通貨ペア","USDJPY"));
            for(int i=0;i<rows.Count;i++){
                var r=rows[i];string ticket=(i+1).ToString();
                report.Append(Row("1",new DateTime(2020,1,1).AddMinutes(r.Open).ToString("yyyy.MM.dd HH:mm"),r.Side==1?"buy":"sell",ticket,"0.1","150","0","0",""));
                report.Append(Row("2",new DateTime(2020,1,1).AddMinutes(r.Close).ToString("yyyy.MM.dd HH:mm"),"close",ticket,"0.1","150","0","0","0"));
            }
            string file=Path.Combine(root,"positions-random.htm");File.WriteAllText(file,report+"</table>");
            var d=w.Import(file,new()).Dataset;var result=w.LoadTradeDetails(new([d.Id]),rows.Count).Positions;
            int Peak(int side)=>Enumerable.Range(0,150).Max(t=>rows.Count(p=>(side==0||p.Side==side)&&p.Open<=t&&(p.Close>t||p.Open==p.Close&&p.Close==t)));
            Program.Eq(Peak(0),result.Maximum);Program.Eq(Peak(1),result.LongMaximum);Program.Eq(Peak(2),result.ShortMaximum);
            var onlyLong=w.LoadTradeDetails(new([d.Id],Side:1),rows.Count(p=>p.Side==1)).Positions;
            Program.Eq(Peak(1),onlyLong.Maximum);Program.Eq(0,onlyLong.ShortMaximum);
        });
        Program.Test("slippage_mixed_symbols_survives_missing_sources",()=>{
            var w=new Workspace(Path.Combine(root,"mixed-slippage"));var ids=new List<long>();
            foreach(var (symbol,entry,exit) in new[]{("USDJPY","150","150.1"),("EURUSD","1.1","1.101"),("XAUUSD","2000","2001")}){
                string f=Path.Combine(root,symbol+"-slippage.htm");
                File.WriteAllText(f,"<table>"+Row("通貨ペア",symbol)+"</table><table>"+Row("1","2020.01.01 00:00","buy","1","0.1",entry,"0","0","")+Row("2","2020.01.01 01:00","close","1","0.1",exit,"0","0","10")+"</table>");
                ids.Add(w.Import(f,new()).Dataset.Id);File.Delete(f);
            }
            var filter=new Filter(ids.ToArray(),Weights:ids.ToDictionary(id=>id,_=>2m));var series=w.LoadSeries(filter);var a=Analytics.Analyze(series,Amount.Parse("1000"));
            Program.Eq<decimal?>(2.5m,w.EstimateSlippage(filter,series.Key,a.Net,a.Count).Pips);
        });
        Program.Test("holding_mixed_symbols_use_individual_pip_units",()=>{
            var w=new Workspace(Path.Combine(root,"mixed-holding"));var ids=new List<long>();
            foreach(var (symbol,entry,exit) in new[]{("USDJPY","150","150.1"),("EURUSD","1.1","1.101"),("XAUUSD","2000","2001"),("UNKNOWN","10","11")}){
                string f=Path.Combine(root,symbol+"-holding.htm");
                File.WriteAllText(f,"<table>"+Row("通貨ペア",symbol)+"</table><table>"+Row("1","2020.01.01 00:00","buy","1","0.1",entry,"0","0","")+Row("2","2020.01.01 01:00","close","1","0.1",exit,"0","0","10")+"</table>");
                ids.Add(w.Import(f,new()).Dataset.Id);
            }
            var scatter=w.LoadHoldingScatter(new(ids.ToArray(),Weights:ids.ToDictionary(id=>id,_=>2m)));
            Program.Eq(4L,scatter.Total);Program.Eq(3L,scatter.PipCount);
            foreach(long id in ids.Take(3)){var p=scatter.Points.Single(p=>p.DatasetId==id);Program.Eq<decimal?>(10m,p.Pips);Program.Eq(Amount.Parse("20"),p.Profit);}
            Program.Eq<decimal?>(null,scatter.Points.Single(p=>p.DatasetId==ids[3]).Pips);
        });
        Program.Test("xau_pips_use_point_one_for_all_metrics_and_raw_cache",()=>{
            var w=new Workspace(Path.Combine(root,"xau"));
            string report=Path.Combine(root,"gold.htm");
            File.WriteAllText(report,"<table>"+Row("通貨ペア","XAUUSD")+Row("初期証拠金","1000")+"</table><table>"+
                Row("1","2020.01.01 00:00","buy","1","0.1","2000","0","0","")+
                Row("2","2020.01.01 01:00","close","1","0.1","2000.6","0","0","10")+
                Row("3","2020.01.02 00:00","sell","2","0.1","2000","0","0","")+
                Row("4","2020.01.02 01:00","close","2","0.1","2001.2","0","0","-20")+"</table>");
            var d=w.Import(report,new()).Dataset;var f=new Filter([d.Id]);
            var first=w.LoadSeries(f);var cached=w.LoadSeries(f);Program.Assert(cached.CacheHit,"raw cache was not reused");
            foreach(var s in new[]{first,cached,w.LoadSeries(f,reusable:cached)})
            {
                Program.Eq(10,s.PipFactors[d.Id]);
                var a=Analytics.Analyze(s,Amount.Parse("1000"));
                Program.Eq<decimal?>(6m,a.Pips.Maximum);Program.Eq<decimal?>(-12m,a.Pips.Minimum);
                Program.Eq<decimal?>(-6m,a.Pips.Total);Program.Eq<decimal?>(-3m,a.Pips.Average);
                Program.Eq<decimal?>(12m,a.Pips.Recovery!.Maximum);
                Program.Eq(Amount.Parse("-10"),a.Net);
            }
        });
        Program.Test("streaming_multiline_partial_and_precision", () => { var w = new Workspace(Path.Combine(root, "db")); var r = w.Import(file, new("JPY")); Program.Eq(2L, r.Dataset.Count); var trades = w.ReadPage(new([r.Dataset.Id]), 0); Program.Eq(2, trades.Length); Program.Eq(Amount.Parse("100.12345678"), trades[0].Profit); Program.Eq(1, trades[1].Part); Program.Eq("EURJPY", r.Dataset.Symbol); Program.Eq(Amount.Parse("1000"), r.Dataset.InitialBalance); });
        Program.Test("report_summary_and_balance_are_not_analysis_inputs", () =>
        {
            var w = new Workspace(Path.Combine(root, "summary-independent"));
            foreach (string fake in new[] { "999999999", "not-a-statistic" })
            {
                string report = Path.Combine(root, "summary-" + fake + ".htm");
                string summary = Row("総純益", fake, "Total net profit", fake)
                    + Row("勝率", fake, "Profit trades", fake)
                    + Row("Profit factor", fake, "Maximal drawdown", fake);
                File.WriteAllText(report, Fixture().Replace("<html><table>", "<html><table>" + summary)
                    .Replace("<td>1100</td>", "<td>999999999</td>").Replace("<td>1050</td>", "<td>-999999999</td>"));
                var d = w.Import(report, new()).Dataset;
                var snapshot = w.LoadSeries(new([d.Id]));
                var a = Analytics.Analyze(snapshot.Points, Amount.Parse("1000"));
                Program.Eq(2, a.Count); Program.Eq(Amount.Parse("50.12345678"), a.Net);
                Program.Eq(Amount.Parse("100.12345678"), a.GrossProfit); Program.Eq(Amount.Parse("-50"), a.GrossLoss);
                Program.Eq(1, a.Wins); Program.Eq(1, a.Losses); Program.Eq(50.0, 100.0 * a.Wins / a.Count);
                Program.Eq(Amount.Parse("50"), a.Drawdown);
                Program.Assert(Math.Abs(a.ProfitFactor!.Value - 2.0024691356) < 1e-12, "PF was not recalculated");
                var filtered = Analytics.Analyze(w.LoadSeries(new([d.Id], From: new DateTime(2020, 1, 1, 2, 0, 0).Ticks)).Points, Amount.Parse("1000"));
                Program.Eq(1, filtered.Count); Program.Eq(Amount.Parse("-50"), filtered.Net); Program.Eq(0, filtered.Wins);
            }
        });
        Program.Test("summary_only_report_cannot_supply_trade_statistics", () =>
        {
            var w = new Workspace(Path.Combine(root, "summary-only"));
            string report = Path.Combine(root, "summary-only.htm");
            File.WriteAllText(report, "<table>" + Row("総純益", "12345", "勝率", "99%") + "</table>");
            Program.Throws<FormatException>(() => w.Import(report, new()));
            Program.Eq(0, w.ListDatasets().Length);
        });
        Program.Test("details_use_all_rows_for_pips_and_duration_not_chart_sample", () => {
            var w=new Workspace(Path.Combine(root,"details")); var d=w.Import(file,new()).Dataset;
            var q=new Filter([d.Id]); var a=w.LoadTradeDetails(q,2);
            Program.Eq((decimal)TimeSpan.TicksPerHour*3,a.TotalDuration); Program.Eq<decimal?>((decimal)TimeSpan.TicksPerMinute*90,a.MedianDuration);
            Program.Eq<decimal?>(-1.2m,a.TotalPips); Program.Eq<decimal?>(4.4m,a.WinPips); Program.Eq<decimal?>(-5.6m,a.LossPips);
            using var c=new CancellationTokenSource(); c.Cancel(); Program.Throws<OperationCanceledException>(()=>w.LoadTradeDetails(q,2,c.Token));
            Program.Throws<InvalidOperationException>(()=>w.LoadTradeDetails(q,1));
            Program.Eq<decimal?>(null,w.LoadTradeDetails(q with {Calendar=new(Minutes:0)},0).TotalPips);
            string unknown=Path.Combine(root,"unknown-details.htm"); File.WriteAllText(unknown,Fixture().Replace("EURJPY (Test)","UNKNOWN"));
            var u=w.Import(unknown,new()).Dataset; Program.Eq<decimal?>(null,w.LoadTradeDetails(new([u.Id]),2).TotalPips);
            string even=Path.Combine(root,"even-details.htm"); var report="<table>"+Row("通貨ペア","EURUSD"); int ticket=0;
            foreach(int minutes in new[]{90,1,60,20,20,120}) { ticket++; report+=Row("1","2020.01.01 00:00","buy",ticket.ToString(),"0.1","1","0","0","")+Row("2",new DateTime(2020,1,1).AddMinutes(minutes).ToString("yyyy.MM.dd HH:mm"),"close",ticket.ToString(),"0.1","1.001","0","0","10"); }
            File.WriteAllText(even,report+"</table>"); var e=w.Import(even,new()).Dataset; Program.Eq<decimal?>((decimal)TimeSpan.TicksPerMinute*40,w.LoadTradeDetails(new([e.Id]),6).MedianDuration);
        });
        Program.Test("normal_analysis_pips_survive_cache_and_match_full_detail", () => {
            var w=new Workspace(Path.Combine(root,"automatic-pips")); var d=w.Import(file,new()).Dataset; var q=new Filter([d.Id]);
            var first=w.LoadSeries(q); var a=Analytics.Analyze(first,Amount.Parse("1000"));
            Program.Eq<decimal?>(-1.2m,a.Pips.Total); Program.Eq<decimal?>(-0.6m,a.Pips.Average);
            Program.Eq<decimal?>(4.4m,a.Pips.Maximum); Program.Eq<decimal?>(-5.6m,a.Pips.Minimum);
            Program.Eq<decimal?>(4.4m,a.Pips.WinningAverage); Program.Eq<decimal?>(-5.6m,a.Pips.LosingAverage);
            var hit=w.LoadSeries(q); Program.Assert(hit.CacheHit,"pip cache not hit");
            Program.Eq(a.Pips,Analytics.Analyze(hit,Amount.Parse("1000")).Pips);
            var filtered=w.LoadSeries(q with {From=new DateTime(2020,1,1,2,0,0).Ticks});
            Program.Eq<decimal?>(-5.6m,Analytics.Analyze(filtered,Amount.Parse("1000")).Pips.Average);
            var weighted=w.LoadSeries(q with {Weights=new(){[d.Id]=2m}});
            Program.Eq(a.Pips,Analytics.Analyze(weighted,Amount.Parse("1000")).Pips);
            Program.Eq(a.Pips.Total,w.LoadTradeDetails(q,2).TotalPips);
            var durationOnly=w.LoadTradeDetails(q,2,includePips:false);
            Program.Eq(w.LoadTradeDetails(q,2).MedianDuration,durationOnly.MedianDuration); Program.Eq<decimal?>(null,durationOnly.TotalPips);
        });
        Program.Test("entry_calendar_filters_open_time_in_all_read_paths", () => { var w = new Workspace(Path.Combine(root, "calendar")); string f = Path.Combine(root, "calendar.htm"); File.WriteAllText(f, "<table>" + Row("1", "2020.02.29 23:58", "buy", "42", "0.1", "1", "0", "0", "") + Row("2", "2020.03.01 00:02", "close", "42", "0.1", "1.1", "0", "0", "10") + "</table>"); var d = w.Import(f, new("JPY")).Dataset; var q = new Filter([d.Id], Calendar: new(Months: 2, Days: 1 << 28, Weekdays: 1 << 6, Hours: 1 << 23, Minutes: 1L << 58)); Program.Eq(1, w.LoadSeries(q).Points.Length); Program.Eq(1, w.ReadPage(q, 0).Length); string csv = Path.Combine(root, "calendar.csv"); w.ExportCsv(q, csv); Program.Eq(2, File.ReadLines(csv).Count()); var none = q with { Calendar = q.Calendar! with { Minutes = 0 } }; Program.Eq(0, w.LoadSeries(none).Points.Length); Program.Eq(0, w.ReadPage(none, 0).Length); Program.Assert(w.LoadSeries(none).Key != w.LoadSeries(q).Key, "calendar cache identity collided"); Program.Throws<ArgumentException>(() => w.LoadSeries(q with { Calendar = new(Months: 4096) })); });
        Program.Test("holding_scatter_uses_duration_direction_and_bounded_storage", () => { var w = new Workspace(Path.Combine(root, "db")); var d = w.ListDatasets().Single(); var q = new Filter([d.Id]); var scatter = w.LoadHoldingScatter(q, 1); Program.Eq(2L, scatter.Total); Program.Assert(scatter.Points.Length <= 9, "unbounded scatter"); Program.Assert(scatter.Points.Any(p => p.Duration == TimeSpan.TicksPerHour && p.PriceMove == Amount.Parse("0.044")), "first close data mismatch"); Program.Assert(scatter.Points.Any(p => p.Duration == TimeSpan.TicksPerHour * 2 && p.PriceMove == Amount.Parse("-0.056")), "partial close data mismatch"); Program.Eq(0L, w.LoadHoldingScatter(q with { Calendar = new(Minutes: 0) }).Total); using var c = new CancellationTokenSource(); c.Cancel(); Program.Throws<OperationCanceledException>(() => w.LoadHoldingScatter(q, token: c.Token)); });
        Program.Test("holding_scatter_sell_direction_and_fixed_weight", () => { var w = new Workspace(Path.Combine(root, "sell-scatter")); string f = Path.Combine(root, "sell.htm"); File.WriteAllText(f, "<table>" + Row("1", "2020.01.01 00:00", "sell", "1", "0.1", "1.1", "0", "0", "") + Row("2", "2020.01.01 00:30", "close", "1", "0.1", "1.0", "0", "0", "10") + "</table>"); var d = w.Import(f, new("JPY")).Dataset; var result = w.LoadHoldingScatter(new([d.Id], Weights: new() { [d.Id] = 2m })); Program.Eq(Amount.Parse("0.1"), result.Points[0].PriceMove); Program.Eq(Amount.Parse("20"), result.Points[0].Profit); });
        Program.Test("portfolio_combines_amounts_across_currency_metadata_and_disjoint_periods", () => { var w = new Workspace(Path.Combine(root, "amount-only")); var a = w.Import(file, new("JPY")).Dataset; string other = Path.Combine(root, "later.htm"); File.WriteAllText(other, Fixture().Replace("2020.01.01", "2022.01.01")); var b = w.Import(other, new("USD", TimeBasis: "another broker")).Dataset; var q = w.LoadSeries(new([a.Id, b.Id])); Program.Eq(4, q.Points.Length); Program.Eq(new DateTime(2020, 1, 1, 1, 0, 0).Ticks, q.Points[0].Ticks); Program.Eq(new DateTime(2022, 1, 1, 2, 0, 0).Ticks, q.Points[^1].Ticks); Program.Eq(Amount.Parse("100.24691356"), Analytics.Analyze(q.Points, Amount.Parse("1000")).Net); var unknown = w.Import(file, new(" ", TimeBasis: "third")).Dataset; Program.Eq(6, w.LoadSeries(new([a.Id, b.Id, unknown.Id])).Points.Length); });
        Program.Test("memory_series_reuse_checks_identity_and_budget", () => { var w = new Workspace(Path.Combine(root, "reuse")); var d = w.Import(file, new()).Dataset; var f = new Filter([d.Id]); var a = w.LoadSeries(f); var b = w.LoadSeries(f, reusable: a); Program.Assert(ReferenceEquals(a.Points,b.Points), "unchanged series copied"); var weighted = w.LoadSeries(new([d.Id], Weights:new(){[d.Id]=2m}), reusable:a); Program.Assert(weighted.Key!=a.Key && !ReferenceEquals(a.Points,weighted.Points), "weights reused stale data"); w.SetCurrency(d.Id,"legacy"); Program.Assert(w.LoadSeries(f,reusable:a).Key!=a.Key,"version reused stale data"); var oversized = w.LoadSeries(f) with { Points = new SeriesPoint[1000000] }; Program.Throws<InvalidOperationException>(()=>w.LoadSeries(f,64,reusable:oversized)); });
        Program.Test("deduplicate_and_cache_corruption_recovery", () => { var w = new Workspace(Path.Combine(root, "db")); var r = w.Import(file, new("JPY")); Program.Assert(r.AlreadyImported, "duplicate import"); var q = new Filter([r.Dataset.Id]); var a = w.LoadSeries(q); var b = w.LoadSeries(q); Program.Assert(b.CacheHit, "cache miss"); Program.Eq(2, a.Points.Length); foreach (string f in Directory.GetFiles(Path.Combine(w.Root, "cache"), "*.col")) File.WriteAllText(f, "corrupt"); var c = w.LoadSeries(q); Program.Assert(!c.CacheHit, "corrupt cache accepted"); Program.Eq(a.Points[1], c.Points[1]); w.SetCurrency(r.Dataset.Id, "USD"); Program.Assert(w.LoadSeries(q).Key != a.Key, "metadata version not invalidated"); });
        Program.Test("import_cancel_rolls_back", () => { var w = new Workspace(Path.Combine(root, "cancel")); using var c = new CancellationTokenSource(); c.Cancel(); Program.Throws<OperationCanceledException>(() => w.Import(file, new(), c.Token)); Program.Eq(0, w.ListDatasets().Length); });
        Program.Test("malformed_close_rejected_atomically", () => { var w = new Workspace(Path.Combine(root, "broken")); string f = Path.Combine(root, "broken.htm"); File.WriteAllText(f, "<table>" + Row("1", "2020.01.01 00:00", "close", "999", "0.1", "1", "0", "0", "10", "1010") + "</table>"); Program.Throws<FormatException>(() => w.Import(f, new())); Program.Eq(0, w.ListDatasets().Length); });
        Program.Test("truncated_or_unsupported_settlement_rejected", () => { foreach (string kind in new[] { "close", "close by" }) { var w = new Workspace(Path.Combine(root, "truncated-" + kind.Replace(' ', '_'))); string f = Path.Combine(root, "truncated.htm"); var content = Fixture(); content = kind == "close" ? content.Replace("<td>-50</td><td>1050</td>", "") : content.Replace("<td>-50</td>", "<td>-50</td>").Replace("2020.01.01 02:00</td><td>close", "2020.01.01 02:00</td><td>close by"); File.WriteAllText(f, content); Program.Throws<FormatException>(() => w.Import(f, new())); Program.Eq(0, w.ListDatasets().Length); } });
        Program.Test("cancel_mid_import_rolls_back", () => { var w = new Workspace(Path.Combine(root, "cancel2")); using var c = new CancellationTokenSource(); var progress = new CallbackProgress(p => { if (p.Message.Contains("正規化中")) c.Cancel(); }); Program.Throws<OperationCanceledException>(() => w.Import(file, new(), c.Token, progress)); Program.Eq(0, w.ListDatasets().Length); });
        Program.Test("cp932_after_long_ascii_prefix", () => { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); var w = new Workspace(Path.Combine(root, "cp932")); string f = Path.Combine(root, "cp932.htm"); File.WriteAllText(f, new string(' ', 12000) + Fixture(), System.Text.Encoding.GetEncoding(932)); var r = w.Import(f, new("JPY")); Program.Eq("EURJPY", r.Dataset.Symbol); Program.Eq(2L, r.Dataset.Count); });
    }
    sealed class CallbackProgress(Action<WorkProgress> callback) : IProgress<WorkProgress>
    {
        public void Report(WorkProgress p) => callback(p);
    }
}
