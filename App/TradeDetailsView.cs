namespace ArmadaAnalyzer.Desktop;

public partial class MainWindow
{
    private int analyzedMemoryMiB = ResourceLimits.DefaultMemoryMiB;
    private string? detailsKey;
    private TradeDetails? cachedTradeDetails;
    internal TradeDetails? CurrentTradeDetails => cachedTradeDetails;

    private static string HoldingDuration(decimal? ticks) => ticks is decimal value
        ? $"{decimal.Floor(value / TimeSpan.TicksPerDay):N0}日\n{decimal.Floor(value / TimeSpan.TicksPerHour) % 24:0}時間{decimal.Floor(value / TimeSpan.TicksPerMinute) % 60:0}分"
        : "—";

    private void UpdateTradeDetails(string key, TradeDetails? details)
    {
        if (detailsKey == key && ReferenceEquals(cachedTradeDetails, details)) return;
        detailsKey = key;
        cachedTradeDetails = details;
        MaxPositionsValue.Text = details?.Positions.Maximum.ToString("N0") ?? "—";
        LongPositionsValue.Text = details?.Positions.LongMaximum.ToString("N0") ?? "—";
        ShortPositionsValue.Text = details?.Positions.ShortMaximum.ToString("N0") ?? "—";
        TotalHoldingValue.Text = HoldingDuration(details is { Count: > 0 } ? details.TotalDuration : null);
        MaxHoldingValue.Text = HoldingDuration(details?.MaxDuration);
        MinHoldingValue.Text = HoldingDuration(details?.MinDuration);
        AverageHoldingValue.Text = HoldingDuration(details is { Count: > 0 } ? details.TotalDuration / details.Count : null);
        MedianHoldingValue.Text = HoldingDuration(details?.MedianDuration);
        HoldingMetrics.ToolTip = details == null
            ? "保有時間の全件集計は800万件までです。期間を絞ってください。"
            : "各取引の新規時刻から決済時刻まで。表示は分未満を切り捨てます。";
    }

    private void ClearTradeDetails()
    {
        detailsKey = null;
        cachedTradeDetails = null;
        MaxPositionsValue.Text = LongPositionsValue.Text = ShortPositionsValue.Text = "—";
        TotalHoldingValue.Text = MaxHoldingValue.Text = MinHoldingValue.Text = AverageHoldingValue.Text = MedianHoldingValue.Text = "—";
        HoldingMetrics.ToolTip = null;
    }
}
