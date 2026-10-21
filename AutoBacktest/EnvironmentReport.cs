using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace MT4BatchReporter;

public static class EnvironmentReport
{
    public static void Save(BatchSettings settings, string destination, TestJob? current=null)
    {
        var warnings = new List<string>();
        string loader = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + Path.GetFileName(settings.TerminalFile));
            loader = key?.GetValue("Debugger") as string ?? "";
            if (loader.Length > 0) warnings.Add("MT4の起動が外部ローダーに転送されます。ローダー側の画面・エラーも確認してください。");
        }
        catch (Exception ex) { warnings.Add("起動ローダー確認: " + ex.Message); }
        var histories = new List<object>();
        try
        {
            var root = Path.Combine(settings.DataFolder, "history");
            IEnumerable<TestJob> jobs=current is null?settings.Jobs:[current];
            var wanted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var job in jobs)
            {
                int minutes=job.Period switch{"M1"=>1,"M5"=>5,"M15"=>15,"M30"=>30,"H1"=>60,"H4"=>240,"D1"=>1440,"W1"=>10080,"MN"=>43200,_=>1};
                wanted.Add(job.Symbol+"1.hst");wanted.Add(job.Symbol+minutes+".hst");
            }
            if (Directory.Exists(root))
                // Probe only the requested history names in each server folder.
                // Unrelated symbols never enter the per-job file inventory.
                foreach (var path in Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories).Prepend(root)
                    .SelectMany(folder=>wanted.Select(name=>Path.Combine(folder,name))).Where(File.Exists))
                {
                    using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new BinaryReader(file);
                    int version = reader.ReadInt32();
                    if (version != 401 || file.Length < 208) continue;
                    long count = (file.Length - 148) / 60;
                    file.Position = 148; long first = reader.ReadInt64();
                    file.Position = 148 + (count - 1) * 60; long last = reader.ReadInt64();
                    histories.Add(new { file = Path.GetRelativePath(settings.DataFolder, path), bars = count,
                        firstUtc = DateTimeOffset.FromUnixTimeSeconds(first), lastUtc = DateTimeOffset.FromUnixTimeSeconds(last) });
                }
            if (histories.Count == 0) warnings.Add("HST401の履歴を確認できません。TDSやFXTによるデータ利用の可否は、この診断では判定しません。");
        }
        catch (Exception ex) { warnings.Add("履歴確認: " + ex.Message); }
        string versionText = File.Exists(settings.TerminalFile) ? FileVersionInfo.GetVersionInfo(settings.TerminalFile).FileVersion ?? "" : "";
        var result = new { created = DateTimeOffset.Now, terminal = settings.TerminalFile, version = versionText,
            dataFolder = settings.DataFolder, portable = settings.Portable, disableTickDataSuite = settings.DisableTickDataSuite, serverName = settings.ServerName, externalLoader = loader,
            histories, warnings, realBacktestVerified = false,
            note = "対象銘柄のM1・選択時間足だけを診断します。環境診断はバックテストの成功を証明しません。report.htmと参照画像の実生成が必要です。口座設定・パスワードは読みません。" };
        File.WriteAllText(destination, JsonSerializer.Serialize(result, BatchSettings.JsonOptions), new UTF8Encoding(false));
    }
    public static Dictionary<string,(long Length,long Write)> CaptureTesterLogs(string data)
    {
        string folder=Path.Combine(data,"tester","logs");
        try{return Directory.Exists(folder)?Directory.GetFiles(folder,"*.log").ToDictionary(p=>p,p=>(new FileInfo(p).Length,File.GetLastWriteTimeUtc(p).Ticks),StringComparer.OrdinalIgnoreCase):new(StringComparer.OrdinalIgnoreCase);}
        catch(IOException){return new(StringComparer.OrdinalIgnoreCase);}
    }
    public static void CopyTesterLog(string data, string target, int attempt, Dictionary<string,(long Length,long Write)>? before=null, bool mt5=false, string? name=null)
    {
        try
        {
            string folder = Path.Combine(data, "tester", "logs");
            if (!Directory.Exists(folder)) return;
            var selected=new DirectoryInfo(folder).GetFiles("*.log")
                .Where(f=>before==null||!before.TryGetValue(f.FullName,out var old)||old.Length!=f.Length||old.Write!=f.LastWriteTimeUtc.Ticks).OrderBy(f=>f.Name).ToArray();
            if(selected.Length==0)return;
            var head=new List<string>();var tail=new Queue<string>();var important=new List<string>();
            long lines=0;bool missingTicks=false;
            var warnings=new Regex("error|warning|failed|exception|absent|discarded|no real ticks|エラー|失敗|警告",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            var tickWarning=new Regex("real ticks absent|no real ticks|real ticks discarded",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            foreach(var file in selected)
            {
                using var source=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                long offset=before!=null&&before.TryGetValue(file.FullName,out var previous)&&source.Length>previous.Length?previous.Length:0;
                source.Position=offset;
                using var reader=new StreamReader(source,mt5?Encoding.Unicode:Encoding.GetEncoding(932),true);
                while(reader.ReadLine() is string line)
                {
                    lines++;
                    if(head.Count<80)head.Add(line);
                    tail.Enqueue(line);if(tail.Count>200)tail.Dequeue();
                    if(warnings.IsMatch(line)&&important.Count<5000)important.Add(line);
                    if(tickWarning.IsMatch(line))missingTicks=true;
                }
            }
            using var dest=new StreamWriter(Path.Combine(target,name??$"tester_attempt{attempt}.log"),false,new UTF8Encoding(false));
            dest.WriteLine($"今回の実行ログ: {lines:N0}行。先頭・末尾と警告を保存。元のMT4/MT5ログは変更していません。");
            foreach(var line in head.Concat(important).Concat(tail).Distinct())dest.WriteLine(line);
            if(missingTicks)dest.WriteLine("real ticks absent: detected in this run");
        }
        catch { /* Diagnostics must never prevent restoration of the original tester settings. */ }
    }
}
