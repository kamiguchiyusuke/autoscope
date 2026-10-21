using MT4BatchReporter;

internal static class ReviewFixChecks
{
    public static void Run()
    {
        string dir=Path.Combine(Path.GetTempPath(),"GifTest",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string html=Path.Combine(dir,"report.htm");File.WriteAllText(html,"<html><table></table><img src='report.gif'></html>");
        File.WriteAllBytes(Path.Combine(dir,"report.gif"),[71,73,70,56,57,97,1,0,1,0,0,0,0]);
        bool rejected=false;try{BatchRunner.CollectNativeReport(html,Path.Combine(dir,"out"));}catch(InvalidDataException){rejected=true;}
        if(!rejected)throw new Exception("FAIL truncated GIF header accepted");
        Console.WriteLine("PASS header-only GIF is rejected");Directory.Delete(dir,true);
        string config=Path.Combine(Path.GetTempPath(),"unique run","startup.txt");
        if(!NativeProcessCommandLine.HasArgument($"terminal.exe \"{config}\" /notds",config)||NativeProcessCommandLine.HasArgument($"terminal.exe \"{config}.other\" /notds",config)||NativeProcessCommandLine.HasArgument("terminal.exe /portable",config))throw new Exception("FAIL process attribution must require exact unique config argument");
        if(string.IsNullOrEmpty(NativeProcessCommandLine.Read(Environment.ProcessId)))throw new Exception("FAIL native process command line unavailable");
        Console.WriteLine("PASS process attribution requires unique config and reads native process");
    }
}
