using ArmadaAnalyzer;
using System.Text;
static class Mt5ImportTests
{
    static string Row(params string[] cells) => "<tr>"+string.Join("",cells.Select(c=>"<td>"+c+"</td>"))+"</tr>";
    static string Deal(int id,string type,string direction,string volume,string price,string commission,string swap,string profit,string symbol="XAUUSDp") =>
        Row($"2024.01.01 00:{id:00}:00",id.ToString(),symbol,type,direction,volume,price,(id+100).ToString(),commission,swap,profit,"999 999.00","");
    static string Report(string rows,bool japanese=true) => "<html><table>"+Row("銘柄:","XAUUSDp")+Row("初期証拠金:","10 000.00")+Row("総純益:","999999")+
        Row("注文")+Row("2024.01.01 00:01:00","100","XAUUSDp","buy","0.1 / 0.1","0","0","0","2024.01.01 00:01:00","filled","")+
        Row(japanese?"約定":"Deals")+(japanese?Row("時間","約定","銘柄","タイプ","新規・決済","数量","価格","注文","手数料","スワップ","損益","残高","コメント"):
        Row("Time","Deal","Symbol","Type","Direction","Volume","Price","Order","Commission","Swap","Profit","Balance","Comment"))+rows+Row("","-999","0","999999","999999","")+"</table></html>";
    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"ScopeMT5-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        (Workspace w,Dataset d) Import(string body,bool japanese=true,ImportOptions? options=null)
        {
            string id=Guid.NewGuid().ToString("N"),path=Path.Combine(root,id+".html");File.WriteAllText(path,Report(body,japanese),Encoding.Unicode);
            var w=new Workspace(Path.Combine(root,id));return(w,w.Import(path,options??new()).Dataset);
        }
        Program.Test("mt5_utf16_deals_only_entry_and_exit_costs",()=>{
            var(w,d)=Import(Deal(1,"balance","","","","0","0","10 000","")+Deal(2,"buy","in","0.1","2000","-1","0","0")+Deal(3,"sell","out","0.1","2000.6","-2","-0.5","6"));
            Program.Eq(1L,d.Count);Program.Eq("XAUUSDp",d.Symbol);Program.Eq(Amount.Parse("10000"),d.InitialBalance);
            var t=w.ReadPage(new([d.Id]),0).Single();Program.Eq(Amount.Parse("2.5"),t.Profit);Program.Eq(1,t.Side);Program.Eq(2L,t.Ticket);
            var a=Analytics.Analyze(w.LoadSeries(new([d.Id])),d.InitialBalance);Program.Eq<decimal?>(6,a.Pips.Total);
            var series=w.LoadSeries(new([d.Id]));
            File.Delete(Path.Combine(root,Path.GetFileName(w.Root)+".html"));
            Program.Eq<decimal?>(0.625m,w.EstimateSlippage(new([d.Id]),series.Key,a.Net,a.Count).Pips);
        });
        Program.Test("mt5_partial_closes_preserve_entry_cost_remainder",()=>{
            var(w,d)=Import(Deal(1,"buy","in","0.3","2000","-1","0","0")+Deal(2,"sell","out","0.1","2001","0","0","10")+Deal(3,"sell","out","0.2","2002","0","0","40"),false);
            var t=w.ReadPage(new([d.Id]),0);Program.Eq(2,t.Length);Program.Eq(0,t[0].Part);Program.Eq(1,t[1].Part);Program.Eq(Amount.Parse("49"),t.Sum(x=>x.Profit));
        });
        Program.Test("mt5_reverse_closes_old_then_opens_remainder",()=>{
            var(w,d)=Import(Deal(1,"buy","in","0.1","2000","-1","0","0")+Deal(2,"sell","in/out","0.3","2001","-3","-1","10")+Deal(3,"buy","out","0.2","2000","0","0","20"));
            var t=w.ReadPage(new([d.Id]),0);Program.Eq(2,t.Length);Program.Eq(Amount.Parse("7"),t[0].Profit);Program.Eq(Amount.Parse("18"),t[1].Profit);Program.Eq(2,t[1].Side);
        });
        Program.Test("mt5_open_orders_cash_and_initial_override",()=>{
            var(w,d)=Import(Deal(1,"balance","","","","0","0","10000","")+Deal(2,"sell","in","0.1","2000","0","0","0")+Deal(3,"buy","out","0.1","2001","0","0","-10")+Deal(4,"balance","","","","0","0","-500","")+Deal(5,"buy","in","0.1","2000","-2","0","0"),options:new(InitialBalance:Amount.Parse("1000000")));
            Program.Eq(1L,d.Count);Program.Eq(Amount.Parse("1000000"),d.InitialBalance);Program.Assert(d.Warning.Contains("未決済"),"missing open warning");Program.Eq(Amount.Parse("-10"),w.ReadPage(new([d.Id]),0)[0].Profit);
        });
        Program.Test("mt5_simultaneous_closes_match_prices_and_gross_profit_not_fifo",()=>{
            var(w,d)=Import(Deal(1,"buy","in","0.1","2000","-1","0","0")+Deal(2,"buy","in","0.1","2001","-2","0","0")+
                Deal(3,"sell","out","0.1","2002","0","0","10")+Deal(4,"sell","out","0.1","2002.01","0","0","20.1").Replace("00:04:00","00:03:00"));
            var t=w.ReadPage(new([d.Id]),0);Program.Eq(2,t.Length);Program.Eq(2L,t[0].Ticket);Program.Eq(1L,t[1].Ticket);Program.Eq(Amount.Parse("8"),t[0].Profit);Program.Eq(Amount.Parse("19.1"),t[1].Profit);
        });
        foreach(var ending in new[]{
            Deal(3,"buy","in","0.1","2000","0","0","0")+Deal(4,"buy","in","0.1","2000","0","0","0")+Deal(5,"sell","out","0.1","2001","0","0","10")+Deal(6,"sell","out","0.1","2001","0","0","10").Replace("00:06:00","00:05:00"),
            Deal(3,"buy","in","0.1","2000","0","0","0")+Deal(4,"buy","in","0.1","2001","0","0","0")+Deal(5,"sell","out","0.1","2002","0","0","10")+Deal(6,"sell","out","0.1","2002","0","0","999").Replace("00:06:00","00:05:00"),
            Deal(3,"buy","in","0.1","2000","0","0","0").Replace("2024.01.01 00:03:00",""),
            Row("","3","XAUUSDp","sell","out","0.1","2000","3","0","0")
        }) Program.Test("mt5_late_ambiguous_or_malformed_row_rolls_back_valid_trades",()=>{
            string id=Guid.NewGuid().ToString("N"),path=Path.Combine(root,id+".html");
            File.WriteAllText(path,Report(Deal(1,"buy","in","0.1","2000","0","0","0")+Deal(2,"sell","out","0.1","2001","0","0","10")+ending));
            var w=new Workspace(Path.Combine(root,id));Program.Throws<FormatException>(()=>w.Import(path,new()));Program.Eq(0,w.ListDatasets().Length);
        });
        foreach(var body in new[]{
            Deal(1,"buy","in","0.1","2000","0","0","0")+Deal(2,"buy","in","0.1","2000","0","0","0"),
            Deal(1,"sell","out","0.1","2000","0","0","10"),
            Deal(1,"buy","in","0.1","2000","0","0","0")+Deal(2,"sell","out","0.2","2000","0","0","10"),
            Deal(1,"buy","in","0.1","2000","0","0","0")+Deal(2,"buy","out","0.1","2000","0","0","10"),
            Deal(1,"buy","in","0.1","2000","0","0","0")+Deal(2,"sell","out","0.1","2000","0","0","10","EURUSD"),
            Deal(1,"buy","in","0.1","2000","0","0","0")+Deal(2,"sell","out","0.1","2000","0","0","10")+Deal(3,"commission","","","","0","0","-2","")
        }) Program.Test("mt5_ambiguous_or_incomplete_history_rolls_back",()=>{
            string id=Guid.NewGuid().ToString("N"),path=Path.Combine(root,id+".html");File.WriteAllText(path,Report(body));var w=new Workspace(Path.Combine(root,id));
            Program.Throws<FormatException>(()=>w.Import(path,new()));Program.Eq(0,w.ListDatasets().Length);
        });
        Program.Test("mt5_gold_report_aliases_preserve_pip_unit",()=>{Program.Eq<decimal?>(0.1m,DukascopyPips.Size("XAUUSDp"));Program.Eq<decimal?>(0.1m,DukascopyPips.Size("XAUUSD+"));});
    }
}
