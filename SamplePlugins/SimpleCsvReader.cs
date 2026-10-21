using ArmadaAnalyzer.Contracts;
using System.Globalization;
using System.Text;

namespace ArmadaAnalyzer.SamplePlugins;

/// <summary>Educational numeric CSV schema; quoted fields are intentionally outside this sample format.</summary>
public sealed class SimpleCsvReader : IReportReaderPlugin
{
    public PluginMetadata Metadata => new("samples.numericcsv", "数値CSVサンプル読込", "1.0.0", ".armadacsv形式のサンプル。Open,Close,Side,Volume,Entry,Exit,Profit,Ticket の8列。");
    public bool CanRead(string extension, ReadOnlySpan<byte> prefix) => extension.Equals(".armadacsv", StringComparison.OrdinalIgnoreCase);
    public ReportData ReadReport(Stream input, ReaderContext context, CancellationToken cancellationToken) => new(new("SAMPLE", 1000000L * ContractVersion.AmountScale), ReadRows(input, cancellationToken));

    private static IEnumerable<ImportedTrade> ReadRows(Stream input, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, true, 65536, leaveOpen: true);
        string? header = reader.ReadLine();
        if (header != "Open,Close,Side,Volume,Entry,Exit,Profit,Ticket")
            throw new FormatException("CSVヘッダーがサンプル形式と異なります");
        string? line;
        long sourceRow = 1;
        while ((line = reader.ReadLine()) != null)
        {
            sourceRow++;
            cancellationToken.ThrowIfCancellationRequested();
            string[] cells = line.Split(',');
            if (cells.Length != 8)
                throw new FormatException($"CSV行{sourceRow}: 8列が必要です");
            long opened = DateTime.ParseExact(cells[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture).Ticks;
            long closed = DateTime.ParseExact(cells[1], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture).Ticks;
            int side = cells[2] switch
            {
                "buy" => 1,
                "sell" => 2,
                _ => throw new FormatException("buy/sellが必要です")
            };
            yield return new(long.Parse(cells[7], CultureInfo.InvariantCulture), 0, opened, closed,
                Fixed(cells[3]), Fixed(cells[4]), Fixed(cells[5]), Fixed(cells[6]), side, sourceRow);
        }
    }

    private static long Fixed(string text)
    {
        int point = text.IndexOf('.');
        if (point >= 0 && text[(point + 1)..].Skip(8).Any(digit => digit != '0'))
            throw new FormatException("8桁を超える精度です");
        decimal scaled = decimal.Parse(text, CultureInfo.InvariantCulture) * ContractVersion.AmountScale;
        return checked((long)scaled);
    }
}
