using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
namespace MT4BatchReporter;
public static class Mt5Support
{
    public static string Setting(string data,string key)
    {
        string path=Path.Combine(data,"config","common.ini");if(!File.Exists(path))return "";
        return File.ReadLines(path).FirstOrDefault(x=>x.StartsWith(key+"=",StringComparison.OrdinalIgnoreCase))?.Split('=',2)[1].Trim()??"";
    }
    public static IReadOnlyList<string> Symbols(string data)
    {
        string server=Setting(data,"Server");if(string.IsNullOrWhiteSpace(server)||server.IndexOfAny(Path.GetInvalidFileNameChars())>=0)return [];
        string root=Path.Combine(data,"bases",server);var names=new HashSet<string>(StringComparer.Ordinal);
        string history=Path.Combine(root,"history");if(Directory.Exists(history))foreach(var dir in Directory.GetDirectories(history))names.Add(Path.GetFileName(dir));
        string symbols=Path.Combine(root,"symbols");
        if(Directory.Exists(symbols))foreach(var file in Directory.GetFiles(symbols,"selected-*.dat"))
        {
            var bytes=File.ReadAllBytes(file);if(bytes.Length>16000000)continue;
            string text=Encoding.Unicode.GetString(bytes);
            // Selected-symbol records repeat the exact name 96 bytes after its first field.
            foreach(Match m in Regex.Matches(text,@"[A-Za-z0-9][A-Za-z0-9._#&-]{1,30}\x00"))
            {
                string name=m.Value.TrimEnd('\0');int next=m.Index+48;
                if(next+name.Length<text.Length && text.AsSpan(next,name.Length).SequenceEqual(name.AsSpan()) && text[next+name.Length]=='\0')names.Add(name);
            }
        }
        return names.Order().ToArray();
    }
    public static string Startup(TestJob j,string expert,string report,string parameters,string data)
    {
        string login=Setting(data,"Login"),server=Setting(data,"Server");
        var common="[Common]\r\n";
        if(Regex.IsMatch(login,@"^\d+$"))common+="Login="+login+"\r\n";
        if(server.Length>0&&!server.Contains('\n')&&!server.Contains('\r'))common+="Server="+server+"\r\n";
        return common+string.Join("\r\n",new[]{"[Experts]","Enabled=0","AllowLiveTrading=0","AllowDllImport=0","[Tester]",$"Expert={expert}.ex5",$"ExpertParameters={parameters}",$"Symbol={j.Symbol}",$"Period={(j.Period=="MN"?"MN1":j.Period)}",$"Model={j.Model}","ExecutionMode=0","Optimization=0","ForwardMode=0",$"FromDate={j.From:yyyy.MM.dd}",$"ToDate={j.To:yyyy.MM.dd}",$"Deposit={j.Deposit.ToString(CultureInfo.InvariantCulture)}",$"Currency={j.Currency.ToUpperInvariant()}","UseLocal=1","UseRemote=0","UseCloud=0","Visual=0","ReplaceReport=1","ShutdownTerminal=1",$"Report={report}",""});
    }
}
public sealed partial class BatchRunner
{
    private async Task AttemptMt5(BatchSettings s,TestJob j,int index,int attempt,string runId)
    {
        var preparation=Stopwatch.StartNew();
        if(TerminalIsRunning(s.TerminalFile))throw new IOException("実行対象のMT5を閉じてから実行してください。");
        string stem=$"{index+1:D4}_{JobFiles.SafeName(j.Name)}";if(stem.Length>85)stem=stem[..85];
        string diagnosticDir=DiagnosticJobFolder(index);
        string jobDir=Path.Combine(RunFolder,stem);Directory.CreateDirectory(jobDir);
        var actual=j;
        if(s.UseTds==true){Set(j,"TDS書出・MT5取込");actual=await Mt5Tds.Prepare(s,j,diagnosticDir,Say,cancel.Token,tdsWorker!);}
        string id="Armada_"+Guid.NewGuid().ToString("N"),report=id+".htm";
        string profiles=Path.Combine(s.DataFolder,"MQL5","Profiles","Tester");Directory.CreateDirectory(profiles);
        string set=Path.Combine(profiles,id+".set");
        if(string.IsNullOrWhiteSpace(j.SetFile))File.WriteAllText(set,"",Encoding.Unicode);else File.Copy(j.SetFile,set);
        string config=Path.Combine(diagnosticDir,$"startup_attempt{attempt}.ini");
        var logCursor=EnvironmentReport.CaptureTesterLogs(s.DataFolder);
        try
        {
            File.WriteAllText(config,Mt5Support.Startup(actual,JobFiles.EaRelativeName(j,s.DataFolder,true),report,Path.GetFileName(set),s.DataFolder),Encoding.Unicode);
            Set(j,"実行中・履歴同期");Say($"[{index+1}/{s.Jobs.Count}] {j.Name}: MT5 {actual.Symbol} {actual.Period}。{(s.UseTds==true?"TDS・UTC実ティックを使用します。":"不足履歴は接続サーバーから同期します。")}");
            var start=new ProcessStartInfo(s.TerminalFile){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(s.TerminalFile)!,WindowStyle=ProcessWindowStyle.Hidden};
            start.ArgumentList.Add("/config:"+config);if(s.Portable)start.ArgumentList.Add("/portable");
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);limit.CancelAfter(TimeSpan.FromMinutes(s.TimeoutMinutes));
            Say($"MT5起動準備完了: {preparation.Elapsed.TotalMilliseconds:0.0} ms");
            try{await execute(start,limit.Token);}catch(OperationCanceledException){if(cancel.IsCancellationRequested)throw;throw new TimeoutException($"MT5の履歴同期・テストが制限時間（{s.TimeoutMinutes}分）を超えました。");}
            if(TerminalIsRunning(s.TerminalFile))throw new IOException("MT5の終了を確認できません。");
            string found=Path.Combine(s.DataFolder,report);if(!File.Exists(found))found=Path.Combine(Path.GetDirectoryName(s.TerminalFile)!,report);
            await WaitStable(found,cancel.Token);
            j.SavedReport=CollectNativeReport(found,jobDir,true);
            string named=Path.Combine(jobDir,stem+".htm");File.Move(j.SavedReport,named);j.SavedReport=named;
            Say(j.Name+": MT5標準HTML・画像を保存しました。");
        }
        finally
        {
            if(s.UseTds==true && !TerminalIsRunning(s.TerminalFile))
                await Mt5Tds.Finish(diagnosticDir,Say,tdsWorker!);
            EnvironmentReport.CopyTesterLog(s.DataFolder,diagnosticDir,attempt,logCursor,true,"mt5-run.log");

            if(!TerminalIsRunning(s.TerminalFile))File.Delete(set);
        }
    }
}
