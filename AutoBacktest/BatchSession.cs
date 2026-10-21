namespace MT4BatchReporter;
public sealed class BatchSession(Func<BatchRunner>? factory = null)
{
    private int running;
    private BatchRunner? runner;
    public bool IsRunning => Volatile.Read(ref running) != 0;
    public string OutputFolder { get; private set; } = "";
    public event Action<string>? LogReceived;
    public event Action<TestJob,string>? JobChanged;
    public async Task StartAsync(BatchSettings settings)
    {
        if(Interlocked.CompareExchange(ref running,1,0)!=0)throw new InvalidOperationException("既に実行中です。");
        try
        {
            runner=factory?.Invoke()??new BatchRunner();
            runner.Log=s=>LogReceived?.Invoke(s);runner.Status=(j,s)=>JobChanged?.Invoke(j,s);
            foreach(var job in settings.Jobs){job.Status="待機";job.SavedReport="";job.Assessment=null;}
            await Task.Run(()=>runner.RunAsync(settings));
        }
        finally { OutputFolder=runner?.RunFolder??"";runner=null;Volatile.Write(ref running,0); }
    }
    public void StopAfterCurrent()=>runner?.StopAfterCurrent();
    public void StopNow()=>runner?.StopNow();
}
