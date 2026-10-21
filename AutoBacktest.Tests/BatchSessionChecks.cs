using MT4BatchReporter;
internal static class BatchSessionChecks
{
    public static async Task Run()
    {
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string root=Path.Combine(Path.GetTempPath(),"SessionTest",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"MQL4","Experts"));
        string exe=Path.Combine(root,"terminal.exe"),ea=Path.Combine(root,"MQL4","Experts","a.ex4");File.WriteAllText(exe,"");File.WriteAllText(ea,"");
        var settings=new BatchSettings{Portable=true,TerminalFile=exe,DataFolder=root,OutputFolder=Path.Combine(root,"out"),RetryCount=0,Jobs=[new(){EaFile=ea}]};
        var session=new BatchSession(()=>new BatchRunner(async (_,ct)=>{started.SetResult();await gate.Task.WaitAsync(ct);throw new IOException("test failure");}));
        var run=session.StartAsync(settings);await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        bool duplicate=false;try{await session.StartAsync(settings);}catch(InvalidOperationException){duplicate=true;}
        if(!duplicate||!session.IsRunning)throw new Exception("FAIL duplicate run guard");
        gate.SetResult();await run;
        if(session.IsRunning||settings.Jobs[0].Status!="失敗")throw new Exception("FAIL session finally releases after failure");
        Console.WriteLine("PASS session prevents duplicate start and releases after failure");
        Directory.Delete(root,true);
    }
}

