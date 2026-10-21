using MT4BatchReporter;
using System.Text;
internal static class LogChecks
{
    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"AutoScopeLogChecks",Guid.NewGuid().ToString("N"));
        string logs=Path.Combine(root,"tester","logs"),output=Path.Combine(root,"out");
        Directory.CreateDirectory(logs);Directory.CreateDirectory(output);
        string file=Path.Combine(logs,"20261020.log");
        File.WriteAllText(file,"old user log\n",Encoding.Unicode);
        var cursor=EnvironmentReport.CaptureTesterLogs(root);
        File.AppendAllText(file,string.Concat(Enumerable.Repeat("ordinary trade execution\n",20000))+"real ticks absent\nfinal\n",Encoding.Unicode);
        long original=new FileInfo(file).Length;
        EnvironmentReport.CopyTesterLog(root,output,0,cursor,true);
        string result=File.ReadAllText(Path.Combine(output,"tester_attempt0.log"));
        if(result.Contains("old user log")||!result.Contains("real ticks absent")||result.Length>40000||new FileInfo(file).Length!=original)
            throw new Exception("Incremental log capture lost a warning, copied old logs, or changed source");
        cursor=EnvironmentReport.CaptureTesterLogs(root);
        File.WriteAllText(Path.Combine(logs,"20261021.log"),"rollover warning\n",Encoding.Unicode);
        EnvironmentReport.CopyTesterLog(root,output,1,cursor,true);
        if(!File.ReadAllText(Path.Combine(output,"tester_attempt1.log")).Contains("rollover warning"))
            throw new Exception("Log rollover lost");
        string history=Path.Combine(root,"history","server");Directory.CreateDirectory(history);
        var bars=new byte[208];BitConverter.GetBytes(401).CopyTo(bars,0);BitConverter.GetBytes(1600000000L).CopyTo(bars,148);
        File.WriteAllBytes(Path.Combine(history,"USDJPY1.hst"),bars);File.WriteAllBytes(Path.Combine(history,"EURUSD1.hst"),bars);
        var settings=new BatchSettings{DataFolder=root};settings.Jobs.Add(new TestJob{Symbol="USDJPY",Period="M1"});
        string env=Path.Combine(output,"environment.json");EnvironmentReport.Save(settings,env);
        using(var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(env)))
            if(doc.RootElement.GetProperty("histories").GetArrayLength()!=1)throw new Exception("Unrelated HST files read");
        Console.WriteLine("PASS incremental bounded logs / warning retention / originals unchanged / rollover");
    }
}
