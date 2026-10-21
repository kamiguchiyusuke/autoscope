using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Xml.Linq;
namespace MT4BatchReporter;
public static class Mt4MouseSettings
{
    public static bool TdsInstalled()
    {
        foreach(var root in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)})
            if(File.Exists(Path.Combine(root,"eareview.net","Tick Data Suite","TDSLoader.exe")))return true;
        foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
            try{using var hive=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view);using var key=hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\terminal.exe");string command=key?.GetValue("Debugger") as string??"";var match=Regex.Match(command,"^\\\"?(.+?TDSLoader\\.exe)",RegexOptions.IgnoreCase);if(match.Success&&File.Exists(match.Groups[1].Value))return true;}catch(System.Security.SecurityException){}
        return false;
    }
    public static IReadOnlyList<BrokerSymbol> Symbols(string data)
    {
        string history=Path.Combine(data,"history");if(!Directory.Exists(history))return [];
        // MT4 owns server selection. Only list exact names cached by this MT4, never infer broker aliases.
        var result=new List<BrokerSymbol>();
        foreach(string dir in Directory.GetDirectories(history))
            try{result.AddRange(SymbolCatalog.Load(data,Path.GetFileName(dir)).Symbols);}catch(IOException){}
        return result.DistinctBy(x=>x.Name).OrderBy(x=>x.Name).ToArray();
    }
    public static byte[] VariableSpreadConfig(byte[] original,bool utc=false)
    {
        using var input=new MemoryStream(original);var doc=XDocument.Load(input);var global=doc.Root?.Element("global")??throw new InvalidDataException("MT4のTDS設定を一度開いて保存してください。");
        foreach(string key in new[]{"UseTickDataEnabled","UseVariableSpread"})
        {var setting=global.Elements("add").FirstOrDefault(e=>(string?)e.Attribute("key")==key)??throw new InvalidDataException("未対応のTDS設定形式です。TDSの設定を確認してください。");setting.SetAttributeValue("value","True");}
        if(utc)foreach(string key in new[]{"GMTOffset","DST"})
        {
            var item=global.Elements("add").FirstOrDefault(e=>(string?)e.Attribute("key")==key)??throw new InvalidDataException("TDSの時刻設定がありません。");
            item.SetAttributeValue("value","0");
        }
        using var output=new MemoryStream();doc.Save(output);return output.ToArray();
    }
}
