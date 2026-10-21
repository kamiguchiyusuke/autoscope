using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MT4BatchReporter;

public sealed partial class BatchRunner
{
    private readonly Func<ProcessStartInfo, CancellationToken, Task> execute;
    private bool cancelStartupDialogs;
    private TdsWorker? tdsWorker;
    public BatchRunner(Func<ProcessStartInfo, CancellationToken, Task>? terminalExecutor = null) => execute = terminalExecutor ?? ExecuteTerminal;
    private async Task ExecuteTerminal(ProcessStartInfo start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var launchedAt = DateTime.UtcNow.AddSeconds(-1);
        using var p = Process.Start(start) ?? throw new IOException("MT4を起動できません。");
        var owned = new Dictionary<int, Process>();
        var startupDialogs = new StartupDialogCanceller(cancelStartupDialogs, Say);
        string image = "起動ローダーは終了済み、またはイメージ情報取得不可";
        try { image = p.MainModule?.FileName ?? image; } catch { }
        Say($"起動PID {p.Id} / {image}");
        try
        {
            int quiet = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(start.FileName)))
                {
                    try
                    {
                        if (candidate.StartTime.ToUniversalTime() >= launchedAt &&
                            Path.GetFullPath(candidate.MainModule!.FileName!).Equals(Path.GetFullPath(start.FileName), StringComparison.OrdinalIgnoreCase) &&
                            (candidate.Id == p.Id || NativeProcessCommandLine.HasArgument(NativeProcessCommandLine.Read(candidate.Id), start.ArgumentList[0])))
                        {
                            startupDialogs.Poll(candidate.Id);
                            if (candidate.Id != p.Id && !owned.ContainsKey(candidate.Id))
                            { owned.Add(candidate.Id, candidate); Say($"MT4本体PID {candidate.Id} を確認しました。"); continue; }
                        }
                    }
                    catch { }
                    candidate.Dispose();
                }
                bool active = !p.HasExited || owned.Values.Any(x => !x.HasExited);
                quiet = active ? 0 : quiet + 1;
                if (quiet >= 8) break; // Allow a loader to transfer execution to the real terminal.
                await Task.Delay(250, token);
            }
            Say($"起動プロセス終了コード {p.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            if (!p.HasExited) p.Kill(true);
            foreach (var terminal in owned.Values) if (!terminal.HasExited) terminal.Kill(true);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((!p.HasExited || owned.Values.Any(x => !x.HasExited)) && DateTime.UtcNow < deadline) await Task.Delay(250);
            if (!p.HasExited || owned.Values.Any(x => !x.HasExited)) throw new IOException("起動したMT4の終了を確認できません。MT4を閉じてから設定を復元してください。");
            throw;
        }
        finally { foreach (var terminal in owned.Values) terminal.Dispose(); }
    }
    private volatile bool stopAfterCurrent;
    private readonly CancellationTokenSource cancel = new();
    public Action<string>? Log;
    public Func<TestJob, Task>? ReportSavedAsync;
    public Action<TestJob, string>? Status;
    public string RunFolder { get; private set; } = "";
    public string DiagnosticsFolder { get; private set; } = "";
    private string DiagnosticJobFolder(int index){var path=Path.Combine(DiagnosticsFolder,$"{index+1:D4}");Directory.CreateDirectory(path);return path;}
    public void StopAfterCurrent() => stopAfterCurrent = true;
    public void StopNow() => cancel.Cancel();
    public async Task PrepareAsync(BatchSettings s)
    {
        JobFiles.Validate(s);
        cancelStartupDialogs = false; // Preparation is for the human to inspect or log in.
        if (TerminalIsRunning(s.TerminalFile)) throw new IOException("検証用MT4を閉じてから準備を開始してください。");
        Directory.CreateDirectory(Path.Combine(s.DataFolder, "tester"));
        using var lease = new FileStream(Path.Combine(s.DataFolder, "tester", ".mt4batchreporter.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (HasRecovery(s.DataFolder)) throw new IOException("先に設定を復元してください。");
        string folder = Path.Combine(s.DataFolder, "tester", "BatchReportApp", "prepare");
        Directory.CreateDirectory(folder);
        string config = Path.Combine(folder, "startup.txt");
        JobFiles.WriteMt4(config, (s.ServerName.Length > 0 ? $"Server={s.ServerName}\r\n" : "") + "ExpertsEnable=false\r\nEnableNews=false\r\n");
        var start = new ProcessStartInfo(s.TerminalFile) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(s.TerminalFile)!, WindowStyle = ProcessWindowStyle.Normal };
        start.ArgumentList.Add(config);
        if (s.Portable) start.ArgumentList.Add("/portable");
        if (s.DisableTickDataSuite) start.ArgumentList.Add("/notds");
        await ExecuteTerminal(start, CancellationToken.None); // The human closes MT4 after inspecting its initial screen.
    }
    private void Say(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {message}";
        if (DiagnosticsFolder.Length > 0) File.AppendAllText(Path.Combine(DiagnosticsFolder, "execution.log"), line + Environment.NewLine, Encoding.UTF8);
        Log?.Invoke(line);
    }
    private void Set(TestJob j, string state) { j.Status = state; Status?.Invoke(j, state); }
    public static bool TerminalIsRunning(string path)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (p)
            {
                try { if (p.HasExited) continue; if (Path.GetFullPath(p.MainModule!.FileName!).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return true; }
                catch
                {
                    try { if (p.HasExited) continue; } catch { }
                    throw new IOException("起動中のMTを確認できません。実行対象のMT4 / MT5を閉じてから再試行してください。");
                }
            }
        }
        return false;
    }
    private static string PendingFile(string data) => Path.Combine(data, "tester", ".mt4batchreporter.pending.json");
    private sealed class Recovery
    {
        public Mt4Profile? Profile { get; set; }
        public string? TdsBackup { get; set; }
        public string? TdsHash { get; set; }
        public string IniFile { get; set; } = "";
        public string BackupFile { get; set; } = "";
        public string BackupHash { get; set; } = "";
        public bool Existed { get; set; }
    }
    public static bool HasRecovery(string data) => File.Exists(PendingFile(data));
    public static void Recover(string data)
    {
        string pending = PendingFile(data);
        if (!File.Exists(pending)) return;
        var r = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(pending)) ?? throw new InvalidDataException("復元情報を読めません。");
        string tester = Path.GetFullPath(Path.Combine(data, "tester"));
        if (!Path.GetDirectoryName(Path.GetFullPath(r.IniFile))!.Equals(tester, StringComparison.OrdinalIgnoreCase) || !Path.GetExtension(r.IniFile).Equals(".ini", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("復元対象がtester内のINIではありません。");
        if(r.TdsBackup!=null)
        {
            if(!JobFiles.Within(r.TdsBackup,Path.Combine(tester,"BatchReportApp","state")))throw new InvalidDataException("TDS復元先を確認できません。");
            var tdsBytes=File.ReadAllBytes(r.TdsBackup);if(Convert.ToHexString(SHA256.HashData(tdsBytes))!=r.TdsHash)throw new InvalidDataException("TDSバックアップが破損しています。");
            File.WriteAllBytes(Path.Combine(data,"config","tds.config"),tdsBytes);
        }
        if (r.Existed)
        {
            if (!JobFiles.Within(r.BackupFile, Path.Combine(tester, "BatchReportApp", "state"))) throw new InvalidDataException("バックアップの場所が不正です。");
            var bytes = File.ReadAllBytes(r.BackupFile);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(r.BackupHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("バックアップの整合性を確認できません。");
            var temp = r.IniFile + ".restore-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, r.IniFile, true);
        }
        else File.Delete(r.IniFile);
        r.Profile?.Restore(data);
        File.Delete(pending);
    }
    public static void RecoverSafely(string terminal, string data)
    {
        if (TerminalIsRunning(terminal)) throw new IOException("INIの復元前に実行対象のMT4 / MT5を閉じてください。");
        using var lease = new FileStream(Path.Combine(data, "tester", ".mt4batchreporter.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Recover(data);
    }
    private static string PrepareIni(string data, string runId, TestJob j, bool variableTds,bool utc=false)
    {
        string tester = Path.Combine(data, "tester");
        Directory.CreateDirectory(tester);
        if (HasRecovery(data)) throw new IOException("前回の復元待ちINIがあります。「設定を復元」を実行してください。");
        string ini = Path.Combine(tester, Path.GetFileNameWithoutExtension(j.EaFile) + ".ini");
        string dir = Path.Combine(tester, "BatchReportApp", "state", runId, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var r = new Recovery { IniFile = ini, BackupFile = Path.Combine(dir, "original.ini"), Existed = File.Exists(ini) };
        r.Profile=Mt4Profile.Capture(data);
        if (r.Existed) { var bytes = File.ReadAllBytes(ini); File.WriteAllBytes(r.BackupFile, bytes); r.BackupHash = Convert.ToHexString(SHA256.HashData(bytes)); }
        byte[]? updatedTds=null;
        if(variableTds)
        {
            var original=File.ReadAllBytes(Path.Combine(data,"config","tds.config"));updatedTds=Mt4MouseSettings.VariableSpreadConfig(original,utc);
            r.TdsBackup=Path.Combine(dir,"tds-original.config");r.TdsHash=Convert.ToHexString(SHA256.HashData(original));File.WriteAllBytes(r.TdsBackup,original);
        }
        File.WriteAllText(PendingFile(data), JsonSerializer.Serialize(r), Encoding.UTF8);
        r.Profile.Begin(data);
        if(updatedTds!=null)File.WriteAllBytes(Path.Combine(data,"config","tds.config"),updatedTds);
        JobFiles.WriteMt4(ini, JobFiles.TesterIni(j));
        return r.Profile.Name;
    }
    public async Task RunAsync(BatchSettings s)
    {
        JobFiles.Validate(s);
        cancelStartupDialogs = s.CancelStartupDialogs;
        if (TerminalIsRunning(s.TerminalFile)) throw new IOException("実行対象のMT4 / MT5が起動中です。テスト対象のMTを閉じてから実行してください。");
        Directory.CreateDirectory(Path.Combine(s.DataFolder, "tester"));
        using var lease = new FileStream(Path.Combine(s.DataFolder, "tester", ".mt4batchreporter.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (HasRecovery(s.DataFolder)) throw new IOException("前回の復元待ちINIがあります。「設定を復元」を実行してください。");
        using var worker = s.IsMt5 && s.UseTds==true ? new TdsWorker() : null;
        tdsWorker=worker;
        worker?.Start();
        var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8];
        RunFolder = Path.Combine(Path.GetFullPath(s.OutputFolder), runId);
        Directory.CreateDirectory(RunFolder);
        DiagnosticsFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArmadaScopeFX","AutoBacktestRuns",runId);
        Directory.CreateDirectory(DiagnosticsFolder);
        s.Save(Path.Combine(DiagnosticsFolder, "settings.json"));
        Say($"{s.Jobs.Count}件の連続バックテストを開始します。");
        int complete = 0, fail = 0;
        for (int i = 0; i < s.Jobs.Count; i++)
        {
            var j = s.Jobs[i];
            if (stopAfterCurrent || cancel.IsCancellationRequested) { Set(j, "未実行（中止）"); continue; }
            bool succeeded = false;
            for (int attempt = 0; attempt <= s.RetryCount && !cancel.IsCancellationRequested; attempt++)
            {
                if (attempt > 0) Say($"{j.Name}: 再試行 {attempt}/{s.RetryCount}");
                try
                {
                    await Attempt(s, j, i, attempt, runId);
                    j.Assessment = ReportInspector.Inspect(j.SavedReport, j, s.UseCurrentServer?"":s.ServerName,s.IsMt5);
                    if(s.IsMt5 && (j.Model==4||s.UseTds==true))
                    {
                        string log=Path.Combine(DiagnosticJobFolder(i),"mt5-run.log");
                        if(File.Exists(log) && Regex.IsMatch(File.ReadAllText(log),"real ticks absent|no real ticks|real ticks discarded",RegexOptions.IgnoreCase))
                            j.Assessment=j.Assessment with{Warnings=[..j.Assessment.Warnings,"リアルティックに欠損があります。MT5が生成ティックで補完した部分を含む可能性があります。MT5実行ログを確認してください。"]};
                    }
                    Set(j, j.Assessment.Outcome switch { ReportOutcome.Valid => "保存完了", ReportOutcome.Invalid => "保存済み／テスト不成立", _ => "保存済み／内容確認不可" });
                    Say(j.Assessment.Summary);
                    foreach (string warning in j.Assessment.Warnings) Say(warning);
                    if(j.Assessment.Outcome == ReportOutcome.Valid && ReportSavedAsync != null)
                    {
                        try { await ReportSavedAsync(j); }
                        catch(OperationCanceledException) { Set(j,"HTML保存済み／PDF中止"); stopAfterCurrent=true; }
                        catch(Exception ex) { Set(j,"HTML保存済み／取込・PDF失敗"); Say(j.Name+": "+ex.ToString()); }
                    }
                    succeeded = true;
                    if (j.Assessment.Outcome == ReportOutcome.Valid && !j.Status.Contains("失敗") && !j.Status.Contains("中止")) complete++; else fail++;
                    break;
                }
                catch (OperationCanceledException) { Set(j, "中止"); Say($"{j.Name}: 実行を中止しました。"); break; }
                catch (Exception ex)
                {
                    Say($"{j.Name}: {ex.Message}");
                    Set(j, attempt < s.RetryCount ? "再試行待ち" : "失敗");
                    if (HasRecovery(s.DataFolder) || TerminalIsRunning(s.TerminalFile)) throw new IOException("MT4またはINIの復元状態を確認する必要があるため、次のテストを停止しました。", ex);
                }
            }
            if (!succeeded && !cancel.IsCancellationRequested) fail++;
            File.WriteAllText(Path.Combine(DiagnosticsFolder, "results.json"), JsonSerializer.Serialize(s.Jobs.Select(x => new { x.Name, x.Status, x.SavedReport, x.Assessment }), BatchSettings.JsonOptions), Encoding.UTF8);
        }
        File.WriteAllText(Path.Combine(DiagnosticsFolder, "results.json"), JsonSerializer.Serialize(s.Jobs.Select(x => new { x.Name, x.Status, x.SavedReport, x.Assessment }), BatchSettings.JsonOptions), Encoding.UTF8);
        Say($"終了：保存完了 {complete}件／失敗 {fail}件。保存先：{RunFolder}");
    }
    private async Task Attempt(BatchSettings s, TestJob j, int index, int attempt, string runId)
    {
        if(s.IsMt5){await AttemptMt5(s,j,index,attempt,runId);return;}
        var preparation=Stopwatch.StartNew();
        if (TerminalIsRunning(s.TerminalFile)) throw new IOException("MT4が既に起動しているため、新規起動を停止しました。");
        string stem = $"{index + 1:D4}_{JobFiles.SafeName(j.Name)}";
        if (stem.Length > 85) stem = stem[..85];
        string relative = Path.Combine("tester", "BatchReportApp", "runs", runId, $"{index + 1:D4}_attempt{attempt}", "report.htm");
        string staging = Path.Combine(s.DataFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        string diagnosticDir=DiagnosticJobFolder(index);
        string jobDir = Path.Combine(RunFolder, stem);
        Directory.CreateDirectory(jobDir);
        string config = Path.Combine(Path.GetDirectoryName(staging)!, "startup.txt");
        var logCursor=EnvironmentReport.CaptureTesterLogs(s.DataFolder);
        var actual = SymbolCatalog.ResolveJob(s, j);
        if(s.VariableSpreadTds){actual.Spread=0;actual.Model=0;}
        Say($"銘柄照合: {j.Symbol} → {actual.Symbol}");
        if (!s.UseCurrentServer && SymbolCatalog.Load(s.DataFolder, s.ServerName).Symbols.Count == 0) Say("銘柄一覧がないため、入力した銘柄名をそのまま使用します。MT4側の銘柄を確認してください。");
        JobFiles.WriteMt4(config, JobFiles.Startup(actual, JobFiles.EaRelativeName(j, s.DataFolder), relative, s.UseCurrentServer?"":s.ServerName));
        File.Copy(config, Path.Combine(diagnosticDir, $"startup_attempt{attempt}.txt"));
        EnvironmentReport.Save(s, Path.Combine(diagnosticDir, $"environment_attempt{attempt}.json"),actual);
        bool prepared = false, safeToRestore = true;
        try
        {
            prepared = true; string profile=PrepareIni(s.DataFolder, runId, j,s.VariableSpreadTds,s.TdsTimeZoneUtc);
            File.AppendAllText(config,$"Profile={profile}\r\n",Encoding.GetEncoding(932));
            File.Copy(config,Path.Combine(diagnosticDir,$"startup_attempt{attempt}.txt"),true);
            Set(j, "実行中"); Say($"[{index + 1}/{s.Jobs.Count}] {j.Name}: {j.Symbol} {j.Period} / {j.From:yyyy.MM.dd}–{j.To:yyyy.MM.dd}");
            var start = new ProcessStartInfo(s.TerminalFile) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(s.TerminalFile)! };
            start.ArgumentList.Add(config);
            if (s.Portable) start.ArgumentList.Add("/portable");
            if (s.DisableTickDataSuite) start.ArgumentList.Add("/notds");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            limit.CancelAfter(TimeSpan.FromMinutes(s.TimeoutMinutes));
            Say($"MT4起動準備完了: {preparation.Elapsed.TotalMilliseconds:0.0} ms");
            try { await execute(start, limit.Token); }
            catch (OperationCanceledException)
            {
                if (cancel.IsCancellationRequested) throw;
                throw new TimeoutException($"タイムアウト（{s.TimeoutMinutes}分）。起動したMT4を停止しました。");
            }
            if (TerminalIsRunning(s.TerminalFile)) { safeToRestore = false; throw new IOException("別のMT4プロセスが残っています。構成ファイルの適用状態を確認してください。"); }
            Set(j, "標準レポートを保存中");
            string alternate = Path.Combine(Path.GetDirectoryName(s.TerminalFile)!, relative);
            string found = File.Exists(staging) ? staging : alternate;
            await WaitStable(found, cancel.Token);
            j.SavedReport = CollectNativeReport(found, jobDir);
            string namedReport=Path.Combine(jobDir,stem+".htm");
            File.Move(j.SavedReport,namedReport);j.SavedReport=namedReport;
            Say($"{j.Name}: MT4標準レポートと画像を保存しました → {j.SavedReport}");
        }
        finally
        {
            EnvironmentReport.CopyTesterLog(s.DataFolder, diagnosticDir, attempt,logCursor);
            if (TerminalIsRunning(s.TerminalFile)) safeToRestore = false;
            if (prepared && safeToRestore) { Recover(s.DataFolder); Say($"{j.Name}: 元のEAテスター設定を復元しました。"); }
        }
    }
    private static async Task WaitStable(string file, CancellationToken ct)
    {
        long previous = -1; int stable = 0;
        for (int n = 0; n < 15; n++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(file))
            {
                long size = new FileInfo(file).Length;
                stable = size > 0 && size == previous ? stable + 1 : 0;
                if (stable >= 2) return;
                previous = size;
            }
            await Task.Delay(500, ct);
        }
        throw new IOException("標準レポートの生成を確認できません。起動構成・EA・履歴とMT4のtester\\logsを確認してください。");
    }
    public static string CollectNativeReport(string source, string target, bool mt5=false)
    {
        var bytes = File.ReadAllBytes(source);
        if (bytes.Length > 100 * 1024 * 1024) throw new InvalidDataException("レポートが大きすぎます。");
        string text;using(var reader=new StreamReader(new MemoryStream(bytes),mt5?Encoding.UTF8:Encoding.GetEncoding(932),true))text=reader.ReadToEnd();
        if (!Regex.IsMatch(text, @"<html\b", RegexOptions.IgnoreCase) || !Regex.IsMatch(text, @"<table\b", RegexOptions.IgnoreCase) || !Regex.IsMatch(text, @"</html\s*>", RegexOptions.IgnoreCase)) throw new InvalidDataException("標準HTMLが未完了または破損しています。");
        string root = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, "<img\\b[^>]*?\\bsrc\\s*=\\s*(?:\"([^\"]+)\"|'([^']+)'|([^\\s>]+))", RegexOptions.IgnoreCase))
        {
            string raw = WebUtility.HtmlDecode(m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value);
            string name = Uri.UnescapeDataString(raw.Split('?', '#')[0]);
            if (Path.IsPathRooted(name) || name.Contains(':')) throw new InvalidDataException("画像が相対ファイル参照ではないため保存を停止しました。");
            string file = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!JobFiles.Within(file, root) || !File.Exists(file)) throw new InvalidDataException($"レポート画像が見つからないか、保存範囲外です：{raw}");
            images.Add(Path.GetRelativePath(root, file));
        }
        Directory.CreateDirectory(target);
        if (!mt5 && !images.Any(x => Path.GetExtension(x).Equals(".gif", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("標準GIFの参照がありません。");
        foreach (var image in images)
        {
            if (Path.GetExtension(image).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                if (!GifValidator.IsComplete(Path.Combine(root,image))) throw new InvalidDataException("標準GIFが破損しています。");
            }
            string dest = Path.Combine(target, image); Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            CopyVerified(Path.Combine(root, image), dest);
        }
        string report = Path.Combine(target, Path.GetFileName(source));
        CopyVerified(source, report); // Native bytes remain unchanged. No HTML regeneration.
        return report;
    }
    private static void CopyVerified(string source, string destination)
    {
        string tmp = destination + ".partial";
        File.Copy(source, tmp, true);
        using var a = File.OpenRead(source); using var b = File.OpenRead(tmp);
        if (!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b))) throw new IOException("保存ファイルの整合性を確認できません。");
        a.Close(); b.Close(); File.Move(tmp, destination, true);
    }
}
