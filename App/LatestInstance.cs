using System.Diagnostics;
using System.Runtime.InteropServices;
using MT4BatchReporter;
namespace ArmadaAnalyzer.Desktop;
internal static class LatestInstance
{
    // Serialize startup only. The normal close path cancels workers and restores tester settings.
    internal static IDisposable Enter()
    {
        var gate=new Mutex(false,@"Local\ArmadaScopeFX.Startup");
        try{gate.WaitOne();}catch(AbandonedMutexException){}
        try
        {
            using var current=Process.GetCurrentProcess();
            foreach(var previous in new[]{"AutoScope","ArmadaScopeFX"}.SelectMany(Process.GetProcessesByName))
            using(previous)
            {
                if(previous.Id==current.Id)continue;
                try
                {
                    if(previous.SessionId!=current.SessionId||previous.StartTime>current.StartTime)continue;
                    var path=previous.MainModule?.FileName;
                    if(path==null||FileVersionInfo.GetVersionInfo(path).ProductName is not ("Auto Scope" or "Armada Scope FX"))continue;
                    if(IsHelper(NativeProcessCommandLine.Read(previous.Id)))continue;
                    previous.CloseMainWindow();
                    if(!previous.WaitForExit(20000)){previous.Kill();previous.WaitForExit();}
                }
                catch(InvalidOperationException){} // It already exited.
            }
            return new Lease(gate);
        }
        catch{gate.ReleaseMutex();gate.Dispose();throw;}
    }
    internal static bool IsHelper(string commandLine)
    {
        if(string.IsNullOrWhiteSpace(commandLine))return false;
        nint args=CommandLineToArgvW(commandLine,out int count);
        if(args==0)throw new System.ComponentModel.Win32Exception();
        try{return count>1 && Marshal.PtrToStringUni(Marshal.ReadIntPtr(args,IntPtr.Size)) is "--tds-fast" or "--auto-smoke" or "--smoke" or "--smoke-large" or "--smoke-existing" or "--ui-performance" or "--smoke-mt5" or "--layout-audit" or "--chart-profile";}
        finally{LocalFree(args);}
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern nint CommandLineToArgvW(string text,out int count);
    [DllImport("kernel32.dll")]static extern nint LocalFree(nint memory);
    sealed class Lease(Mutex gate):IDisposable{public void Dispose(){gate.ReleaseMutex();gate.Dispose();}}
}

