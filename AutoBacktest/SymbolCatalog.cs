using System.Text;
using System.Text.RegularExpressions;

namespace MT4BatchReporter;

public sealed record BrokerSymbol(string Name, string Description, string AlternateName, string BaseCurrency)
{
    public override string ToString() => Name;
}

public sealed class SymbolCatalog
{
    public IReadOnlyList<BrokerSymbol> Symbols { get; private init; } = [];
    public string Source { get; private init; } = "";
    private static readonly HashSet<string> Currencies = new("USD EUR GBP JPY CHF CAD AUD NZD CNH CNY HKD SGD NOK SEK DKK PLN CZK HUF TRY ZAR MXN RUB BRL XAU XAG XPT XPD BTC ETH".Split(' '));

    public static SymbolCatalog Load(string data, string server)
    {
        string history = Path.GetFullPath(Path.Combine(data, "history"));
        if (!Directory.Exists(history)) return new();
        string folder;
        if (!string.IsNullOrWhiteSpace(server))
        {
            folder = Path.GetFullPath(Path.Combine(history, server));
            if (!JobFiles.Within(folder, history)) throw new InvalidDataException("サーバー名はhistory内のフォルダー名を指定してください。");
        }
        else
        {
            if (Directory.GetDirectories(history).Any(x => File.Exists(Path.Combine(x, "symbols.raw"))))
                throw new InvalidDataException("銘柄一覧を実行先と一致させるため、サーバー名を指定してください（history内のフォルダー名）。");
            return new();
        }
        string path = Path.Combine(folder, "symbols.raw");
        if (!File.Exists(path)) return new();
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (file.Length == 0 || file.Length > 1936L * 100000 || file.Length % 1936 != 0) throw new InvalidDataException("銘柄一覧の形式を確認できません。MT4で一覧を更新してください。");
        var symbols = new List<BrokerSymbol>();
        var record = new byte[1936];
        string Field(int offset, int count) => Encoding.GetEncoding(932).GetString(record, offset, count).Split('\0')[0].Trim();
        while (file.Position < file.Length)
        {
            file.ReadExactly(record);
            string name = Field(0, 12);
            if (name.Length is < 1 or > 11 || name.Any(c => c < 33 || c > 126 || "\\/\"=".Contains(c))) throw new InvalidDataException("銘柄一覧に未対応のデータがあります。自動照合を停止しました。");
            symbols.Add(new(name, Field(12, 64), Field(76, 12), Field(88, 12)));
        }
        return new() { Source = path, Symbols = symbols.DistinctBy(x => x.Name, StringComparer.Ordinal).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray() };
    }

    private static string Core(string name)
    {
        string upper = name.ToUpperInvariant();
        var pairs = new HashSet<string>();
        for (int i = 0; i + 6 <= upper.Length; i++)
        {
            string a = upper.Substring(i, 3), b = upper.Substring(i + 3, 3);
            if (a != b && Currencies.Contains(a) && Currencies.Contains(b)) pairs.Add(a + b);
        }
        if (pairs.Count == 1) return pairs.Single();
        // Only common spot-gold spellings are inferred without broker metadata.
        // Unknown names remain selectable verbatim in the broker list.
        if (Regex.IsMatch(upper, @"^(?:[.#_!-]|PRO|RAW|M)*GOLD(?:MICRO|PRO|RAW|M|[.#_!0-9-])*$") ||
            Regex.IsMatch(upper, @"(?:^|[.#_!-])GOLD(?:$|[.#_!-])")) return GoldIdentity(name, "");
        return "";
    }

    private static string GoldIdentity(string name, string description)
    {
        var quotes = new HashSet<string>();
        string text = (name + " " + description).ToUpperInvariant();
        foreach (Match pair in Regex.Matches(text, @"(?:XAU|GOLD[.#_!-]?)([A-Z]{3})"))
            if (Currencies.Contains(pair.Groups[1].Value) && !pair.Groups[1].Value.StartsWith('X')) quotes.Add(pair.Groups[1].Value);
        foreach (Match token in Regex.Matches(text, @"\b[A-Z]{3}\b"))
            if (Currencies.Contains(token.Value) && !token.Value.StartsWith('X') && token.Value is not ("BTC" or "ETH")) quotes.Add(token.Value);
        foreach (var (pattern, currency) in new[] { (@"\bEURO\b|ユーロ", "EUR"), (@"\bYEN\b|日本円", "JPY"), (@"\bSTERLING\b", "GBP"), (@"\bUS\s*DOLLAR", "USD") })
            if (Regex.IsMatch(text, pattern)) quotes.Add(currency);
        if (quotes.Count > 1) return "";
        return "XAU" + (quotes.Count == 1 ? quotes.Single() : "USD");
    }

    private static bool Excluded(BrokerSymbol symbol) => Regex.IsMatch(symbol.Description, @"\b(futures?|forward|stock|shares?|equity|etf|mining|company|corporation)\b", RegexOptions.IgnoreCase);

    private static string Core(BrokerSymbol symbol)
    {
        if (Excluded(symbol)) return "";
        string core = Core(symbol.Name);
        string alternate = Core(symbol.AlternateName);
        if ((symbol.Name + symbol.AlternateName).Contains("GOLD", StringComparison.OrdinalIgnoreCase) &&
            (core.Length > 0 || alternate.Length > 0 || symbol.BaseCurrency.Equals("XAU", StringComparison.OrdinalIgnoreCase)))
            return GoldIdentity(symbol.Name + " " + symbol.AlternateName, symbol.Description);
        if (core.Length > 0) return core;
        core = alternate;
        if (core.Length > 0) return core;
        if (symbol.BaseCurrency.Equals("XAU", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(symbol.Description, @"\bUSD\b|US\s*Dollar", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(symbol.Description, @"future|forward", RegexOptions.IgnoreCase)) return "XAUUSD";
        return "";
    }

    public IReadOnlyList<BrokerSymbol> Candidates(string requested)
    {
        requested = requested.Trim();
        var exact = Symbols.Where(x => x.Name == requested).ToArray();
        if (exact.Length > 0) return exact;
        var caseMatch = Symbols.Where(x => x.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (caseMatch.Length > 0) return caseMatch;
        string core = Core(requested);
        return Symbols.Where(x => !Excluded(x) && (core.Length > 0 ? Core(x) == core :
            x.AlternateName.Length > 0 && x.AlternateName.Equals(requested, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public string Resolve(string requested)
    {
        if (Symbols.Count == 0) return requested.Trim(); // Older/manual environments have no cached catalog.
        var matches = Candidates(requested);
        if (matches.Count == 1) return matches[0].Name;
        if (matches.Count == 0) throw new InvalidDataException($"銘柄「{requested}」に対応する実銘柄がありません。条件編集の一覧から選択してください。");
        throw new InvalidDataException($"銘柄「{requested}」の候補が複数あります。一覧から選択してください: {string.Join(", ", matches.Select(x => x.Name))}");
    }

    public static TestJob ResolveJob(BatchSettings settings, TestJob job)
    {
        var resolved = job.Clone();
        resolved.Symbol = settings.UseCurrentServer ? job.Symbol : Load(settings.DataFolder, settings.ServerName).Resolve(job.Symbol);
        return resolved;
    }
}
