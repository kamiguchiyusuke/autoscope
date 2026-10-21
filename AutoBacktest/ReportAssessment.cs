namespace MT4BatchReporter;
public enum ReportOutcome { Valid, Invalid, Unknown }
public sealed record ReportAssessment
{
    public string Server { get; init; } = "";
    public long? Bars { get; init; }
    public long? Ticks { get; init; }
    public long? Trades { get; init; }
    public decimal? Profit { get; init; }
    public DateTime? ActualFrom { get; init; }
    public DateTime? ActualTo { get; init; }
    public ReportOutcome Outcome { get; init; }
    public List<string> Warnings { get; init; } = [];
    public string Summary => Outcome switch { ReportOutcome.Valid => $"バー {Bars:N0} · ティック {Ticks:N0} · 取引 {Trades:N0} · 純益 {Profit:N2}", ReportOutcome.Invalid => "テスト不成立："+string.Join(" / ",Warnings), _ => "内容確認不可："+string.Join(" / ",Warnings) };
}
