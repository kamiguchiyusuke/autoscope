using MT4BatchReporter;
using System.Text;
internal static class Mt5Checks
{
    public static async Task Run()
    {
        void Check(bool ok,string name){if(!ok)throw new Exception("MT5 FAIL: "+name);Console.WriteLine("PASS MT5 "+name);}
        var tdsJob=new TestJob{Symbol="USDJPY.raw",Model=0};
        var effective=Mt5Tds.EffectiveJob(tdsJob,"USDJPY_TDS_UTC");
        Check(effective.Model==4&&effective.Symbol=="USDJPY_TDS_UTC"&&tdsJob.Model==0&&tdsJob.Symbol=="USDJPY.raw","TDS selects custom symbol and real ticks without changing job");
        bool bad=false;try{Mt5Tds.EffectiveJob(tdsJob,"USDJPY.raw");}catch(InvalidDataException){bad=true;}
        Check(bad,"TDS rejects broker symbol as imported target");
        string root=Path.Combine(Path.GetTempPath(),"ArmadaMt5Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string experts=Path.Combine(root,"MQL5","Experts");Directory.CreateDirectory(experts);
        string exe=Path.Combine(root,"terminal64.exe"),ea=Path.Combine(experts,"日本語EA.ex5");File.WriteAllText(exe,"");File.WriteAllText(ea,"");
        var job=new TestJob{Name="MT5検証",EaFile=ea,Symbol="USDJPY.raw",Period="H1",Model=4,From=new(2026,9,1),To=new(2026,9,8)};
        var s=new BatchSettings{TerminalFile=exe,DataFolder=root,Portable=true,UseCurrentServer=true,OutputFolder=Path.Combine(root,"output"),Jobs=[job],RetryCount=0};
        JobFiles.Validate(s);Check(s.IsMt5,"detect terminal64 and validate ex5");
        var discovered=Mt4Installations.Discover(Path.Combine(root,"absent"),[Path.GetDirectoryName(root)!]);Check(discovered.Any(x=>x.TerminalFile==exe),"discover portable MT5");
        var runner=new BatchRunner((start,token)=>{
            Check(start.ArgumentList[0].StartsWith("/config:"),"MT5 startup argument");
            string config=File.ReadAllText(start.ArgumentList[0][8..]);
            Check(config.Contains("Model=4")&&config.Contains("UseCloud=0")&&config.Contains("AllowLiveTrading=0")&&!config.Contains("TestExpert="),"real ticks and local tester only");
            string Value(string key)=>config.Split('\n').First(x=>x.StartsWith(key+"="))[(key.Length+1)..].Trim();
            Check(File.Exists(Path.Combine(root,"MQL5","Profiles","Tester",Value("ExpertParameters"))),"isolated set staged");
            Directory.CreateDirectory(Path.Combine(root,"Tester","logs"));File.WriteAllText(Path.Combine(root,"Tester","logs",DateTime.Now.ToString("yyyyMMdd")+".log"),"real ticks absent for 1 minute",Encoding.Unicode);
            string report=Path.Combine(root,Value("Report")),image=Path.GetFileNameWithoutExtension(report)+".png";
            File.WriteAllText(report,"<html><table><tr><td>期間:</td><td>H1 (2026.09.01 - 2026.09.08)</td></tr><tr><td>バー:</td><td>1 000</td><td>ティック:</td><td>10 000</td></tr><tr><td>総損益:</td><td>1 010</td><td>取引数:</td><td>42</td></tr></table><img src='"+image+"'></html>",Encoding.Unicode);
            File.WriteAllBytes(Path.Combine(root,image),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg=="));return Task.CompletedTask;
        });
        int callbacks=0;runner.ReportSavedAsync=j=>{callbacks++;return Task.CompletedTask;};await runner.RunAsync(s);
        Check(job.Assessment?.Outcome==ReportOutcome.Valid&&job.Assessment.Trades==42&&job.Assessment.Profit==1010&&callbacks==1,"unicode MT5 report assessed and callback run");
        Check(job.Assessment!.Warnings.Any(x=>x.Contains("リアルティック")),"missing real ticks reported as warning");
        Check(Directory.GetFiles(Path.Combine(root,"MQL5","Profiles","Tester"),"Armada_*.set").Length==0,"temporary set cleaned");
        Check(File.Exists(job.SavedReport)&&Directory.GetFiles(Path.GetDirectoryName(job.SavedReport)!,"*.png").Length==1,"native HTML and PNG saved");
        var cancelRunner=new BatchRunner((start,token)=>Task.FromException(new OperationCanceledException()));await cancelRunner.RunAsync(s);
        Check(Directory.GetFiles(Path.Combine(root,"MQL5","Profiles","Tester"),"Armada_*.set").Length==0,"temporary set cleaned after cancellation");
    }
}
