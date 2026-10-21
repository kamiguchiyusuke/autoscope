using ArmadaAnalyzer;
static class BacktestPeriodTests
{
    public static void Run()
    {
        Program.Test("report_header_origin_and_tds_summary",()=>{
            var mt4=new ReportMetadata(1,"a",new(){{"形式","MT4"},{"スプレッド","15"},{"モデリング品質","90.00%"}});
            var mt5=new ReportMetadata(2,"b",new(){{"形式","MT5"},{"スプレッド","20"},{"履歴品質","100%"},{"TDS","使用"}});
            var single=ReportHeaderSummary.Create(["USDJPY","USDJPY"],[mt4]);Program.Eq("USDJPY",single.Currency);Program.Eq("MT4",single.Platform);Program.Eq("15",single.Spread);Program.Eq("90.00%",single.Quality);
            var mixed=ReportHeaderSummary.Create(["USDJPY","EURUSD"],[mt4,mt5]);Program.Eq("複数",mixed.Currency);Program.Eq("混同",mixed.Platform);
            var tds=ReportHeaderSummary.Create(["USDJPY_TDS_UTC"],[mt5]);Program.Eq("変動",tds.Spread);Program.Eq("99.90%",tds.Quality);
            Program.Eq("100%",mt5.Fields["履歴品質"]);
            var cached=new ReportMetadata(3,"c",new(){{"形式","MT5"},{"通貨","USDJPY_TDS_UTC"},{"履歴品質","100%"}});
            Program.Eq("99.90%",ReportHeaderSummary.Create(["USDJPY_TDS_UTC"],[cached]).Quality);
        });
        Program.Test("report_format_without_quality_and_explicit_tds_origin_persist",()=>{
            string root=Path.Combine(Path.GetTempPath(),"scope-origin-"+Guid.NewGuid().ToString("N"));var w=new Workspace(root);string file=DemoReports.Create(root,10);
            var d=w.Import(file,new()).Dataset;Program.Eq("MT4",w.LoadReportMetadata(new([d.Id]))[0].Fields["形式"]);
            var before=w.LoadSeries(new([d.Id]));w.SetReportOrigin(d.Id,"MT4",true);File.Delete(file);
            var metadata=w.LoadReportMetadata(new([d.Id]));Program.Eq("使用",metadata[0].Fields["TDS"]);Program.Eq(before.Points.Length,w.LoadSeries(new([d.Id])).Points.Length);
            Program.Eq("変動",ReportHeaderSummary.Create([d.Symbol],metadata).Spread);
        });
        Program.Test("report_metadata_is_exact_persisted_and_missing_not_invented",()=>{
            string root=Path.Combine(Path.GetTempPath(),"scope-metadata-"+Guid.NewGuid().ToString("N"));var w=new Workspace(root);string file=DemoReports.Create(root,10);
            File.WriteAllText(file,File.ReadAllText(file).Replace("<table>","<table><tr><td>Spread:</td><td>Current</td><td>Modeling quality:</td><td>99.90%</td></tr>"));
            var d=w.Import(file,new()).Dataset;File.Delete(file);var fields=w.LoadReportMetadata(new([d.Id]))[0].Fields;
            Program.Eq("Current",fields["スプレッド"]);Program.Eq("99.90%",fields["モデリング品質"]);Program.Eq("MT4",fields["形式"]);Program.Assert(!fields.ContainsKey("ロット"),"do not infer lot");
        });
        Program.Test("portfolio_display_period_spans_report_headers_and_disjoint_eas",()=>{
            string root=Path.Combine(Path.GetTempPath(),"scope-portfolio-period-"+Guid.NewGuid().ToString("N"));
            var w=new Workspace(root);string file=DemoReports.Create(root,10);string original=File.ReadAllText(file);
            File.WriteAllText(file,original.Replace("<table>","<table><tr><td>Period:</td><td>H1 (2000.01.01 - 2005.01.01)</td></tr>"));var a=w.Import(file,new()).Dataset;
            File.WriteAllText(file,original.Replace("<table>","<table><tr><td>Period:</td><td>H1 (2010.01.01 - 2025.01.01)</td></tr>"));var b=w.Import(file,new()).Dataset;
            Program.Eq(BacktestPeriod.Parse("2000.01.01 - 2025.01.01"),w.DisplayPeriod(new([a.Id,b.Id])));
            Program.Eq(BacktestPeriod.Parse("2010.01.01 - 2025.01.01"),w.DisplayPeriod(new([b.Id])));
        });
        Program.Test("period_report_header_overrides_trade_fallback",()=>{
            string root=Path.Combine(Path.GetTempPath(),"scope-header-"+Guid.NewGuid().ToString("N"));
            string file=DemoReports.Create(root,10);
            File.WriteAllText(file,File.ReadAllText(file).Replace("<table>","<table><tr><td>Period:</td><td>H1 (2010.01.01 - 2025.01.01)</td></tr>"));
            var w=new Workspace(root);var d=w.Import(file,new()).Dataset;
            Program.Eq(BacktestPeriod.Parse("2010.01.01 - 2025.01.01"),w.DisplayPeriod(new([d.Id])));
        });
        Program.Test("period_import_fallback_persists_and_deletes_with_dataset",()=>{
            string root=Path.Combine(Path.GetTempPath(),"scope-period-"+Guid.NewGuid().ToString("N"));
            var w=new Workspace(root);string file=DemoReports.Create(root,100);
            var dataset=w.Import(file,new()).Dataset;var filter=new Filter([dataset.Id]);
            var rows=w.ReadPage(filter,0);
            var actual=w.DisplayPeriod(filter)!;
            Program.Eq(rows.Min(t=>t.OpenTicks),actual.Start);Program.Eq(rows.Max(t=>t.CloseTicks),actual.End);
            Program.Eq(actual,new Workspace(root).DisplayPeriod(filter));
            Program.Assert(w.Import(file,new()).AlreadyImported,"duplicate remains duplicate");
            Program.Eq(actual,w.DisplayPeriod(filter));
            w.DeleteDatasets([dataset.Id]);Program.Eq<BacktestPeriod?>(null,w.DisplayPeriod(filter));
        });
        Program.Test("period_metadata_covers_all_selected_eas_and_explicit_range",()=>{
            var a=BacktestPeriod.Parse("H1 (2000.01.01 - 2010.01.01)")!;
            var b=BacktestPeriod.Parse("M5 (2005.01.01 00:00 - 2015.01.01 00:00)")!;
            var common=BacktestPeriod.Covering([a,b])!;
            Program.Eq(a.Start,common.Start);Program.Eq(b.End,common.End);
            Program.Eq<BacktestPeriod?>(null,BacktestPeriod.Covering([a,null]));
            Program.Eq<BacktestPeriod?>(new(a.Start,b.End+1),BacktestPeriod.Covering([a,new(b.End,b.End+1)]));
            Program.Eq<BacktestPeriod?>(new(a.Start,b.End),BacktestPeriod.Covering([null],a.Start,b.End));
            Program.Eq<BacktestPeriod?>(null,BacktestPeriod.Parse("2010.01.01 - 2000.01.01"));
            Program.Eq<BacktestPeriod?>(null,BacktestPeriod.Parse("no dates"));
            var five=new BacktestPeriod(0,(long)(5.36*365.2425*TimeSpan.TicksPerDay));
            Program.Eq("（テスト期間 5.36年）",five.Label);
        });
    }
}
