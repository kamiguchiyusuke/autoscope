using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MT4BatchReporter;

/// <summary>One worker per batch. Private pipes, serial requests, no cached verification results.</summary>
public sealed class TdsWorker : IDisposable
{
    private Process? process;
    private SafeFileHandle? job;
    private Task<string>? errors;
    private readonly string root;
    private readonly SemaphoreSlim serial = new(1);
    public TdsWorker(string? root=null)=>this.root=root??Mt5Tds.Root;
    public void Start()
    {
        if(process is {HasExited:false})return;
        DisposeProcess();
        var start=new ProcessStartInfo(Path.Combine(root,"python","python.exe"))
        {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,
            StandardErrorEncoding=Encoding.UTF8
        };
        start.Environment["PYTHONUTF8"]="1";
        start.ArgumentList.Add("-u");start.ArgumentList.Add(Path.Combine(root,"scope_bridge.py"));start.ArgumentList.Add("--worker");
        job=CreateJobObjectW(IntPtr.Zero,null);
        if(job.IsInvalid)throw new System.ComponentModel.Win32Exception();
        var limits=new ExtendedLimit{Basic=new BasicLimit{Flags=0x2000}};
        if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimit>()))throw new System.ComponentModel.Win32Exception();
        try
        {
            process=Process.Start(start)??throw new IOException("TDS準備プロセスを起動できません。");
            if(!AssignProcessToJobObject(job,process.Handle))throw new System.ComponentModel.Win32Exception();
            errors=process.StandardError.ReadToEndAsync();
        }
        catch{DisposeProcess();throw;}
    }
    public async Task Execute(string operation,string request,Action<string> log,CancellationToken token)
    {
        await serial.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();Start();
            string id=Guid.NewGuid().ToString("N");
            await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{id,op=operation,path=request}).AsMemory(),token);
            await process.StandardInput.FlushAsync(token);
            while(true)
            {
                string? line=await process.StandardOutput.ReadLineAsync(token);
                if(line is null)throw new IOException("TDS準備プロセスが終了しました。"+(errors?.IsCompleted==true?await errors:""));
                using var response=JsonDocument.Parse(line);
                var r=response.RootElement;
                if(r.GetProperty("id").GetString()!=id)throw new IOException("TDS処理の要求IDが一致しません。");
                string? type=r.GetProperty("type").GetString();
                if(type=="log"){log(r.GetProperty("text").GetString()??"");continue;}
                if(type!="done")throw new IOException("TDS処理の応答形式が不正です。");
                if(!r.GetProperty("ok").GetBoolean())throw new IOException(r.GetProperty("error").GetString());
                return;
            }
        }
        catch{DisposeProcess();throw;}
        finally{serial.Release();}
    }
    private void DisposeProcess()
    {
        int? pid=process?.Id;
        try{if(process is {HasExited:false}){process.Kill(true);process.WaitForExit(15000);}}
        catch(InvalidOperationException){}
        finally{job?.Dispose();job=null;process?.Dispose();process=null;errors=null;}
        string guard=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"MetaQuotes","Terminal","Common","Files","TDSBridge","bridge.lock");
        if(pid.HasValue && File.Exists(guard) && File.ReadAllText(guard).Trim()==pid.Value.ToString())File.Delete(guard);
    }
    public void Dispose()=>DisposeProcess();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    {
        public long PerProcess,PerJob;public uint Flags;public UIntPtr Min,Max;public uint Active;public UIntPtr Affinity;public uint Priority,Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters {public ulong A,B,C,D,E,F;}
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    {
        public BasicLimit Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref ExtendedLimit info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
}
