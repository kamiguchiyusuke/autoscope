using ArmadaAnalyzer.Contracts;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArmadaAnalyzer.Desktop;

public partial class MainWindow : Window
{
    private Workspace workspace;
    private readonly ObservableCollection<DatasetView> datasets = [];
    private readonly PluginManager plugins = new();
    private CancellationTokenSource? jobCancellation;
    private bool busy, closeAfterJob, lastJobCanceled;
    private int generation, pageOffset;
    private Snapshot? snapshot;
    internal Snapshot? CurrentSnapshot => snapshot;
    private AnalysisResult? analysis;
    private Filter? appliedFilter;
    private long initialCapital;
    private string? analysisCacheKey;
    private AnalysisResult? cachedAnalysis;
    private sealed record DerivedView(GroupRow[] Groups, (CorrelationRow[] Rows, string Note) Correlations, MonthlyRow[] Monthly, DashboardBucket[] Dashboard);
    private string? derivedCacheKey;
    private DerivedView? cachedDerived;
    internal string? LastError
    {
        get; private set;
    }
    internal Workspace CurrentWorkspace => workspace;
    internal AnalysisResult? CurrentAnalysis => analysis;
    internal bool IsBusy => busy;

    public MainWindow(string root)
    {
        InitializeComponent();
        TightenChartMetricPairs();
        Tabs.CanPrepare = () => !busy;
        workspace = new(root);
        DatasetList.ItemsSource = datasets;
        RefreshDatasets();
        InitializeDashboard();
        InitializeConditions();
        InitializeAppearance();
        InitializeAutoBacktest();
    }

    private void RefreshDatasets()
    {
        var previous = datasets.ToDictionary(x => x.Data.Id);
        datasets.Clear();
        foreach (Dataset data in workspace.ListDatasets())
            datasets.Add(new(data)
            {
                Selected = !previous.TryGetValue(data.Id, out var old) || old.Selected,
                Weight = previous.GetValueOrDefault(data.Id)?.Weight ?? "1"
            });
        WorkspaceName.Text = Path.GetFileName(workspace.Root);
        WorkspaceName.ToolTip = workspace.Root;
    }

    private void ShowError(Exception error)
    {
        LastError = error.Message;
        StatusText.Text = UserError.Message(error);
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Loss");
        if (error is not FormatException and not ArgumentException and not InvalidOperationException)
            App.WriteError(error);
    }

    private void SetJobControlsEnabled(bool enabled)
    {
        // Inactive, retained pages must not rebuild their disabled visual trees for every job.
        ActionsPanel.IsEnabled = DatasetPanel.IsEnabled = FiltersPanel.IsEnabled = ImportPanel.IsEnabled = enabled;
        foreach (var (panel, page) in new (UIElement, int)[] { (PageControls, 1), (GroupMode, 2), (ResultTabsPanel, 0), (SideOptions, 0), (ConditionsPanel, 6), (HoldingControls, 0) })
            if (enabled || Tabs.SelectedIndex == page) panel.IsEnabled = enabled;
    }

    internal async Task RunJob(string title, Func<CancellationToken, IProgress<WorkProgress>, Task<string>> action)
    {
        if (busy)
            return;
        busy = true;
        LastError = null;
        lastJobCanceled = false;
        int current = ++generation;
        jobCancellation = new();
        CancellationToken token = jobCancellation.Token;
        SetJobControlsEnabled(false);
        CancelButton.IsEnabled = true;
        JobProgress.Value = 0;
        SetBusyFeedback(true);
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Muted");
        StatusText.Text = title;
        var watch = Stopwatch.StartNew();
        var progress = new Progress<WorkProgress>(p => { if (current == generation && !token.IsCancellationRequested) { JobProgress.IsIndeterminate = MotionEnabled && (!double.IsFinite(p.Fraction) || p.Fraction <= 0); JobProgress.Value = double.IsFinite(p.Fraction) ? Math.Clamp(p.Fraction * 100, 0, 100) : 0; StatusText.Text = p.Message; } });
        try
        {
            token.ThrowIfCancellationRequested();
            string result = await action(token, progress);
            token.ThrowIfCancellationRequested();
            StatusText.Text = $"待機中 · {result} · {watch.Elapsed.TotalMilliseconds:N0} ms";
            JobProgress.Value = 100;
        }
        catch (OperationCanceledException) { lastJobCanceled = true; StatusText.Text = "処理をキャンセルしました。確定前の取込は保存されません。"; JobProgress.Value = 0; }
        catch (Exception error) { ShowError(error); JobProgress.Value = 0; }
        finally
        {
            generation++;
            jobCancellation.Dispose();
            jobCancellation = null;
            busy = false;
            SetJobControlsEnabled(true);
            CancelButton.IsEnabled = false;
            SetBusyFeedback(false);
            UpdateBackgroundStatus();
            if (closeAfterJob)
                Close();
        }
    }

