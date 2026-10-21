using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace MT4BatchReporter;

public sealed class TestJob : INotifyPropertyChanged
{
    public TestJob()
    {
        To = DateTime.Today.AddDays(-1);
        From = To.AddYears(-15);
    }
    private string name="テスト";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get=>name; set {if(name==value)return;name=value;PropertyChanged?.Invoke(this,new(nameof(Name)));} }
    public string EaFile { get; set; } = "";
    public string SetFile { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string EaName => Path.GetFileName(EaFile);
    [System.Text.Json.Serialization.JsonIgnore] public string SetName => Path.GetFileName(SetFile);
    public string Symbol { get; set; } = "EURUSD";
    public string Period { get; set; } = "H1";
    public int Model { get; set; }
    public int Spread { get; set; } = 20;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public decimal Deposit { get; set; } = 1000000;
    public string Currency { get; set; } = "JPY";
    public int Positions { get; set; } = 2;
    [System.Text.Json.Serialization.JsonIgnore] public string Status { get; set; } = "待機";
    [System.Text.Json.Serialization.JsonIgnore] public string SavedReport { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public ReportAssessment? Assessment { get; set; }
    public TestJob Clone() => JsonSerializer.Deserialize<TestJob>(JsonSerializer.Serialize(this))!;
}

public sealed class BatchSettings
{
    public int SchemaVersion { get; set; } = 1;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsMt5 => Path.GetFileName(TerminalFile).Equals("terminal64.exe",StringComparison.OrdinalIgnoreCase);
    public string TerminalFile { get; set; } = "";
    public string DataFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public int TimeoutMinutes { get; set; } = 60;
    public int RetryCount { get; set; } = 1;
    public bool Portable { get; set; }
    public bool UseCurrentServer { get; set; }
    public bool VariableSpreadTds { get; set; }
    public bool TdsTimeZoneUtc { get; set; }
    public bool? UseTds { get; set; }
    public bool DisableTickDataSuite { get; set; }
    public bool CancelStartupDialogs { get; set; } = true;
    public string ServerName { get; set; } = "";
    public List<TestJob> Jobs { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static BatchSettings Load(string path)
    {
        if (new FileInfo(path).Length > 10 * 1024 * 1024) throw new InvalidDataException("設定ファイルが大きすぎます。");
        var s = JsonSerializer.Deserialize<BatchSettings>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("設定を読み込めません。");
        if (s.SchemaVersion != 1 || s.Jobs is null || s.Jobs.Count > 10000 || s.Jobs.Any(j => j is null)) throw new InvalidDataException("設定の形式・版を確認してください。");
        return s;
    }
    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions), new System.Text.UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}

public static class JobFiles
{
    public static readonly string[] Periods = ["M1", "M5", "M15", "M30", "H1", "H4", "D1", "W1", "MN"];
    public static string EaRelativeName(TestJob j, string data, bool mt5=false)
    {
        string root = Path.GetFullPath(Path.Combine(data, mt5?"MQL5":"MQL4", "Experts"));
        string file = Path.GetFullPath(j.EaFile);
        if (!Within(file, root) || !File.Exists(file) || !Path.GetExtension(file).Equals(mt5?".ex5":".ex4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{j.Name}: EAは選択したMTのExperts内の対応形式（MT4: .ex4 / MT5: .ex5）を選んでください。");
        return Path.ChangeExtension(Path.GetRelativePath(root, file), null)!;
    }
    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void Validate(BatchSettings s)
    {
        if (!File.Exists(s.TerminalFile) || !(s.IsMt5 || Path.GetFileName(s.TerminalFile).Equals("terminal.exe", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("MT4のterminal.exeまたはMT5のterminal64.exeを選択してください。");
        if (!Directory.Exists(Path.Combine(s.DataFolder, s.IsMt5?"MQL5":"MQL4", "Experts"))) throw new InvalidDataException("データフォルダーにはMQL4\\Expertsが必要です。MT4の「ファイル→データフォルダを開く」で確認してください。");
        if (s.Portable && !Path.GetFullPath(s.DataFolder).Equals(Path.GetFullPath(Path.GetDirectoryName(s.TerminalFile)!), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ポータブルモードのデータフォルダーはterminal.exeのフォルダーを指定してください。");
        if(!s.Portable)
        {
            string origin=Path.Combine(s.DataFolder,"origin.txt");
            if(!File.Exists(origin) || !Path.GetFullPath(File.ReadAllText(origin).Trim()).Equals(Path.GetFullPath(Path.GetDirectoryName(s.TerminalFile)!),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("選択したMT4とデータフォルダーが一致しません。MT4の「データフォルダを開く」で確認してください。");
        }
        if (s.Jobs.Count == 0) throw new InvalidDataException("テストを1件以上追加してください。");
        if (s.ServerName.IndexOfAny(['\r', '\n', '=', '"']) >= 0) throw new InvalidDataException("サーバー名に使用できない文字があります。");
        if (string.IsNullOrWhiteSpace(s.OutputFolder) || s.TimeoutMinutes is < 1 or > 1440 || s.RetryCount is < 0 or > 3) throw new InvalidDataException("保存先・タイムアウト・再試行回数を確認してください。");
        foreach (var j in s.Jobs)
        {
            if (string.IsNullOrWhiteSpace(j.Name) || string.IsNullOrWhiteSpace(j.Symbol) || j.Symbol.IndexOfAny(['\r', '\n', '=', '"']) >= 0) throw new InvalidDataException("テスト名と銘柄を確認してください。");
            var relative = EaRelativeName(j, s.DataFolder,s.IsMt5);
            if (relative.IndexOfAny(['\r', '\n', '=']) >= 0) throw new InvalidDataException("EAのファイル名に使用できない文字があります。");
            if (!string.IsNullOrWhiteSpace(j.SetFile) && (!File.Exists(j.SetFile) || !Path.GetExtension(j.SetFile).Equals(".set", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException($"{j.Name}: setファイルが見つかりません。");
            if (!Periods.Contains(j.Period) || (j.Model < 0 || j.Model > (s.IsMt5?4:2) || (s.IsMt5 && j.Model==3)) || j.Spread is < 0 or > 100000 || j.Deposit <= 0 || j.Positions is < 0 or > 2 || j.From.Date >= j.To.Date || !System.Text.RegularExpressions.Regex.IsMatch(j.Currency, "^[A-Za-z]{3}$")) throw new InvalidDataException($"{j.Name}: 日付・モデル・入金・通貨等を確認してください。");
        }
    }
    public static string Startup(TestJob j, string ea, string report, string server = "") => (string.IsNullOrWhiteSpace(server) ? "" : $"Server={server}\r\n") + string.Join("\r\n", new[] {
        "; MT4 Batch Reporter - native strategy tester report", "ExpertsEnable=false", "EnableNews=false", $"TestExpert={ea}", "TestExpertParameters=", $"TestSymbol={j.Symbol}", $"TestPeriod={j.Period}", $"TestModel={j.Model}", $"TestSpread={j.Spread}", "TestOptimization=false", "TestDateEnable=true", $"TestFromDate={j.From:yyyy.MM.dd}", $"TestToDate={j.To:yyyy.MM.dd}", $"TestReport={report}", "TestReplaceReport=true", "TestShutdownTerminal=true", "TestVisualEnable=false", "" });
    public static string TesterIni(TestJob j)
    {
        var text = "";
        if (!string.IsNullOrWhiteSpace(j.SetFile))
        {
            var bytes = File.ReadAllBytes(j.SetFile);
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("setファイルが大きすぎます。");
            if (bytes.Length >= 2 && ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes[0] == 0xfe && bytes[1] == 0xff)))
            { using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true); text = reader.ReadToEnd(); }
            else
            { try { text = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff'); } catch (System.Text.DecoderFallbackException) { text = System.Text.Encoding.GetEncoding(932).GetString(bytes); } }
            if (text.Contains("<inputs>", StringComparison.OrdinalIgnoreCase) || text.Contains("</inputs>", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("setファイルにINIセクションが含まれています。MT4で保存した.setを指定してください。");
        }
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").TrimEnd('\r', '\n');
        return $"<common>\r\npositions={j.Positions}\r\ndeposit={j.Deposit.ToString(CultureInfo.InvariantCulture)}\r\ncurrency={j.Currency.ToUpperInvariant()}\r\nfitnes=0\r\ngenetic=0\r\n</common>\r\n\r\n<inputs>\r\n{text}\r\n</inputs>\r\n\r\n<limits>\r\nbalance_enable=0\r\nbalance=200.00\r\nprofit_enable=0\r\nprofit=10000.00\r\nmarginlevel_enable=0\r\nmarginlevel=30.00\r\nmaxdrawdown_enable=0\r\nmaxdrawdown=70.00\r\nconsecloss_enable=0\r\nconsecloss=5000.00\r\nconseclossdeals_enable=0\r\nconseclossdeals=10.00\r\nconsecwin_enable=0\r\nconsecwin=10000.00\r\nconsecwindeals_enable=0\r\nconsecwindeals=30.00\r\n</limits>\r\n";
    }
    public static void WriteMt4(string path, string value)
    {
        var encoding = System.Text.Encoding.GetEncoding(932, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback);
        var bytes = encoding.GetBytes(value); // Reject unrepresentable characters, never silently change EA inputs.
        File.WriteAllBytes(path, bytes);
    }
    public static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.');
}
