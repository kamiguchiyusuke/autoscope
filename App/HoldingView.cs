using System.Globalization;
using System.Windows;
using System.Windows.Controls;
namespace ArmadaAnalyzer.Desktop;

public partial class MainWindow
{
    internal static bool CanDisplayPips(IEnumerable<string> symbols)
    {
        var unique = symbols.Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return unique.Length>0 && unique.All(s=>DukascopyPips.Size(s)!=null);
    }
    private HoldingScatterResult? holding;
    private string? holdingKey;
    private void ClearHolding()
    {
        holding = null;
        holdingKey = null;
        HoldingProfitChart.SetData([]);
        HoldingPipsChart.SetData([]);
        HoldingSummary.Text = "取引時間タブを選択して読み込みます";
    }
    private async void Holding_Click(object sender, RoutedEventArgs e) => await ShowHoldingAsync();
    internal async Task ShowHoldingAsync()
    {
        if (busy || snapshot == null || appliedFilter == null)
            return;
        var symbols = datasets.Where(d => appliedFilter.DatasetIds.Contains(d.Data.Id)).Select(d => d.Data.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        PipSizeBox.IsEnabled=symbols.Length==1;
        bool automaticPip = symbols.Length!=1 || string.IsNullOrWhiteSpace(PipSizeBox.Text);
        decimal pip = 0;
        try
        {
            if (!automaticPip)
            {
                pip = Amount.Parse(PipSizeBox.Text) / (decimal)Amount.Scale;
                if (pip <= 0)
                    throw new ArgumentException("1 Pipの価格幅は正の値で指定してください");
            }
        }
        catch (Exception error) { ShowError(error); return; }
        selectingPeriod = true;
        foreach (var radio in PeriodTabs.Children.OfType<RadioButton>())
            radio.IsChecked = radio.Tag?.ToString() == "holding";
        selectingPeriod = false;
        EquitySection.Visibility = BreakdownSection.Visibility = Visibility.Collapsed;
        HoldingSection.Visibility = Visibility.Visible;
        await RunJob("保有時間の分布を読み込み中", async (token, _) =>
        {
            string key = snapshot.Key;
            var result = holdingKey == key && holding != null ? holding : await Task.Run(() => workspace.LoadHoldingScatter(appliedFilter, token: token), token);
            token.ThrowIfCancellationRequested();
            holding = result;
            holdingKey = key;
            HoldingProfitChart.SetData(result.Points);
            HoldingPipsChart.SetData(result.Points,pip);
            HoldingSummary.Text = $"全{result.Total:N0}取引 / 表示{result.Points.Length:N0}座標 · 6,000点＋極値を上限に抽出。";
            HoldingSummary.Text += automaticPip?" pipsは各通貨ペアの単位で換算（数量・ウェイト非加重）。":$" 1pip = {pip}（手動指定、数量・ウェイト非加重）。";
            if(automaticPip && result.PipCount<result.Total)HoldingSummary.Text += $" 単位不明の{result.Total-result.PipCount:N0}取引はpips未表示。";
            return "保有時間と損益の散布図を表示しました";
        });
    }
}
