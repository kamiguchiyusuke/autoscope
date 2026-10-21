using MT4BatchReporter;
using System.Text;
internal static class ReportInspectorChecks
{
    public static void Run()
    {
        string folder=Path.Combine(Path.GetTempPath(),"ReportInspector",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var job=new TestJob { From=new(2026,8,3),To=new(2026,8,5) };
        string Write(string bars="100",string ticks="2500",string trades="0",string dates="2026.08.03 00:00 - 2026.08.04 23:59",bool japanese=false)
        {
            string path=Path.Combine(folder,"native.htm");
            File.WriteAllText(path,$"<html><meta name='server' content='Demo-A'><table><tr><td>{(japanese?"期間":"Period")}</td><td>M15 {dates} (2026.08.03 - 2026.08.05)</td></tr><tr><td>{(japanese?"テストバー数":"Bars in test")}</td><td>{bars}</td><td>{(japanese?"モデルティック数":"Ticks modelled")}</td><td>{ticks}</td></tr><tr><td>{(japanese?"総取引数":"Total trades")}</td><td>{trades}</td><td>{(japanese?"純益":"Total net profit")}</td><td>12.50</td></tr></table><img src='report.gif'></html>",Encoding.GetEncoding(932));return path;
        }
        void Expect(bool v,string name){if(!v)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);}
        var zeroTrades=ReportInspector.Inspect(Write(),job,"Demo-A");
        Expect(zeroTrades.Outcome==ReportOutcome.Valid&&zeroTrades.Trades==0&&zeroTrades.Profit==12.50m,"data processed with zero trades is valid");
        Expect(ReportInspector.Inspect(Write(bars:"0"),job,"Demo-A").Outcome==ReportOutcome.Invalid,"zero bars cannot be successful");
        Expect(ReportInspector.Inspect(Write(ticks:"0"),job,"Demo-A").Outcome==ReportOutcome.Invalid,"zero ticks cannot be successful");
        Expect(ReportInspector.Inspect(Write(dates:"1970.01.01 00:00 - 1970.01.01 00:00"),job,"Demo-A").Outcome==ReportOutcome.Invalid,"requested range cannot disguise empty actual dates");
        Expect(ReportInspector.Inspect(Write(dates:"2025.08.03 00:00 - 2025.08.04 23:59"),job,"Demo-A").Outcome==ReportOutcome.Invalid,"wrong actual date range is invalid");
        Expect(ReportInspector.Inspect(Write(japanese:true),job,"Demo-A").Outcome==ReportOutcome.Valid,"CP932 Japanese native report is parsed");
        Expect(ReportInspector.Inspect(Write(),job,"Demo-B").Warnings.Any(w=>w.Contains("Demo-A")),"actual server mismatch is visible");
        Expect(ReportInspector.Inspect(Write(bars:"?"),job,"Demo-A").Outcome==ReportOutcome.Unknown,"missing numbers remain unknown");
        File.WriteAllText(Path.Combine(folder,"broken.htm"),"<html>");
        Expect(ReportInspector.Inspect(Path.Combine(folder,"broken.htm"),job,"").Outcome==ReportOutcome.Unknown,"broken report is not successful");
        File.WriteAllBytes(Path.Combine(folder,"report.gif"),Encoding.ASCII.GetBytes("NOTGIF"));
        bool rejected=false;try{BatchRunner.CollectNativeReport(Write(),Path.Combine(folder,"copy"));}catch(InvalidDataException){rejected=true;}
        Expect(rejected,"corrupt native GIF cannot count as saved");
        Directory.Delete(folder,true);
    }
}