    internal async Task ImportPathsAsync(string[] files)
    {
        if (busy || files.Length == 0)
            return;
        var options = new ImportOptions();
        var reader = (ReaderBox.SelectedItem as ComboBoxItem)?.Tag as IReportReaderPlugin;
        await RunJob("取込を開始します", async (token, progress) =>
        {
            int added = 0, existing = 0;
            await Task.Run(() =>
            {
                for (int index = 0; index < files.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    int position = index;
                    var fileProgress = new CallbackProgress(p => progress.Report(new((position + p.Fraction) / files.Length, Path.GetFileName(files[position]) + " · " + p.Message)));
                    ImportResult result = reader == null ? workspace.Import(files[index], options, token, fileProgress) : workspace.ImportPlugin(files[index], reader, options, token, fileProgress);
                    if (result.AlreadyImported)
                        existing++;
                    else
                        added++;
                }
            }, token);
            return $"{added}件のレポートを追加、{existing}件は保存済み（金額は数値のまま使用）";
        });
        RefreshDatasets();
        if (LastError == null && !lastJobCanceled && !closeAfterJob)
            await AnalyzeAsync();
    }

    private Filter ReadFilter()
    {
        var chosen = datasets.Where(d => d.Selected).ToArray();
        var weights = chosen.ToDictionary(d => d.Data.Id, d => decimal.Parse(d.Weight, CultureInfo.InvariantCulture));
        if (weights.Values.Any(w => w < 0 || w > 1000))
            throw new ArgumentException("ウェイトは0〜1000で指定してください");
        long[] ids = chosen.Where(d => weights[d.Data.Id] != 0).Select(d => d.Data.Id).ToArray();
        if (ids.Length == 0)
            throw new InvalidOperationException("分析する戦略を選択してください（ウェイト0は対象外です）");
        if (FromDate.SelectedDate > ToDate.SelectedDate)
            throw new ArgumentException("期間の開始日が終了日より後です");
        return new(ids, FromDate.SelectedDate?.Date.Ticks, ToDate.SelectedDate?.Date.AddDays(1).Ticks, SideBox.SelectedIndex, weights, ReadEntryCalendar());
    }

