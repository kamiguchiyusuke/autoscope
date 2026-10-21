using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
namespace ArmadaAnalyzer;

internal sealed class HtmlRows : IDisposable
{
    readonly StreamReader reader; char[] buffer = ArrayPool<char>.Shared.Rent(65536); int pos, end; bool eof;
    internal long Position => reader.BaseStream.Position;
    internal long Length => reader.BaseStream.Length;
    internal HtmlRows(string path, bool forceCp932 = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var stream = File.OpenRead(path);
        Encoding encoding;
        try
        {
            Span<byte> sample = stackalloc byte[8192];
            int n = stream.Read(sample);
            var prefix = Encoding.ASCII.GetString(sample[..n]);
            bool legacy = prefix.Contains("shift_jis", StringComparison.OrdinalIgnoreCase) || prefix.Contains("windows-31j", StringComparison.OrdinalIgnoreCase) || prefix.Contains("charset=sjis", StringComparison.OrdinalIgnoreCase);
            encoding = new UTF8Encoding(false, true);
            if (legacy || forceCp932)
                encoding = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            else
                try
                {
                    encoding.GetDecoder().GetCharCount(sample[..n], false);
                }
                catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            stream.Position = 0;
            reader = new StreamReader(stream, encoding, true, 65536);
        }
        catch { stream.Dispose(); throw; }
    }
    internal bool Next(out ReadOnlySpan<char> row)
    {
        while (true)
        {
            int b = buffer.AsSpan(pos, end - pos).IndexOf("<tr", StringComparison.OrdinalIgnoreCase);
            if (b >= 0)
            {
                b += pos;
                int e = buffer.AsSpan(b, end - b).IndexOf("</tr>", StringComparison.OrdinalIgnoreCase);
                if (e >= 0)
                {
                    e += b + 5;
                    row = buffer.AsSpan(b, e - b);
                    pos = e;
                    return true;
                }
                pos = b;
            }
            else
                pos = end > 3 ? end - 3 : pos;
            if (eof)
            {
                if (b >= 0)
                    throw new FormatException("閉じられていないHTML行があります");
                row = default;
                return false;
            }
            int left = end - pos;
            buffer.AsSpan(pos, left).CopyTo(buffer);
            pos = 0;
            end = left;
            if (end == buffer.Length)
            {
                if (end >= 2097152)
                    throw new FormatException("HTML行が2MiBの上限を超えました");
                var bigger = ArrayPool<char>.Shared.Rent(buffer.Length * 2);
                buffer.AsSpan(0, end).CopyTo(bigger);
                ArrayPool<char>.Shared.Return(buffer);
                buffer = bigger;
            }
            int n = reader.Read(buffer, end, buffer.Length - end);
            end += n;
            eof = n == 0;
        }
    }
    public void Dispose()
    {
        reader.Dispose();
        ArrayPool<char>.Shared.Return(buffer);
    }
}
internal sealed record ImportMetadata(string Symbol, long Initial, long Closed, int Open, string Warning)
{
    public string Format {get;init;}="MT4";
}
internal static class HtmlImporter
{
    static readonly string[] Formats = ["yyyy.MM.dd HH:mm", "yyyy.MM.dd HH:mm:ss"];
    internal static DateTime ParseTime(ReadOnlySpan<char> text, long row)
    {
        if ((text.Length != 16 && text.Length != 19) || text[4] != '.' || text[7] != '.' || text[10] != ' ' || text[13] != ':' || (text.Length == 19 && text[16] != ':'))
            throw new FormatException($"HTML行{row}: 時刻形式が不正です");
        static int Digits(ReadOnlySpan<char> value, int start, int length)
        {
            int result = 0;
            for (int i = start; i < start + length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                    throw new FormatException("時刻の数字が不正です");
                result = result * 10 + value[i] - '0';
            }
            return result;
        }
        try
        {
            return new DateTime(Digits(text, 0, 4), Digits(text, 5, 2), Digits(text, 8, 2), Digits(text, 11, 2), Digits(text, 14, 2), text.Length == 19 ? Digits(text, 17, 2) : 0, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException e) { throw new FormatException($"HTML行{row}: 暦日/時刻が不正です", e); }
    }
    sealed record Open(long Time, long Volume, long Price, int Side, int Part);
    internal static string Clean(ReadOnlySpan<char> s) => s.IndexOfAny('<', '&') < 0 ? s.Trim().ToString() : WebUtility.HtmlDecode(Regex.Replace(s.ToString(), "<[^>]*>", "", RegexOptions.CultureInvariant)).Trim();
    static ReadOnlySpan<char> Value(ReadOnlySpan<char> s) => s.Trim();
    static long Number(ReadOnlySpan<char> s) => s.IndexOfAny('<', '&') < 0 ? Amount.Parse(s) : Amount.Parse(Clean(s));
    internal static int Cells(ReadOnlySpan<char> row, Span<Range> ranges)
    {
        int n=0,pos=0;
        while(n<ranges.Length)
        {
            int b=row[pos..].IndexOf("<td",StringComparison.OrdinalIgnoreCase);
            int h=row[pos..].IndexOf("<th",StringComparison.OrdinalIgnoreCase);
            bool heading=h>=0 && (b<0 || h<b); if(heading)b=h;
            if(b<0)break;
            b+=pos;int tag=row[b..].IndexOf('>');if(tag<0)throw new FormatException("HTMLセルの開始タグが不正です");b+=tag+1;
            int e=row[b..].IndexOf(heading?"</th>":"</td>",StringComparison.OrdinalIgnoreCase);
            if(e<0)throw new FormatException("HTMLセルが閉じられていません");e+=b;ranges[n++]=b..e;pos=e+5;
        }
        return n;
    }
    internal static ImportMetadata Parse(string path, Action<Trade> emit, ImportOptions options, int memoryMiB, CancellationToken token, IProgress<WorkProgress>? progress, bool forceCp932 = false)
    {
        using var rows = new HtmlRows(path, forceCp932);
        var active = new Dictionary<long, Open>();
        string symbol = "不明";
        long initial = options.InitialBalance ?? 0, sequence = 0, rowId = 0;
        bool reportedTrade = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Span<Range> ranges = stackalloc Range[32];
        while (rows.Next(out var row))
        {
            token.ThrowIfCancellationRequested();
            rowId++;
            int n = Cells(row,ranges);
            if (n>=12 && Mt5HtmlImporter.IsHeader(row))
            {
                if(sequence!=0 || active.Count!=0)throw new FormatException("MT4/MT5の履歴が混在しています");
                return Mt5HtmlImporter.Parse(rows,row,ranges[..n],rowId,initial,options,emit,token,progress);
            }
            if (n < 2)
                continue;
            if (sequence == 0 && active.Count == 0)
            {
                for (int c = 0; c < n - 1; c++)
                {
                    var label = row[ranges[c]];
                    if (label.Contains("通貨ペア", StringComparison.Ordinal) || label.Contains("Symbol", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = Clean(row[ranges[c + 1]]);
                        symbol = val.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "不明";
                    }
                    if (options.InitialBalance == null && (label.Contains("初期証拠金", StringComparison.Ordinal) || label.Contains("Initial deposit", StringComparison.OrdinalIgnoreCase)))
                        initial = Mt5HtmlImporter.Number(Clean(row[ranges[c + 1]]));
                }
            }
            var timeCell = Value(row[ranges[1]]);
            bool tradeShaped = timeCell.Length >= 10 && timeCell[4] == '.' && timeCell[7] == '.';
            if (n < 3)
            {
                if (tradeShaped)
                    throw new FormatException($"HTML行{rowId}: 履歴セルが不足しています");
                continue;
            }
            var kind = Value(row[ranges[2]]);
            bool buy = kind.Equals("buy", StringComparison.OrdinalIgnoreCase), sell = kind.Equals("sell", StringComparison.OrdinalIgnoreCase);
            bool close = kind.Equals("close", StringComparison.OrdinalIgnoreCase) || kind.Equals("s/l", StringComparison.OrdinalIgnoreCase) || kind.Equals("t/p", StringComparison.OrdinalIgnoreCase) || kind.Equals("close at stop", StringComparison.OrdinalIgnoreCase);
            bool ignored = kind.Equals("modify", StringComparison.OrdinalIgnoreCase) || kind.Equals("delete", StringComparison.OrdinalIgnoreCase) || kind.Equals("buy stop", StringComparison.OrdinalIgnoreCase) || kind.Equals("sell stop", StringComparison.OrdinalIgnoreCase) || kind.Equals("buy limit", StringComparison.OrdinalIgnoreCase) || kind.Equals("sell limit", StringComparison.OrdinalIgnoreCase);
            if (!tradeShaped && !buy && !sell && !close)
                continue;
            if (n < 9)
                throw new FormatException($"HTML行{rowId}: 履歴セルが不足しています");
            if (ignored)
                continue;
            if (!buy && !sell && !close)
                throw new FormatException($"HTML行{rowId}: 未対応の履歴種別 {Clean(kind)} です。履歴は保存しません。");
            var time = ParseTime(Value(row[ranges[1]]), rowId);
            if (!long.TryParse(Value(row[ranges[3]]), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticket) || ticket < 0)
                throw new FormatException($"HTML行{rowId}: 注文番号が不正です");
            long volume = Number(row[ranges[4]]), price = Number(row[ranges[5]]);
            if (volume <= 0 || price < 0)
                throw new FormatException($"HTML行{rowId}: 数量/価格が不正です");
            if (buy || sell)
            {
                if (!active.TryAdd(ticket, new(time.Ticks, volume, price, buy ? 1 : 2, 0)))
                    throw new FormatException($"HTML行{rowId}: 注文番号{ticket}が重複しています");
                if ((long)active.Count * 160 > memoryMiB * 1048576L / 2)
                    throw new InvalidOperationException("未決済注文の数がメモリ予算を超えました");
            }
            else
            {
                if (!active.TryGetValue(ticket, out var o))
                    throw new FormatException($"HTML行{rowId}: 注文{ticket}に対応する新規取引がありません");
                if (volume > o.Volume || time.Ticks < o.Time)
                    throw new FormatException($"HTML行{rowId}: 部分決済数量または決済時刻が不正です");
                long priceProfit=Number(row[ranges[8]]);
                emit(new(sequence++, ticket, o.Part, o.Time, time.Ticks, volume, o.Price, price, priceProfit, o.Side, 0, rowId){PriceProfit=priceProfit});
                if (volume == o.Volume)
                    active.Remove(ticket);
                else
                    active[ticket] = o with
                    {
                        Volume = o.Volume - volume,
                        Part = o.Part + 1
                    };
            }
            if ((sequence > 0 && !reportedTrade) || watch.ElapsedMilliseconds >= 100)
            {
                progress?.Report(new(Math.Min(0.99, (double)rows.Position / Math.Max(1, rows.Length)), $"{sequence:N0}取引を正規化中"));
                reportedTrade = sequence > 0;
                watch.Restart();
            }
        }
        token.ThrowIfCancellationRequested();
        if (sequence == 0)
            throw new FormatException("対応するMT4決済履歴またはMT5約定表がありません。取引明細付きのHTMLレポートを指定してください。");
        return new(symbol, initial, sequence, active.Count, active.Count > 0 ? $"未決済注文{active.Count}件は統計に含めていません" : "");
    }
}
