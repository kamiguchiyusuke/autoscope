using MT4BatchReporter;
using System.Text;
using System.Security.Cryptography;

internal static class TestProgram
{
    static readonly string root = Path.Combine(Path.GetTempPath(), "MT4BatchTests", Guid.NewGuid().ToString("N"));
    static int passed;
    static async Task StartFake(System.Diagnostics.ProcessStartInfo original, CancellationToken token)
    {
        string root=Path.GetDirectoryName(original.FileName)!;
        var values=File.ReadAllLines(original.ArgumentList.First(File.Exists),Encoding.GetEncoding(932)).Where(l=>l.Contains('=')).ToDictionary(l=>l[..l.IndexOf('=')],l=>l[(l.IndexOf('=')+1)..]);
        string report=Path.Combine(root,values["TestReport"]);
        File.AppendAllText(Path.Combine(root,"launches.txt"),values["TestSymbol"]+"\n");
        File.WriteAllText(Path.Combine(root,"arguments.json"),System.Text.Json.JsonSerializer.Serialize(original.ArgumentList));
        string mode=Environment.GetEnvironmentVariable("MT4_FAKE_MODE")??"normal";
        await Task.Delay(mode=="slow"?60000:150,token);
        if(mode=="retry"&&!File.Exists(Path.Combine(root,"retried"))){File.WriteAllText(Path.Combine(root,"retried"),"1");File.WriteAllText(report,"BROKEN");return;}
        bool zero=values["TestSymbol"]=="ZERO";
        string dates=values["TestFromDate"]+" 00:00 - "+values["TestToDate"]+" 00:00";
        File.WriteAllBytes(report,Encoding.GetEncoding(932).GetBytes("<html><table><tr><td>Period</td><td>"+dates+"</td></tr><tr><td>Bars in test</td><td>100</td><td>Ticks modelled</td><td>1000</td></tr><tr><td>Total net profit</td><td>0.00</td><td>Total trades</td><td>"+(zero?"0":"5")+"</td></tr></table><img src='report.gif'></html>"));
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(report)!,"report.gif"),Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"));
    }
    static void Check(bool value, string name) { if(!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
    [STAThread] static void Main(string[] args)
    {
        try { RunMain(args); }
        catch(Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode=1; }
    }
    static void RunMain(string[] args)
    {
        if(args.Length>1&&args[0]=="--tds-worker-tests"){WorkerTests.Run(args[1]).GetAwaiter().GetResult();return;}
        if(args.Length>1&&args[0]=="--tds-worker-parent")
        {
            using var worker=new TdsWorker(args[1]);
            worker.Execute("prepare","sleep",line=>File.WriteAllText(Path.Combine(args[1],"worker.pid"),line),CancellationToken.None).GetAwaiter().GetResult();
            return;
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        if (args.Length > 0 && args[0] == "--dialog-fixture")
        {
            using var fixture=new NativeDialogFixture(args[1],int.Parse(args[2]),args[3] is "labels" or "hidden-labels",hiddenLabels:args[3]=="hidden-labels");
            var until=System.Diagnostics.Stopwatch.StartNew();
            while(!fixture.Cancelled&&until.ElapsedMilliseconds<1800){Application.DoEvents();Thread.Sleep(10);}
            Environment.ExitCode=fixture.Cancelled?0:2;return;
        }
        Directory.CreateDirectory(root);
        LogChecks.Run();


        if(args.Contains("--ui-state"))return;
        ReviewFixChecks.Run();
        JobNamingChecks.Run();
        ReportInspectorChecks.Run();
        StartupDialogChecks();
        Task.Run(Run).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(root,"test-results.txt"),$"{passed} meaningful checks passed.\nFixtures: {root}\nReal MT4 was not launched or modified.");
        Console.WriteLine($"{passed} checks passed");
    }
    static BatchSettings Fixture(string name, params string[] symbols)
    {
        string data=Path.Combine(root,name);Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data,"terminal.exe"),"test-only placeholder; fake executor boundary");
        Directory.CreateDirectory(Path.Combine(data,"MQL4","Experts"));Directory.CreateDirectory(Path.Combine(data,"tester"));
        string ea=Path.Combine(data,"MQL4","Experts","Demo.ex4");File.WriteAllText(ea,"fake EA");
        string set=Path.Combine(data,"test.set");File.WriteAllText(set,"Lots=0.1\nLabel=テスト\n",new UTF8Encoding(false));
        var jobs=symbols.Select(x=>new TestJob{Name="試験 "+x,EaFile=ea,SetFile=set,Symbol=x}).ToList();
        return new BatchSettings{TerminalFile=Path.Combine(data,"terminal.exe"),DataFolder=data,OutputFolder=Path.Combine(data,"results"),TimeoutMinutes=1,RetryCount=1,Portable=true,Jobs=jobs};
    }
    static void StartupDialogChecks()
    {
        // Exercise a real Win32 dialog and its IDCANCEL handler. Before the fix,
        // no startup-dialog controller exists and the fixture stays blocked.
        using var dialog=new NativeDialogFixture("デモ口座の申請");
        var controller=new StartupDialogCanceller(true);
        for(int i=0;i<40&&!dialog.Cancelled;i++) { controller.Poll(Environment.ProcessId); Application.DoEvents();Thread.Sleep(20); }
        Check(dialog.Cancelled,"起動時の口座案内をIDCANCELだけで閉じて進行");
        using(var repeated=new NativeDialogFixture("Authorization",hideFirstCancellation:true))
        {
            var again=new StartupDialogCanceller(true);
            for(int i=0;i<40&&repeated.CancelCount<1;i++){again.Poll(Environment.ProcessId);Application.DoEvents();Thread.Sleep(20);}
            again.Poll(Environment.ProcessId);repeated.Reopen();
            for(int i=0;i<40&&repeated.CancelCount<2;i++){again.Poll(Environment.ProcessId);Application.DoEvents();Thread.Sleep(20);}
            Check(repeated.CancelCount==2,"同じハンドルの案内が再表示されてもキャンセル");
        }
        void CrossProcess(string title,int button,bool enabled,bool labels,bool wrongPid,bool expect,string label,bool hiddenLabels=false)
        {
            var start=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false};
            foreach(var a in new[]{"--dialog-fixture",title,button.ToString(),hiddenLabels?"hidden-labels":labels?"labels":"plain"})start.ArgumentList.Add(a);
            using var process=System.Diagnostics.Process.Start(start)!;
            var canceler=new StartupDialogCanceller(enabled);var clock=System.Diagnostics.Stopwatch.StartNew();
            while(!process.HasExited&&clock.ElapsedMilliseconds<6000){canceler.Poll(wrongPid?Environment.ProcessId:process.Id);Application.DoEvents();Thread.Sleep(20);}
            if(!process.HasExited){process.Kill(true);process.WaitForExit();}
            Check((process.ExitCode==0)==expect,label);
        }
        CrossProcess("Settings fixture",2,true,true,false,false,"隠れた認証欄を持つ設定画面は閉じない",hiddenLabels:true);
        CrossProcess("Authorization",2,true,false,false,true,"別プロセスのログイン画面へキャンセルだけ送信");
        CrossProcess("Broker fixture",2,true,true,false,true,"別プロセスのログインラベルを認識（Edit本文は読まない）");
        CrossProcess("Order Confirmation",2,true,false,false,false,"注文確認など無関係な画面には触れない");
        CrossProcess("Authorization",2,true,false,true,false,"別のPIDの画面には触れない");
        CrossProcess("Authorization",7,true,false,false,false,"IDCANCEL以外のボタンを押さない");
        CrossProcess("Authorization",2,false,false,false,false,"自動キャンセル無効時は画面を操作しない");
    }
    static async Task Run()
    {
        {
            string discovery=Path.Combine(root,"discovery"),programs=Path.Combine(discovery,"Programs"),terminals=Path.Combine(discovery,"Terminal");
            string fxcm=Path.Combine(programs,"FXCM MetaTrader 4"),data=Path.Combine(terminals,"ABC"),portable=Path.Combine(programs,"ThreeTrader MT4 Terminal");
            Directory.CreateDirectory(fxcm);File.WriteAllText(Path.Combine(fxcm,"terminal.exe"),"");Directory.CreateDirectory(Path.Combine(data,"MQL4","Experts"));File.WriteAllText(Path.Combine(data,"origin.txt"),fxcm,Encoding.Unicode);
            Directory.CreateDirectory(Path.Combine(portable,"MQL4","Experts"));File.WriteAllText(Path.Combine(portable,"terminal.exe"),"");
            string mt5=Path.Combine(programs,"Broker MT5");Directory.CreateDirectory(mt5);File.WriteAllText(Path.Combine(mt5,"terminal64.exe"),"");
            var found=Mt4Installations.Discover(terminals,[programs]);
            Check(found.Count==2 && found.Any(x=>x.Name=="FXCM MT4" && x.DataFolder==data && !x.Portable),"MT4 names and exact origin data folder detected");
            Check(found.Any(x=>x.Name=="ThreeTrader MT4" && x.Portable),"portable MT4 detected without including MT5");
        }
        {
            var tds=Fixture("tds-scope","EURUSD");tds.VariableSpreadTds=true;tds.UseCurrentServer=true;tds.DisableTickDataSuite=false;tds.ServerName="STALE-SERVER";
            string config=Path.Combine(tds.DataFolder,"config","tds.config");Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config,"<configuration><global><add key='UseTickDataEnabled' value='False'/><add key='UseVariableSpread' value='False'/><add key='GMTOffset' value='2'/></global></configuration>");var tdsOriginalBytes=File.ReadAllBytes(config);
            await new BatchRunner(async (start,ct)=>{var text=File.ReadAllText(start.ArgumentList[0],Encoding.GetEncoding(932));Check(!text.Contains("Server="),"MT4 server setting is not overridden");Check(text.Contains("TestSpread=0")&&text.Contains("TestModel=0")&&!start.ArgumentList.Contains("/notds"),"TDS launch uses tick model without fixed spread");var xml=System.Xml.Linq.XDocument.Load(config);Check(xml.Descendants("add").Where(x=>(string?)x.Attribute("key") is "UseVariableSpread" or "UseTickDataEnabled").All(x=>(string?)x.Attribute("value")=="True"),"TDS variable spread enabled during test");await StartFake(start,ct);}).RunAsync(tds);
            Check(tdsOriginalBytes.SequenceEqual(File.ReadAllBytes(config)),"TDS settings restored byte-for-byte");
            var utcSource=Encoding.UTF8.GetBytes("<configuration><global><add key='UseTickDataEnabled' value='False'/><add key='UseVariableSpread' value='False'/><add key='GMTOffset' value='2'/><add key='DST' value='1'/></global></configuration>");
            var utcConfig=System.Xml.Linq.XDocument.Load(new MemoryStream(Mt4MouseSettings.VariableSpreadConfig(utcSource,true)));
            Check(utcConfig.Descendants("add").Where(x=>(string?)x.Attribute("key") is "GMTOffset" or "DST").All(x=>(string?)x.Attribute("value")=="0"),"comparison clock is UTC without DST");
            var keepClock=System.Xml.Linq.XDocument.Load(new MemoryStream(Mt4MouseSettings.VariableSpreadConfig(utcSource)));
            Check(keepClock.Descendants("add").Single(x=>(string?)x.Attribute("key")=="GMTOffset").Attribute("value")!.Value=="2" && keepClock.Descendants("add").Single(x=>(string?)x.Attribute("key")=="DST").Attribute("value")!.Value=="1","ordinary TDS runs preserve timezone and DST");
            var defaults=new TestJob();Check(defaults.Deposit==1000000&&defaults.Currency=="JPY","new job defaults one million JPY");
        }
        await BatchSessionChecks.Run();await Mt5Checks.Run();
        var pipeline=Fixture("scope-pipeline","EURUSD","USDJPY");var sequence=new List<string>();
        var engine=new BatchRunner(StartFake){ReportSavedAsync=async j=>{Check(File.Exists(j.SavedReport),"callback receives saved native report");Check(!BatchRunner.HasRecovery(pipeline.DataFolder),"settings restored before scope export");await Task.Delay(10);sequence.Add(j.Symbol);}};
        await engine.RunAsync(pipeline);Check(sequence.SequenceEqual(new[]{"EURUSD","USDJPY"}),"scope callbacks run sequentially");
        var fail=Fixture("scope-failure","EURUSD");fail.RetryCount=2;int calls=0;
        await new BatchRunner(StartFake){ReportSavedAsync=j=>{calls++;throw new IOException("PDF failure fixture");}}.RunAsync(fail);
        Check(calls==1 && fail.Jobs[0].Status.Contains("PDF失敗") && File.Exists(fail.Jobs[0].SavedReport),"PDF failure preserves HTML without repeating MT4");
        await SymbolChecks();
        var s=Fixture("sequential","EURUSD","GBPUSD","ZERO");
        s.DisableTickDataSuite=true;s.ServerName="FXCM-USDDemo02";
        var original=new byte[]{0xff,0xfe,0x41,0,0x42,0};string ini=Path.Combine(s.DataFolder,"tester","Demo.ini");File.WriteAllBytes(ini,original);
        var runner=new BatchRunner(StartFake);await runner.RunAsync(s);
        Check(s.Jobs.All(x=>x.Status=="保存完了"),"3件の連続実行・0取引成功");
        Check(File.ReadAllLines(Path.Combine(s.DataFolder,"launches.txt")).SequenceEqual(new[]{"EURUSD","GBPUSD","ZERO"}),"実行順の一致");
        Check(File.ReadAllBytes(ini).SequenceEqual(original),"既存INIをバイト単位で復元");
        Check(!BatchRunner.HasRecovery(s.DataFolder),"復元待ちの解消");
        ProfileChecks.Run(root);
        foreach(var j in s.Jobs){Check(File.Exists(j.SavedReport),"標準HTM保存 "+j.Symbol);string stage=Directory.EnumerateFiles(Path.Combine(s.DataFolder,"tester","BatchReportApp","runs"),"report.htm",SearchOption.AllDirectories).First(f=>Encoding.GetEncoding(932).GetString(File.ReadAllBytes(f))==Encoding.GetEncoding(932).GetString(File.ReadAllBytes(j.SavedReport)));Check(SHA256.HashData(File.ReadAllBytes(stage)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(j.SavedReport))),"HTM無改変 "+j.Symbol);}
        Check(File.Exists(Path.Combine(Path.GetDirectoryName(s.Jobs[0].SavedReport)!,"report.gif")),"参照GIFも同じフォルダーに保存");
        Check(Directory.GetFiles(runner.RunFolder,"*",SearchOption.AllDirectories).All(f=>new[]{".htm",".gif"}.Contains(Path.GetExtension(f))),"report folder excludes diagnostics and analysis data");
        Check(File.Exists(Path.Combine(runner.DiagnosticsFolder,"results.json")),"diagnostics retained outside report destination");
        Check(Path.GetFileName(ReportFiles.ScopePdfPath(root,"EA名_USDJPY（2）"))=="EA名_USDJPY（2）.pdf","PDF uses exact test name");
        Check(Path.GetFileName(ReportFiles.ScopePdfPath(root,"EA/JPY:*"))=="EA_JPY__.pdf","PDF replaces invalid filename characters");
        Check(Path.GetFileName(ReportFiles.ScopePdfPath(root,"CON"))=="_CON.pdf","PDF handles Windows reserved name");
        var cfg=File.ReadAllText(Directory.EnumerateFiles(runner.DiagnosticsFolder,"startup_attempt0.txt",SearchOption.AllDirectories).First(),Encoding.GetEncoding(932));Check(cfg.Contains("TestShutdownTerminal=true")&&cfg.Contains("TestOptimization=false")&&cfg.Contains("TestReport="),"MT4純正レポート指定");
        Check(cfg.Contains("Server=FXCM-USDDemo02"),"任意サーバー名をMT4構成へ反映");
        var args=System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(s.DataFolder,"arguments.json")))!;
        Check(args[0].EndsWith("startup.txt")&&args.Contains("/portable")&&args.Contains("/notds"),"構成ファイルと標準MT4起動オプションを渡す");
        string json=Path.Combine(root,"settings.json");s.Save(json);var loaded=BatchSettings.Load(json);Check(loaded.Jobs.Count==3&&loaded.Jobs[0].Symbol=="EURUSD"&&loaded.Jobs[0].Status=="待機","設定保存・読込み、実行状況は保存しない");
        Environment.SetEnvironmentVariable("MT4_FAKE_MODE","retry");var retry=Fixture("retry","EURUSD");await new BatchRunner(StartFake).RunAsync(retry);Environment.SetEnvironmentVariable("MT4_FAKE_MODE",null);
        Check(retry.Jobs[0].Status=="保存完了"&&File.ReadAllLines(Path.Combine(retry.DataFolder,"launches.txt")).Length==2,"破損HTMLの再試行");
        Check(!File.Exists(Path.Combine(retry.DataFolder,"tester","Demo.ini")),"元INIがなければ生成INIを除去");
        var graceful=Fixture("graceful","EURUSD","GBPUSD");var gr=new BatchRunner(StartFake);gr.Status=(j,status)=>{if(status=="実行中")gr.StopAfterCurrent();};await gr.RunAsync(graceful);Check(graceful.Jobs[0].Status=="保存完了"&&graceful.Jobs[1].Status=="未実行（中止）","終了待ち中止でレポート保存し次の投入停止");
        var force=Fixture("force","EURUSD","GBPUSD");Environment.SetEnvironmentVariable("MT4_FAKE_MODE","slow");var fr=new BatchRunner(StartFake);fr.Status=(j,state)=>{if(state=="実行中")_ = Task.Run(async()=>{await Task.Delay(600);fr.StopNow();});};await fr.RunAsync(force);Environment.SetEnvironmentVariable("MT4_FAKE_MODE",null);
        Check(force.Jobs[0].Status=="中止"&&!File.Exists(Path.Combine(force.DataFolder,"tester","Demo.ini"))&&!BatchRunner.HasRecovery(force.DataFolder),"即時中止後のINI復元（実行部分は模擬）");
        string bad=Path.Combine(root,"bad.htm");File.WriteAllText(bad,"<html><table></table><img src='../escape.gif'></html>");bool rejected=false;try{BatchRunner.CollectNativeReport(bad,Path.Combine(root,"unsafe"));}catch(InvalidDataException){rejected=true;}Check(rejected,"範囲外の画像参照を拒否");
        var input=new TestJob{SetFile=Path.Combine(s.DataFolder,"test.set")};string iniText=JobFiles.TesterIni(input);Check(iniText.Contains("Label=テスト")&&iniText.Contains("deposit=1000000")&&iniText.Contains("currency=JPY"),"日本語setと入金設定の生成");
        var invalid=s.Jobs[0].Clone();invalid.To=invalid.From;s.Jobs=[invalid];rejected=false;try{JobFiles.Validate(s);}catch(InvalidDataException){rejected=true;}Check(rejected,"逆転・同日の日付を拒否");
        var recover=Fixture("recover","EURUSD");string originalIni=Path.Combine(recover.DataFolder,"tester","Demo.ini"),backup=Path.Combine(recover.DataFolder,"tester","BatchReportApp","state","test","original.ini");Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.WriteAllBytes(backup,original);File.WriteAllText(originalIni,"interrupted");
        File.WriteAllText(Path.Combine(recover.DataFolder,"tester",".mt4batchreporter.pending.json"),System.Text.Json.JsonSerializer.Serialize(new {IniFile=originalIni,BackupFile=backup,BackupHash=Convert.ToHexString(SHA256.HashData(original)),Existed=true}));
        BatchRunner.RecoverSafely(recover.TerminalFile,recover.DataFolder);Check(File.ReadAllBytes(originalIni).SequenceEqual(original)&&!BatchRunner.HasRecovery(recover.DataFolder),"異常終了後のバックアップ復元");
        var locking=Fixture("lock","EURUSD");using(var lease=new FileStream(Path.Combine(locking.DataFolder,"tester",".mt4batchreporter.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){rejected=false;try{await new BatchRunner(StartFake).RunAsync(locking);}catch(IOException){rejected=true;}Check(rejected,"同一データフォルダーへの重複実行を拒否");}
    }
    static void Catalog(BatchSettings s, string server, params string[] names)
    {
        string folder=Path.Combine(s.DataFolder,"history",server); Directory.CreateDirectory(folder);
        using var stream=File.Create(Path.Combine(folder,"symbols.raw"));
        foreach(string name in names){var record=new byte[1936];Encoding.ASCII.GetBytes(name).CopyTo(record,0);BitConverter.GetBytes(2).CopyTo(record,104);BitConverter.GetBytes(2).CopyTo(record,108);stream.Write(record);}
    }
    static async Task SymbolChecks()
    {
        var alternate=Fixture("alternate-quote","XAUUSD");alternate.ServerName="Alt-Broker";Catalog(alternate,"Alt-Broker","Metal1");
        string alternateRaw=Path.Combine(alternate.DataFolder,"history",alternate.ServerName,"symbols.raw");var alternateRecord=File.ReadAllBytes(alternateRaw);Encoding.ASCII.GetBytes("Gold / Euro").CopyTo(alternateRecord,12);Encoding.ASCII.GetBytes("GOLD").CopyTo(alternateRecord,76);Encoding.ASCII.GetBytes("XAU").CopyTo(alternateRecord,88);File.WriteAllBytes(alternateRaw,alternateRecord);
        var alternateCatalog=SymbolCatalog.Load(alternate.DataFolder,alternate.ServerName);
        Check(alternateCatalog.Candidates("XAUUSD").Count==0&&alternateCatalog.Candidates("GOLD").Count==0,"別名フィールドでも金の建値通貨を区別");
        foreach(string quoted in new[]{"GOLD.EUR","GOLD.JPY","GOLD","GoldJPY"})
        {
            var quote=Fixture("quote-"+quoted,"XAUUSD");quote.ServerName="Quote-Broker";Catalog(quote,"Quote-Broker",quoted);
            string raw=Path.Combine(quote.DataFolder,"history",quote.ServerName,"symbols.raw");var record=File.ReadAllBytes(raw);Encoding.ASCII.GetBytes(quoted=="GoldJPY"?"":quoted.Contains("JPY")?"Gold / Japanese Yen":"Gold / Euro").CopyTo(record,12);Encoding.ASCII.GetBytes("XAU").CopyTo(record,88);File.WriteAllBytes(raw,record);
            Check(SymbolCatalog.Load(quote.DataFolder,quote.ServerName).Candidates("XAUUSD").Count==0,"金の建値通貨を区別: "+quoted);
        }
        var suffix=Fixture("symbol-suffix","XAUUSD");suffix.ServerName="Broker-A";Catalog(suffix,"Broker-A","EURUSD.xyz","XAUUSD.xyz");
        await new BatchRunner(StartFake).RunAsync(suffix);
        Check(File.ReadAllLines(Path.Combine(suffix.DataFolder,"launches.txt")).SequenceEqual(new[]{"XAUUSD.xyz"}),"任意の接尾辞を実銘柄へ照合して起動");
        var prefix=Fixture("symbol-prefix","XAUUSD.raw");prefix.ServerName="Broker-B";Catalog(prefix,"Broker-B","proXAUUSD");
        await new BatchRunner(StartFake).RunAsync(prefix);
        Check(File.ReadAllText(Path.Combine(prefix.DataFolder,"launches.txt")).Trim()=="proXAUUSD","別ブローカーの接尾辞から接頭辞へ照合");
        var exact=Fixture("symbol-exact","XAUUSD");exact.ServerName="Broker-C";Catalog(exact,"Broker-C","XAUUSD","XAUUSD.m");
        await new BatchRunner(StartFake).RunAsync(exact);
        Check(File.ReadAllText(Path.Combine(exact.DataFolder,"launches.txt")).Trim()=="XAUUSD","実銘柄の完全一致を優先");
        var ambiguous=Fixture("symbol-ambiguous","XAUUSD");ambiguous.ServerName="Broker-D";Catalog(ambiguous,"Broker-D","XAUUSD.a","XAUUSD.b");
        await new BatchRunner(StartFake).RunAsync(ambiguous);
        Check(!File.Exists(Path.Combine(ambiguous.DataFolder,"launches.txt"))&&ambiguous.Jobs[0].Status!="保存完了","複数候補では勝手に起動しない");
        var missing=Fixture("symbol-server","XAUUSD");missing.ServerName="Broker-E";Catalog(missing,"Broker-E","EURUSD");Catalog(missing,"Other-Broker","XAUUSD");
        await new BatchRunner(StartFake).RunAsync(missing);
        Check(!File.Exists(Path.Combine(missing.DataFolder,"launches.txt")),"別サーバーの銘柄を混ぜない");
        foreach(var item in new[]{("Gold#","XAUUSD"),("GOLDmicro","XAUUSD.raw"),("XAUUSD.m","Gold"),("mGOLD","XAUUSD"),("GOLD.xyz","XAUUSD"),("XAUUSD.raw","GOLD.xyz")})
        {
            var gold=Fixture("gold-"+Guid.NewGuid().ToString("N"),item.Item2);gold.ServerName="Gold-Broker";Catalog(gold,"Gold-Broker",item.Item1,"XAUJPY");
            await new BatchRunner(StartFake).RunAsync(gold);
            Check(File.Exists(Path.Combine(gold.DataFolder,"launches.txt"))&&File.ReadAllText(Path.Combine(gold.DataFolder,"launches.txt")).Trim()==item.Item1,"金の別名と大文字小文字を照合: "+item.Item2+" → "+item.Item1);
        }
        var goldAmbiguous=Fixture("gold-ambiguous","XAUUSD");goldAmbiguous.ServerName="Gold-Broker";Catalog(goldAmbiguous,"Gold-Broker","GOLD","GOLDmicro","XAUJPY");
        await new BatchRunner(StartFake).RunAsync(goldAmbiguous);
        Check(!File.Exists(Path.Combine(goldAmbiguous.DataFolder,"launches.txt")),"標準・マイクロの金を勝手に選ばない");
        var stock=Fixture("gold-stock","XAUUSD");stock.ServerName="Stock-Broker";Catalog(stock,"Stock-Broker","GOLD");
        string stockRaw=Path.Combine(stock.DataFolder,"history",stock.ServerName,"symbols.raw");var stockRecord=File.ReadAllBytes(stockRaw);Encoding.ASCII.GetBytes("Gold company shares").CopyTo(stockRecord,12);File.WriteAllBytes(stockRaw,stockRecord);
        await new BatchRunner(StartFake).RunAsync(stock);
        Check(!File.Exists(Path.Combine(stock.DataFolder,"launches.txt")),"GOLDという株式を金スポットと取り違えない");
        var future=Fixture("gold-futures","GOLD.xyz");future.ServerName="Future-Broker";Catalog(future,"Future-Broker","GOLD.xyzF");
        string futureRaw=Path.Combine(future.DataFolder,"history",future.ServerName,"symbols.raw");var futureRecord=File.ReadAllBytes(futureRaw);Encoding.ASCII.GetBytes("Gold futures").CopyTo(futureRecord,12);File.WriteAllBytes(futureRaw,futureRecord);
        await new BatchRunner(StartFake).RunAsync(future);
        Check(!File.Exists(Path.Combine(future.DataFolder,"launches.txt")),"未知の接尾辞から金先物へ部分一致しない");
        var blankServer=Fixture("blank-server","XAUUSD");Catalog(blankServer,"Stale-Broker","XAUUSD.raw");
        await new BatchRunner(StartFake).RunAsync(blankServer);
        Check(!File.Exists(Path.Combine(blankServer.DataFolder,"launches.txt")),"サーバー未指定で古い一覧を推測使用しない");

    }
}