    internal async Task AnalyzeAsync()
    {
        if (busy)
            return;
        Filter filter;
        long capital;
        DrawdownOptions ddOptions;
        int budget, grouping;
        try
        {
            filter = ReadFilter();
            capital = Amount.Parse(CapitalBox.Text);
            ddOptions=ReadDrawdownOptions();
            if (capital <= 0)
                throw new ArgumentException("初期資本は正の値で指定してください");
            budget = ResourceLimits.DefaultMemoryMiB;
            grouping = GroupMode.SelectedIndex;
        }
        catch (Exception error) { ShowError(error); return; }
        bool firstPageAvailable = pageOffset == 0 && TradesGrid.ItemsSource != null;
        await RunJob("保存済み履歴を分析中", async (token, progress) =>
        {
            var result = await Task.Run(() =>
            {
                Snapshot data = workspace.LoadSeries(filter, budget, token, snapshot);
                string cacheKey = data.Key + ":" + capital + ":" + ddOptions;
                AnalysisResult summary = cacheKey == analysisCacheKey && cachedAnalysis != null ? cachedAnalysis : Analytics.Analyze(data, capital,ddOptions,token);
                token.ThrowIfCancellationRequested();
                string viewKey = data.Key + ":" + grouping + ":" + dashboardPeriod;
                var view = viewKey == derivedCacheKey && cachedDerived != null ? cachedDerived : new DerivedView(Grouping.Compute(data.Points, grouping,token), CorrelationForView(data.Points,token), DashboardAggregation.Months(data.Points,token), DashboardAggregation.Compute(data.Points, dashboardPeriod,token));
                token.ThrowIfCancellationRequested();
                var groups = view.Groups;
                var correlationView = view.Correlations;
                var correlations = correlationView.Rows;
                var holdingDetails = detailsKey == data.Key && cachedTradeDetails != null ? cachedTradeDetails
                    : data.Points.Length <= 8000000 ? workspace.LoadTradeDetails(filter, data.Points.Length, token, progress, includePips: false) : null;
                token.ThrowIfCancellationRequested();
                var page = firstPageAvailable && ReferenceEquals(snapshot?.Points, data.Points) ? Array.Empty<Trade>() : workspace.ReadPage(filter, 0);
                var monthly = view.Monthly;
                var dashboard = view.Dashboard;
                var displayPeriod=workspace.DisplayPeriod(filter,token);
                return (data, summary, groups, correlations, page, cacheKey, monthly, dashboard, correlationView, viewKey, view, holdingDetails, displayPeriod);
            }, token);
            token.ThrowIfCancellationRequested();
            bool sameSeries = ReferenceEquals(snapshot?.Points, result.data.Points);
            bool sameFirstPage = sameSeries && pageOffset == 0 && TradesGrid.ItemsSource != null;
            snapshot = result.data;
            analyzedMemoryMiB = budget;
            derivedCacheKey = result.viewKey;
            cachedDerived = result.view;
            analysis = result.summary;
            appliedFilter = filter;
            initialCapital = capital;
            pageOffset = 0;
            analysisCacheKey = result.cacheKey;
            cachedAnalysis = analysis;
            SyncSideSelection(filter.Side);
            var selected = datasets.Where(d => filter.DatasetIds.Contains(d.Data.Id)).Select(d => d.Data).ToArray();
            var names = selected.ToDictionary(d => d.Id, d => d.Name);
            AnalysisTitle.Text = !string.IsNullOrWhiteSpace(PortfolioName.Text) ? PortfolioName.Text.Trim() : selected.Length == 1 ? selected[0].Name : $"ポートフォリオ · {selected.Length}戦略";
            AnalysisTitle.Text += result.displayPeriod?.Label ?? "（テスト期間 不明）";
            AnalysisTitle.ToolTip = "レポート記載の期間を優先し、不明時は最初の取引開始〜最後の取引終了を使用。複数戦略は全EAの最も早い開始〜最も遅い終了、開始・終了指定時は指定期間を表示します。分析値は全対象取引を使用します。";
            string period = analysis.Count == 0 ? "対象0件" : $"{new DateTime(snapshot.Points[0].Ticks):yyyy/MM/dd} ～ {new DateTime(snapshot.Points[^1].Ticks):yyyy/MM/dd}";
            AnalysisDetail.Text = $"{string.Join(" / ", selected.Select(d => d.Symbol).Distinct())} · {period} · 金額（通貨の区別なし） · レポート時刻をそのまま使用";
            AnalysisDetail.ToolTip = string.Join(" ", selected.Select(d => d.Warning).Where(text => text.Length > 0));
            if (filter.Calendar != null)
                AnalysisDetail.Text += " · 新規時刻フィルター適用";
            NetValue.Text = Amount.Display(analysis.Net);
            UpdateConvergence(analysis);
            PfValue.Text = analysis.ProfitFactor?.ToString("F2") ?? "—";
            WinValue.Text = analysis.Count == 0 ? "算出不可" : $"{NumberDisplay.TwoDecimal(100.0 * analysis.Wins / analysis.Count)}%";
            DdValue.Text = Amount.Display(-analysis.Drawdown);
            ZeroValue.Text = analysis.Zeros.ToString("N0");
            NetValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, analysis.Net >= 0 ? "Gain" : "Loss");
            GroupsGrid.ItemsSource = result.groups;
            CorrelationNote.Text = "選択した戦略の全期間を使用し、取引のない日は損益0。時刻と金額はレポートの数値のままです。" + result.correlationView.Note;
            if (!sameSeries || CorrelationGrid.ItemsSource == null) CorrelationGrid.ItemsSource = result.correlations.Select(c => new { First = names[c.First], Second = names[c.Second], c.Days, Value = c.Value?.ToString("F4") ?? "未定義" }).ToArray();
            if (!sameSeries) ClearHolding();
            UpdateDashboard(result.monthly, result.dashboard, sameSeries);
            UpdateTradeDetails(result.data.Key, result.holdingDetails);
            UpdateDrawdowns(ddOptions);
            if (!sameFirstPage) ShowPage(result.page);
            ApplyChart();
            PluginMetricsGrid.ItemsSource = null;
            ExtensionChart.SetSeries(null);
            ScheduleMetricEstimates();
            ScheduleSlippage();
            Tabs.PrepareWhenIdle();
            return $"{analysis.Count:N0}取引の分析を表示" + (snapshot.CacheHit ? "（保存済みデータを再利用）" : "");
        });
        if (LastError == null && !lastJobCanceled && !closeAfterJob && Tabs.SelectedIndex == 0 && HoldingSection.Visibility == Visibility.Visible)
            await ShowHoldingAsync();
    }

    private void ApplyChart()
    {
        if (analysis == null || snapshot == null)
            return;
        bool balance = ChartMode.SelectedIndex == 1;
        ChartTitle.Text = balance ? "残高 / 決済順" : "総損益 / 決済順";
        MainChart.SetData(analysis.Equity, snapshot.Points, balance ? initialCapital : 0);
        MainChart.SetDrawdownBands(analysis.ChartDrawdowns);
        decimal? total=initialCapital>0 && analysis.Count>0?100m*analysis.Net/initialCapital:null;
        double years=snapshot.Points.Length>1?(snapshot.Points[^1].Ticks-snapshot.Points[0].Ticks)/(double)TimeSpan.TicksPerDay/365.2425:0;
        AnnualReturnValue.Text=total.HasValue && years>0?NumberDisplay.TwoDecimal((double)total.Value/years)+"%":"算出不可";
        TotalReturnValue.Text=total.HasValue?NumberDisplay.TwoDecimal(total.Value)+"%":"算出不可";
        DrawdownChart.SetData(analysis.Drawdowns, snapshot.Points, drawdown: true);
    }
    private void ShowPage(Trade[] rows)
    {
        var names = datasets.ToDictionary(d => d.Data.Id, d => d.Data.Name);
        TradesGrid.ItemsSource = rows.Select(t => new { Strategy = names.GetValueOrDefault(t.DatasetId, ""), Ticket = $"{t.Ticket}:{t.Part}", Open = new DateTime(t.OpenTicks).ToString("yyyy/MM/dd HH:mm"), Close = new DateTime(t.CloseTicks).ToString("yyyy/MM/dd HH:mm"), Side = t.Side == 1 ? "買い" : "売り", Volume = Amount.Exact(t.Volume), Entry = Amount.Exact(t.EntryPrice), Exit = Amount.Exact(t.ExitPrice), Profit = Amount.Display(t.Profit), t.SourceRow }).ToArray();
        PageLabel.Text = rows.Length == 0 ? "0件" : $"{pageOffset + 1:N0}〜{pageOffset + rows.Length:N0} / {snapshot?.Points.Length:N0}件";
    }

    internal async Task PageAsync(int direction)
    {
        if (busy || appliedFilter == null || snapshot == null)
            return;
        int next = Math.Clamp(pageOffset + direction * 500, 0, Math.Max(0, (snapshot.Points.Length - 1) / 500 * 500));
        await RunJob("取引一覧を読込中", async (token, _) => { var rows = await Task.Run(() => workspace.ReadPage(appliedFilter, next), token); token.ThrowIfCancellationRequested(); pageOffset = next; ShowPage(rows); return PageLabel.Text; });
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "取引レポート|*.htm;*.html;*.armadacsv;*.csv|すべてのファイル|*.*" };
        if (dialog.ShowDialog(this) == true)
            await ImportPathsAsync(dialog.FileNames);
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();
    private async void Demo_Click(object sender, RoutedEventArgs e) => await LoadDemoAsync();
    internal async Task LoadDemoAsync()
    {
        if (busy)
            return;
        string folder = Path.Combine(workspace.Root, "samples");
        ReaderBox.SelectedIndex = 0;
        await ImportPathsAsync([DemoReports.Create(folder), DemoReports.Create(folder, seed: 79, symbol: "GBPUSD")]);
    }
    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        var dialog = new OpenFolderDialog { Title = "保存用ワークスペースを選択" };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                SwitchWorkspace(dialog.FolderName);
            }
            catch (Exception error) { ShowError(error); }
        }
    }
    internal void SwitchWorkspace(string folder,bool resetConditions=true)
    {
        workspace = new(folder);
        PluginMetricsGrid.ItemsSource = null;
        ExtensionChart.SetSeries(null);
        snapshot = null;
        analysis = null;
        appliedFilter = null;
        cachedAnalysis = null;
        cachedDerived = null;
        if(resetConditions)datasets.Clear();
        RefreshDatasets();
        MainChart.SetData([]);
        AnnualReturnValue.Text=TotalReturnValue.Text="算出不可";
        ClearDashboard();
        ClearHolding();
        if(resetConditions)ResetConditions();
        TradesGrid.ItemsSource = GroupsGrid.ItemsSource = CorrelationGrid.ItemsSource = null;
        CountValue.Text = NetValue.Text = PfValue.Text = DdValue.Text = ZeroValue.Text = "—";
        WinValue.Text="算出不可";
        AnalysisTitle.Text = "戦略を選択して分析してください";
        AnalysisDetail.Text = "保存フォルダーを切り替えました";
        PageLabel.Text = "";
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool select = datasets.Any(d => !d.Selected);
        foreach (var d in datasets)
            d.Selected = select;
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (busy || appliedFilter == null)
            return;
        var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "Armada-trades.csv" };
        if (dialog.ShowDialog(this) == true)
            await RunJob("CSV出力中", async (token, _) => { await Task.Run(() => workspace.ExportCsv(appliedFilter, dialog.FileName, token), token); return "取引CSVを保存しました"; });
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await PageAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await PageAsync(1);
    private async void ClearDates_Click(object sender, RoutedEventArgs e)
    {
        FromDate.SelectedDate = ToDate.SelectedDate = null;
        await AnalyzeAsync();
    }
    private void ChartMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            ApplyChart();
    }
    private void ResetChart_Click(object sender, RoutedEventArgs e)
    {
        MainChart.ResetView();
        DrawdownChart.ResetView();
    }
    private async void GroupMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || busy || snapshot == null)
            return;
        int mode = GroupMode.SelectedIndex;
        await RunJob("時間帯を集計中", async (token, _) => { var rows = await Task.Run(() => Grouping.Compute(snapshot.Points, mode), token); token.ThrowIfCancellationRequested(); GroupsGrid.ItemsSource = rows; return "時間帯集計を更新しました"; });
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        jobCancellation?.Cancel();
        StatusText.Text = "取消を要求しました…";
    }
    internal void CancelJob() => jobCancellation?.Cancel();
    private void Files_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Files_Drop(object sender, DragEventArgs e)
    {
        if (!busy && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await ImportPathsAsync(files);
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (autoRunning) { e.Cancel = true; closeAfterAuto = true; autoCancellation?.Cancel(); autoRunner?.StopNow(); return; }
        CancelSlippage();
        CancelMetricEstimates();
        if (busy)
        {
            e.Cancel = true;
            closeAfterJob = true;
            jobCancellation?.Cancel();
        }
    }
    private async void LoadPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        var dialog = new OpenFileDialog { Filter = "公開プラグインDLL|*.dll", InitialDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins") };
        if (dialog.ShowDialog(this) == true)
            await LoadPluginAsync(dialog.FileName);
    }
    internal async Task LoadPluginAsync(string file) => await RunJob("プラグインを読み込み中", async (token, _) =>
    {
        await Task.Run(() => plugins.Load(file), token);
        PluginsGrid.ItemsSource = plugins.Plugins.Select(p => new PluginView(p)).ToArray();
        ReaderBox.Items.Clear();
        ReaderBox.Items.Add(new ComboBoxItem { Content = "MT4 / MT5 HTML" });
        foreach (var reader in plugins.Plugins.OfType<IReportReaderPlugin>())
            ReaderBox.Items.Add(new ComboBoxItem { Content = reader.Metadata.Name, Tag = reader });
        ReaderBox.SelectedIndex = 0;
        return $"{plugins.Plugins.Count}個の拡張を利用できます";
    });
    private async void RunPlugin_Click(object sender, RoutedEventArgs e) => await RunSelectedPluginAsync();
    internal async Task RunSelectedPluginAsync()
    {
        if (busy || snapshot == null || appliedFilter == null || PluginsGrid.SelectedItem is not PluginView selected)
            return;
        string trials = "1000", steps = "1000", seed = "42";
        await RunJob("拡張機能を実行中", async (token, _) =>
        {
            var result = await Task.Run(() =>
            {
                if (snapshot.Points.LongLength * 96 + 16777216 > ResourceLimits.DefaultMemoryMiB * 1048576L)
                    throw new InvalidOperationException("拡張への入力がメモリ予算を超えます。期間を絞ってください。");
                var points = Array.AsReadOnly(snapshot.Points.Select(p => new TradePoint(p.Ticks, p.Profit, p.DatasetId)).ToArray());
                var data = datasets.Where(d => appliedFilter.DatasetIds.Contains(d.Data.Id)).Select(d => new DatasetInfo(d.Data.Id, d.Data.Name, d.Data.Symbol, "", d.Data.InitialBalance)).ToArray();
                return plugins.Run(selected.Plugin, new(points, data, new Dictionary<string, string> { ["trials"] = trials, ["steps"] = steps, ["seed"] = seed }), token);
            }, token);
            token.ThrowIfCancellationRequested();
            PluginMetricsGrid.ItemsSource = result.Metrics;
            ExtensionChart.SetSeries(result.Charts?.FirstOrDefault());
            return "拡張結果を表示しました";
        });
    }
    private void PluginDocs_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "公開APIは ArmadaAnalyzer.Contracts.dll です。分析指標、MC、グラフ、評価、最適化、読込形式の6種類に対応します。\n\nソース配布の PLUGIN_DEVELOPMENT.md と SamplePlugins を参照してください。", "プラグイン開発", MessageBoxButton.OK, MessageBoxImage.Information);
    private sealed class CallbackProgress(Action<WorkProgress> callback) : IProgress<WorkProgress>
    {
        public void Report(WorkProgress value) => callback(value);
    }
}

public sealed class DatasetView(Dataset data) : INotifyPropertyChanged
{
    public Dataset Data { get; } = data;
    public string Name => Data.Name;
    public string Detail => $"{Data.Symbol} · {Data.Count:N0}取引";
    private bool selected;
    public bool Selected
    {
        get => selected; set
        {
            selected = value;
            PropertyChanged?.Invoke(this, new(nameof(Selected)));
        }
    }
    public string Format => "SQLite";
    private string weight = "1";
    public string Weight
    {
        get => weight; set
        {
            weight = value;
            PropertyChanged?.Invoke(this, new(nameof(Weight)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record PluginView(IPlugin Plugin)
{
    public string Name => Plugin.Metadata.Name;
    public string Version => Plugin.Metadata.Version;
    public string Description => Plugin.Metadata.Description;
    public string Kind => Plugin switch { IReportReaderPlugin => "取込形式", IMonteCarloPlugin => "MC", IChartPlugin => "グラフ", IEvaluationPlugin => "EA評価", IPortfolioOptimizerPlugin => "最適化", _ => "分析指標" };
}
