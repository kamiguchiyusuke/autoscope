using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
namespace ArmadaAnalyzer.Desktop;

public partial class MainWindow
{
    internal static (CorrelationRow[] Rows, string Note) CorrelationForView(SeriesPoint[] points,CancellationToken token=default)
    {
        try
        {
            return (Portfolio.Correlations(points,token), "");
        }
        catch (InvalidOperationException error) { return ([], "\n日次相関のみ表示対象外: " + error.Message + "。合成損益の計算・全期間表示は行います。"); }
    }
    private bool selectingPeriod;
    private DashboardPeriod dashboardPeriod = DashboardPeriod.Year;
    private readonly Dictionary<DashboardPeriod, DashboardBucket[]> dashboardCache = new();
    private void InitializeDashboard()
    {
        MainChart.ViewChanged += (start, count) => DrawdownChart.SetView(start, count);
        DrawdownChart.ViewChanged += (start, count) => MainChart.SetView(start, count);
        MonthlyGrid.Columns.Add(new DataGridTextColumn { Header = "年", Binding = new Binding("Year"), Width = new DataGridLength(55) });
        for (int month = 0; month < 13; month++)
        {
            string path = month == 12 ? "Total" : $"Cells[{month}]";
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(path + ".Text"));
            text.SetBinding(TextBlock.ForegroundProperty, new Binding(path + ".Color"));
            text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetValue(TextBlock.FontSizeProperty, 11.0);
            MonthlyGrid.Columns.Add(new DataGridTemplateColumn { Header = month == 12 ? "合計" : $"{month + 1}月", CellTemplate = new DataTemplate { VisualTree = text }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 70 });
        }
        MonthlyGrid.RowHeight = 48;
    }
    private void UpdateDashboard(MonthlyRow[] months, DashboardBucket[] buckets, bool sameSeries = false)
    {
        if (!sameSeries) { dashboardCache.Clear(); ClearTradeDetails(); }
        dashboardCache[dashboardPeriod] = buckets;
        SetDashboardBars(buckets);
        if (!sameSeries || MonthlyGrid.ItemsSource == null) MonthlyGrid.ItemsSource = months.Select(row => new { row.Year, Cells = Enumerable.Range(0, 12).Select(i => new MonthCell(row.Profit[i], row.Count[i])).ToArray(), Total = new MonthCell(row.Total, row.Count.Sum()) }).ToArray();
        if (analysis == null)
            return;
        TotalPipsValue.Text = analysis.Pips.Total?.ToString("N2") ?? "—";
        AveragePipsValue.Text = analysis.Pips.Average?.ToString("N3") ?? "—";
        MaxPipsValue.Text = analysis.Pips.Maximum?.ToString("N2") ?? "—";
        MinPipsValue.Text = analysis.Pips.Minimum?.ToString("N2") ?? "—";
        WinPipsValue.Text = analysis.Pips.WinningAverage?.ToString("N2") ?? "—";
        LossPipsValue.Text = analysis.Pips.LosingAverage?.ToString("N2") ?? "—";
        PayoffValue.Text = analysis.PayoffRatio?.ToString("F2") ?? "—";
        DeviationValue.Text = analysis.SampleDeviation is double sd ? (sd/Amount.Scale).ToString("N2") : "—";
        RecoveryValue.Text = analysis.RecoveryFactor?.ToString("F2") ?? "—";
        SqnValue.Text = analysis.TradeSqn?.ToString("F2") ?? "—";
        SqnValue.ToolTip = "sqrt(全決済数) × 平均損益 / 損益の標本標準偏差。ゼロ損益も含む独自の明示定義です。";
        WinsCountValue.Text = analysis.Wins.ToString("N0");
        LossesCountValue.Text = analysis.Losses.ToString("N0");
        AverageProfitValue.Text = analysis.AverageProfit is decimal average ? (average / Amount.Scale).ToString("N2") : "—";
        double p = analysis.Count > 0 ? (double)analysis.Wins / analysis.Count : double.NaN;
        string theoreticalWins = p >= 0 && p < 1 ? (1 / (1 - p)).ToString("F2") : "—";
        string theoreticalLosses = p > 0 && p <= 1 ? (1 / p).ToString("F2") : "—";
        AverageWinsValue.Text = $"{analysis.AverageWinStreak?.ToString("F2") ?? "—"} / {theoreticalWins}";
        AverageLossesValue.Text = $"{analysis.AverageLossStreak?.ToString("F2") ?? "—"} / {theoreticalLosses}";
        BreakEvenValue.Text = analysis.BreakEvenWinRate is double rate ? $"{NumberDisplay.TwoDecimal(rate)}%" : "算出不可";
        const string streakBasis = "実測平均 / 理論平均。pは勝数÷総取引回数（ゼロ損益を含む）。実測は決済時刻順で、ゼロ損益で区切り、末尾の連続も含めます。算出できない値は—。表示のみの比較です。";
        AverageWinsValue.ToolTip = streakBasis + " 理論平均連勝数 = 1 / (1 − p)。";
        AverageLossesValue.ToolTip = streakBasis + " 理論平均連敗数 = 1 / p。";
        BreakEvenValue.ToolTip = "平均損失の絶対値 ÷（平均利益＋平均損失の絶対値）。勝ちと負けの両方がある場合のみ表示。";
        GrossValue.Text = Amount.Display(analysis.GrossProfit);
        LossValue.Text = Amount.Display(analysis.GrossLoss);
        ExpectedValue.Text = analysis.Count == 0 ? "—" : Amount.Display(analysis.Net / analysis.Count);
        StreakValue.Text = $"{analysis.WinStreak} / {analysis.LossStreak}";
        RelativeValue.Text = analysis.DrawdownPercent?.ToString("F2", CultureInfo.InvariantCulture) + "%";
        AverageLossValue.Text = analysis.Losses == 0 ? "—" : Amount.Display(analysis.GrossLoss / analysis.Losses);
        foreach(var value in new[]{GrossValue,LossValue,ExpectedValue,AverageLossValue,AverageProfitValue,TotalPipsValue,AveragePipsValue,MaxPipsValue,MinPipsValue,WinPipsValue,LossPipsValue,PayoffValue,DeviationValue,RecoveryValue,SqnValue})
            value.Text=NumberDisplay.TrimFraction(value.Text);
        AverageWinsValue.Text=string.Join(" / ",AverageWinsValue.Text.Split(" / ").Select(NumberDisplay.TrimFraction));
        AverageLossesValue.Text=string.Join(" / ",AverageLossesValue.Text.Split(" / ").Select(NumberDisplay.TrimFraction));

    }
    private void ClearDashboard()
    {
        ClearConvergence();
        dashboardCache.Clear();
        ClearTradeDetails();
        DrawdownGrid.SetRows(null);
        displayedDrawdowns=null;
        MonthlyGrid.ItemsSource = null;
        DrawdownChart.SetData([]);
        SetDashboardBars([]);
        TotalPipsValue.Text = AveragePipsValue.Text = MaxPipsValue.Text = MinPipsValue.Text = WinPipsValue.Text = LossPipsValue.Text = "—";
        PayoffValue.Text = DeviationValue.Text = RecoveryValue.Text = SqnValue.Text = "—";
        WinsCountValue.Text = LossesCountValue.Text = AverageProfitValue.Text = AverageWinsValue.Text = AverageLossesValue.Text = "—";
        BreakEvenValue.Text="算出不可";
        GrossValue.Text = LossValue.Text = ExpectedValue.Text = StreakValue.Text = RelativeValue.Text = AverageLossValue.Text = "—";
    }
    private void SetDashboardBars(DashboardBucket[] buckets)
    {
        ProfitBars.SetData(buckets);
        AverageBars.SetData(buckets);
        CountBars.SetData(buckets);
        DrawdownBars.SetData(buckets);
    }
    internal async Task ShowBreakdownAsync(DashboardPeriod period)
    {
        if (busy)
            return;
        dashboardPeriod = period;
        selectingPeriod = true;
        foreach (var radio in PeriodTabs.Children.OfType<RadioButton>())
            radio.IsChecked = radio.Tag?.ToString() == ((int)period).ToString(CultureInfo.InvariantCulture);
        selectingPeriod = false;
        HoldingSection.Visibility = Visibility.Collapsed;
        EquitySection.Visibility = Visibility.Collapsed;
        BreakdownSection.Visibility = Visibility.Visible;
        BreakdownTitle.Text = new[] { "年別", "月別（年を合算）", "日別（各月の日を合算）", "曜日別", "時間別", "分別" }[(int)period] + " / 決済時刻で集計";
        if (snapshot == null)
        {
            SetDashboardBars([]);
            return;
        }
        if (dashboardCache.TryGetValue(period, out var cached))
        {
            SetDashboardBars(cached);
            return;
        }
        SetDashboardBars([]);
        await RunJob("区分別のグラフを集計中", async (token, _) =>
        {
            var result = await Task.Run(() => DashboardAggregation.Compute(snapshot.Points, period,token), token);
            token.ThrowIfCancellationRequested();
            dashboardCache[period] = result;
            SetDashboardBars(result);
            return "集計グラフを表示しました";
        });
    }
    private async void ResultPeriod_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || selectingPeriod || sender is not RadioButton radio)
            return;
        string tag = radio.Tag?.ToString() ?? "";
        if (tag == "holding")
        {
            await ShowHoldingAsync();
            return;
        }
        if (tag == "correlation")
        {
            Tabs.SelectedIndex = 3;
            return;
        }
        if (tag == "equity")
        {
            HoldingSection.Visibility = Visibility.Collapsed;
            EquitySection.Visibility = Visibility.Visible;
            BreakdownSection.Visibility = Visibility.Collapsed;
            return;
        }
        if (int.TryParse(tag, out int period))
            await ShowBreakdownAsync((DashboardPeriod)period);
    }
    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (Tabs == null || sender is not RadioButton radio)
            return;
        if (radio.Tag?.ToString() == "conditions")
        {
            Tabs.SelectedIndex = 6;
            return;
        }
        if (int.TryParse(radio.Tag?.ToString(), out int index))
            Tabs.SelectedIndex = index;
    }
    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.OriginalSource != Tabs)
            return;
        AnimateSection();
        ImportPanel.Visibility = FiltersPanel.Visibility = Tabs.SelectedIndex == 6 ? Visibility.Hidden : Visibility.Visible;
        SetJobControlsEnabled(!busy);
        foreach (var radio in NavigationPanel.Children.OfType<RadioButton>())
            if (radio.Tag?.ToString() == (Tabs.SelectedIndex == 6 ? "conditions" : Tabs.SelectedIndex.ToString(CultureInfo.InvariantCulture)))
                radio.IsChecked = true;
        if (Tabs.SelectedIndex == 0 && !busy && snapshot != null && HoldingSection.Visibility == Visibility.Visible && holdingKey != snapshot.Key)
            await ShowHoldingAsync();
    }
    private async void SideFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || busy || synchronizingSide || sender is not RadioButton radio)
            return;
        SideBox.SelectedIndex = int.Parse(radio.Tag.ToString()!, CultureInfo.InvariantCulture);
        if (snapshot != null)
            await AnalyzeAsync();
    }
    private void CorrelationView_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 3;
    private void Sidebar_Click(object sender, RoutedEventArgs e) => SidebarColumn.Width = new GridLength(SidebarColumn.Width.Value == 0 ? 232 : 0);
    private sealed record MonthCell(long Profit, int Count)
    {
        public string Text => $"{Amount.Display(Profit)}\n({Count:N0}回)";
        public Brush Color => UiTheme.Get(Profit < 0 ? "Loss" : Profit > 0 ? "Gain" : "Muted");
    }
}
