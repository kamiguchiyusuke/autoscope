using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace MT4BatchReporter;
public static class Mt5Tds
{
    public static string Root=>Path.Combine(AppContext.BaseDirectory,"TdsBridge");
    public static bool Available=>File.Exists(Path.Combine(Root,"python","python.exe")) && File.Exists(Path.Combine(Root,"scope_bridge.py")) && Mt4MouseSettings.TdsInstalled();
    public static TestJob EffectiveJob(TestJob job,string symbol)
    {
        if(!Regex.IsMatch(symbol,@"^[A-Z0-9]{3,12}_TDS_UTC$")||symbol.Length>31)throw new InvalidDataException("TDS取込先の銘柄名が不正です。");
        var copy=job.Clone();copy.Symbol=symbol;copy.Model=4;return copy;
    }
    public static async Task<TestJob> Prepare(BatchSettings settings,TestJob job,string folder,Action<string> log,CancellationToken token,TdsWorker worker)
    {
        if(!Available)throw new IOException("MT5用TDS連携またはTick Data Suiteが見つかりません。インストールを確認してください。");
        string request=Path.Combine(folder,"tds-request.json"),receipt=Path.Combine(folder,"tds-result.json");
        File.WriteAllText(request,JsonSerializer.Serialize(new{terminal=settings.TerminalFile,data=settings.DataFolder,portable=settings.Portable,symbol=job.Symbol,period=job.Period,from=job.From.ToString("yyyy-MM-dd"),to=job.To.ToString("yyyy-MM-dd"),output=receipt}),Encoding.UTF8);
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(TimeSpan.FromMinutes(settings.TimeoutMinutes));
        await worker.Execute("prepare",request,log,limit.Token);
        if(!File.Exists(receipt))throw new IOException("TDS検証結果がありません。");
        using var result=JsonDocument.Parse(File.ReadAllText(receipt));
        if(!result.RootElement.GetProperty("ok").GetBoolean())throw new IOException("TDS取込の検証が完了していません。");
        return EffectiveJob(job,result.RootElement.GetProperty("symbol").GetString()!);
    }
    public static async Task Finish(string folder,Action<string> log,TdsWorker worker)
    {
        string request=Path.Combine(folder,"tds-request.json");
        if(!File.Exists(request))return;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await worker.Execute("finish",request,log,timeout.Token);
        }
        catch(Exception e){log("履歴復元を見送りました。次回は変更検知・全件照合を行います: "+e.Message);}
    }
}