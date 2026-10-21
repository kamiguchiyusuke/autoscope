using MT4BatchReporter;
using System.Diagnostics;
using System.Text.Json;
static class WorkerTests
{
    public static async Task Run(string root)
    {
        int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
        var lines=new List<string>();
        using(var worker=new TdsWorker(root))
        {
            await worker.Execute("prepare","ok",lines.Add,CancellationToken.None);
            int pid=int.Parse(lines.Last());
            await worker.Execute("finish","ok",lines.Add,CancellationToken.None);
            Check(pid==int.Parse(lines.Last()),"same owned worker used for consecutive operations");
            try{await worker.Execute("prepare","error",lines.Add,CancellationToken.None);throw new Exception("expected error");}
            catch(IOException e){Check(e.Message.Contains("fixture error"),"worker error fails request");}
            await worker.Execute("prepare","ok",lines.Add,CancellationToken.None);
            Check(pid!=int.Parse(lines.Last()),"fresh worker after error");
            using var cancel=new CancellationTokenSource(700);
            try{await worker.Execute("prepare","sleep",lines.Add,cancel.Token);throw new Exception("expected cancellation");}
            catch(OperationCanceledException){Check(true,"cancellation interrupts private pipe read");}
            int child=int.Parse(File.ReadAllText(Path.Combine(root,"child.pid")));
            await Task.Delay(300);
            bool alive;try{using var p=Process.GetProcessById(child);alive=!p.HasExited;}catch(ArgumentException){alive=false;}
            Check(!alive,"owned child stopped with canceled worker");
            await worker.Execute("prepare","ok",lines.Add,CancellationToken.None);
            Check(int.Parse(lines.Last())>0,"worker recovers after cancellation");
        }
        int last=int.Parse(lines.Last());
        bool remains;try{using var p=Process.GetProcessById(last);remains=!p.HasExited;}catch(ArgumentException){remains=false;}
        Check(!remains,"batch disposal closes worker");
        Console.WriteLine($"{passed} worker checks passed");
    }
}
