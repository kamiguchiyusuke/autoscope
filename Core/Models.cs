namespace ArmadaAnalyzer;

public readonly record struct SeriesPoint(long Ticks, long Profit, long DatasetId, long PriceMove = long.MinValue);
public readonly record struct ChartPoint(int Index, long Value);
public sealed record AnalysisResult(int Count, long Net, long GrossProfit, long GrossLoss, long Drawdown, int Wins, int Losses, int Zeros, int WinStreak, int LossStreak, double? ProfitFactor, double? DrawdownPercent, long[] Equity, long[] Drawdowns)
{
    public DrawdownStatistics? Recovery { get; init; }
    public ChartDrawdown[] ChartDrawdowns { get; init; } = [];
    public PipStatistics Pips { get; init; } = PipStatistics.Unavailable;
    public double? SampleDeviation { get; init; }
    public double? RecoveryFactor => Drawdown == 0 ? null : (double)Net / Drawdown;
    public double? PayoffRatio => AverageProfit is decimal gain && AverageLoss is < 0 ? (double)(gain / -AverageLoss.Value) : null;
    public double? TradeSqn => SampleDeviation is > 0 ? Math.Sqrt(Count) * ((double)Net/Count) / SampleDeviation.Value : null;
    public int WinRuns { get; init; }
    public int LossRuns { get; init; }
    public double? AverageWinStreak => WinRuns == 0 ? null : (double)Wins / WinRuns;
    public double? AverageLossStreak => LossRuns == 0 ? null : (double)Losses / LossRuns;
    public decimal? AverageProfit => Wins == 0 ? null : (decimal)GrossProfit / Wins;
    public decimal? AverageLoss => Losses == 0 ? null : (decimal)GrossLoss / Losses;
    public double? BreakEvenWinRate => AverageProfit is > 0 && AverageLoss is < 0
        ? (double)(-AverageLoss.Value / (AverageProfit.Value - AverageLoss.Value) * 100m) : null;
}
public sealed record Dataset(long Id, string Name, string Symbol, string Currency, string TimeBasis, long InitialBalance, long Count, string Hash, long Version, string Warning);
public sealed record ImportOptions(string Currency = "不明", string TimeBasis = "ブローカー時刻", long? InitialBalance = null);
public sealed record ImportResult(Dataset Dataset, bool AlreadyImported);
public sealed record WorkProgress(double Fraction, string Message);
public sealed record Filter(long[] DatasetIds, long? From = null, long? To = null, int Side = 0, Dictionary<long, decimal>? Weights = null, EntryCalendar? Calendar = null);
public readonly record struct Trade(long Sequence, long Ticket, int Part, long OpenTicks, long CloseTicks, long Volume, long EntryPrice, long ExitPrice, long Profit, int Side, long DatasetId, long SourceRow) { public long? PriceProfit {get;init;} }
public sealed record Snapshot(string Key, SeriesPoint[] Points, bool CacheHit)
{
    public IReadOnlyDictionary<long,int> PipFactors { get; init; } = new Dictionary<long,int>();
}
public static class Amount
{
    public const long Scale = 100000000;
    public static long Parse(string text) => Parse(text.AsSpan());
    public static long Parse(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        int decimalPoint = text.IndexOf('.');
        if (decimalPoint >= 0)
        {
            var fraction = text[(decimalPoint + 1)..];
            for (int i = 8; i < fraction.Length; i++)
                if (fraction[i] != '0')
                    throw new FormatException("小数点以下8桁を超える精度です");
        }
        if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new FormatException("数値を解釈できません: " + text.ToString());
        decimal scaled = value * Scale;
        if (decimal.Truncate(scaled) != scaled)
            throw new FormatException("小数点以下8桁を超える精度です");
        return checked((long)scaled);
    }
    public static string Display(long value) => ((decimal)value / Scale).ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
    public static string Exact(long value) => ((decimal)value / Scale).ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
}
