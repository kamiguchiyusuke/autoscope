using System.Text.RegularExpressions;
namespace MT4BatchReporter;
public sealed record Mt4Installation(string Name,string TerminalFile,string DataFolder,bool Portable)
{
    public override string ToString() => Name;
    public string Detail => TerminalFile+Environment.NewLine+DataFolder;
}
public static class Mt4Installations
{
    public static IReadOnlyList<Mt4Installation> Discover(string terminalRoot,IEnumerable<string> programRoots)
    {
        var entries=new List<Mt4Installation>();
        if(Directory.Exists(terminalRoot))
            foreach(string data in Directories(terminalRoot))
                try
                {
                    string origin=Path.Combine(data,"origin.txt");if(!File.Exists(origin))continue;
                    string home=Path.GetFullPath(File.ReadAllText(origin).Trim());string exe=Path.Combine(home,File.Exists(Path.Combine(home,"terminal64.exe"))?"terminal64.exe":"terminal.exe");
                    if(File.Exists(exe)&&Directory.Exists(Path.Combine(data,Path.GetFileName(exe)=="terminal64.exe"?"MQL5":"MQL4","Experts")))entries.Add(Create(exe,data,false));
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){}
        foreach(string root in programRoots.Where(Directory.Exists))
            foreach(string home in Directories(root))
            {
                string exe=Path.Combine(home,File.Exists(Path.Combine(home,"terminal64.exe"))?"terminal64.exe":"terminal.exe");
                if(File.Exists(exe)&&Directory.Exists(Path.Combine(home,Path.GetFileName(exe)=="terminal64.exe"?"MQL5":"MQL4","Experts"))&&!entries.Any(x=>SamePath(x.TerminalFile,exe)))entries.Add(Create(exe,home,true));
            }
        var distinct=entries.DistinctBy(x=>x.TerminalFile.ToUpperInvariant()+"|"+x.DataFolder.ToUpperInvariant()).OrderBy(x=>x.Name).ToList();
        return distinct.Select(x=>distinct.Count(y=>y.Name==x.Name)>1?x with{Name=x.Name+"（"+Path.GetFileName(x.DataFolder)+"）"}:x).ToArray();
    }
    private static IEnumerable<string> Directories(string root)
    {try{return Directory.GetDirectories(root);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return [];}}
    public static bool SamePath(string a,string b)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),StringComparison.OrdinalIgnoreCase);
    public static Mt4Installation Create(string exe,string data,bool portable)
    {
        string name=Path.GetFileName(Path.GetDirectoryName(exe))!;
        if(Path.GetFileName(exe).Equals("terminal64.exe",StringComparison.OrdinalIgnoreCase))return new(Regex.Replace(name,@"(?:MetaTrader\s*5|MT5)(?:\s+Terminal)?","MT5",RegexOptions.IgnoreCase)+(name.Contains("MT5",StringComparison.OrdinalIgnoreCase)||name.Contains("MetaTrader 5",StringComparison.OrdinalIgnoreCase)?"":" MT5"),exe,data,portable);
        name=Regex.Replace(name,@"MetaTrader\s*4(?:\s+Terminal)?","MT4",RegexOptions.IgnoreCase);
        name=Regex.Replace(name,@"MT4\s+Terminal","MT4",RegexOptions.IgnoreCase);
        if(!name.Contains("MT4",StringComparison.OrdinalIgnoreCase))name+=" MT4";
        return new(name,exe,data,portable);
    }
}

