using ArmadaAnalyzer;
static class SlippageTests
{
    static Trade T(string move,string gross,string net,string lots="1")=>new Trade(1,1,0,1,2,Amount.Parse(lots),Amount.Parse("100"),Amount.Parse("100")+Amount.Parse(move),Amount.Parse(net),1,1,1){PriceProfit=Amount.Parse(gross)};
    public static void Run()
    {
        Program.Test("slippage_varying_lots_costs_weights",()=>{
            var a=new SlippageAccumulator();a.Add(T("1","100","95"),.1m);a.Add(T("-1","-200","-210","2"),.1m,.25m);
            Program.Eq(Amount.Parse("42.5"),(long)a.Net);Program.Eq<decimal?>(Amount.Parse("15"),a.CostPerPip);
            Program.Eq<decimal?>(42.5m*.25m/15m,SlippageAccumulator.Finish(a.Net,a.Count,a.CostPerPip).Pips);
        });
        Program.Test("slippage_zero_price_calibration_and_missing",()=>{
            var a=new SlippageAccumulator();a.Add(T("1","100","100"),.1m);a.Add(T("0","0","-2","2"),.1m);
            Program.Eq<decimal?>(Amount.Parse("30"),a.CostPerPip);
            var b=new SlippageAccumulator();b.Add(T("0","0","0"),.1m);Program.Eq<decimal?>(null,b.CostPerPip);
            Program.Eq<decimal?>(null,SlippageAccumulator.Finish(0,1,1).Pips);
            Program.Eq<decimal?>(null,SlippageAccumulator.Finish(-1,1,1).Pips);
        });
    }
}
