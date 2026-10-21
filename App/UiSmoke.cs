using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ArmadaAnalyzer.Desktop;

internal static class UiSmoke
{
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    internal static async Task Run(MainWindow window, string output, bool large = false, bool existing = false, string? pipWidth = null)
    {
        Directory.CreateDirectory(output);
        if(window.DdAmountBox.Text!="250000" || window.DdRelativeBox.Text!="10" || window.FindName("DdPipsBox")!=null)throw new Exception("DD defaults or removed pips control incorrect");
        double startup = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        var checks = new List<string>();
        var intervals = new List<double>();
        var clock = Stopwatch.StartNew();
        double previous = clock.Elapsed.TotalMilliseconds;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
        heartbeat.Tick += (_, _) => { double now = clock.Elapsed.TotalMilliseconds; intervals.Add(now - previous); previous = now; };
        heartbeat.Start();
        void Check(bool condition, string description)
        {
            if (!condition)
                throw new InvalidOperationException("UI smoke failed: " + description + " / " + window.LastError);
            checks.Add(description);
        }
        async Task Render(string name)
        {
            heartbeat.Stop();
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
            await Task.Delay(120);
            var visual = (FrameworkElement)window.Content;
            var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(output, name + ".png"));
            encoder.Save(file);
            previous = clock.Elapsed.TotalMilliseconds;
            heartbeat.Start();
        }
        async Task ProbeChartPlayback()
        {
            window.Tabs.SelectedIndex=0;
            window.ApplyAppearance(false,true);
            window.EquitySection.Visibility=Visibility.Visible;
            window.HoldingSection.Visibility=window.BreakdownSection.Visibility=Visibility.Collapsed;
            window.MainChart.BringIntoView();
            window.Topmost=true;window.Activate();
            await Task.Delay(350);
            window.MainChart.Playback.Stop();window.DrawdownChart.Playback.Stop();
            var analysis=window.CurrentAnalysis;
            var range=window.MainChart.VisibleRange;
            int samples=window.MainChart.SamplingRuns, renders=window.MainChart.RenderPasses;
            window.MainChart.Replay();window.DrawdownChart.Replay();
            Check(window.MainChart.Playback.Running && window.MainChart.Playback.Progress==0,"chart_replay_starts_with_baseline_only");
            for(int attempt=0;attempt<10 && window.MainChart.Playback.Progress==0;attempt++)await Task.Delay(100);
            Check(window.MainChart.Playback.Progress>0,"chart_replay_advances_in_fixed_time");
            await Task.Delay(900);
            Check(!window.MainChart.Playback.Running && window.MainChart.Playback.Progress==1,"chart_replay_stops_frame_subscription_at_completion");
            Check(window.MainChart.SamplingRuns==samples && window.MainChart.RenderPasses==renders,"chart_replay_never_resamples_or_fully_rerenders");
            Check(ReferenceEquals(analysis,window.CurrentAnalysis) && window.MainChart.VisibleRange==range,"chart_replay_preserves_analysis_and_view");
            Check(window.MainChart.ReplayValue(1)==analysis!.Net,"chart_replay_counter_ends_at_exact_actual_net");
            var frames=window.MainChart.Playback.FrameIntervals.Order().ToArray();
            File.WriteAllText(Path.Combine(output,"chart-playback.json"),JsonSerializer.Serialize(new {
                trades=analysis.Count,frames=frames.Length,
                median_interval_ms=frames.Length==0?0:frames[frames.Length/2],
                p95_interval_ms=frames.Length==0?0:frames[(int)((frames.Length-1)*.95)],
                lightweight=window.MainChart.Playback.Lightweight,
                frame_cpu_max_ms=window.MainChart.Playback.FrameCosts.DefaultIfEmpty().Max(),
                frame_cpu_mean_ms=window.MainChart.Playback.FrameCosts.DefaultIfEmpty().Average(),
                full_render_passes=window.MainChart.RenderPasses-renders,
                sampling_runs=window.MainChart.SamplingRuns-samples,
                method="WPF Rendering event cadence, not GPU present timing or guaranteed FPS"
            }));
            var priorRenderMode=RenderOptions.ProcessRenderMode;
            RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
            await Task.Delay(250);
            window.MainChart.Replay();window.DrawdownChart.Replay();await Task.Delay(1100);
            var softwareFrames=window.MainChart.Playback.FrameIntervals.Order().ToArray();
            File.WriteAllText(Path.Combine(output,"chart-software-comparison.json"),JsonSerializer.Serialize(new {
                frames=softwareFrames.Length,median_interval_ms=softwareFrames.Length==0?0:softwareFrames[softwareFrames.Length/2],
                frame_cpu_max_ms=window.MainChart.Playback.FrameCosts.DefaultIfEmpty().Max(),
                method="Diagnostic software-render comparison; production renderer unchanged"
            }));
            RenderOptions.ProcessRenderMode=priorRenderMode;await Task.Delay(250);
            window.MainChart.Replay();window.DrawdownChart.Replay();await Task.Delay(180);await Render("chart-replay-midpoint");
            window.MainChart.SetView(range.First,range.Count);
            Check(!window.MainChart.Playback.Running,"chart_interaction_interrupts_replay");
            await Task.Delay(100);
            window.MainChart.Replay();window.ApplyAppearance(false,false);
            Check(!window.MainChart.Playback.Running && !window.MotionEnabled && window.FindName("ReplayChartButton")==null,"chart_replay_respects_motion_off");
            window.ApplyAppearance(false,true);await Task.Delay(100);
            window.MainChart.Replay();window.Tabs.SelectedIndex=1;await Task.Delay(100);
            Check(!window.MainChart.Playback.Running,"chart_navigation_interrupts_replay");
            window.Tabs.SelectedIndex=0;await Task.Delay(150);
            Check(window.MainChart.Playback.Running,"chart_replays_when_page_is_shown_again");
            await Task.Delay(900);
            await Render("chart-replay-complete");
            window.Topmost=false;
        }
        async Task ProbeMotion()
        {
            var frames = new List<double>();
            TimeSpan? previousFrame = null;
            EventHandler listener = (_, e) => { var now = ((RenderingEventArgs)e).RenderingTime; if (previousFrame.HasValue && now > previousFrame.Value) frames.Add((now - previousFrame.Value).TotalMilliseconds); previousFrame = now; };
            CompositionTarget.Rendering += listener;
            try
            {
                for (int i = 0; i < 6; i++) { window.Tabs.SelectedIndex = i % 2; await Task.Delay(180); }
                window.Tabs.SelectedIndex = 0;
            }
            finally { CompositionTarget.Rendering -= listener; }
            var sortedFrames = frames.Order().ToArray();
            File.WriteAllText(Path.Combine(output, "motion-cadence.json"), JsonSerializer.Serialize(new { lightweight_transition_fallback = window.LightweightTransitions, motion_enabled = window.MotionEnabled, transition_surface = window.HasTransitionSurface, samples = frames.Count, median_frame_interval_ms = sortedFrames.Length == 0 ? 0 : sortedFrames[sortedFrames.Length / 2], p95_frame_interval_ms = sortedFrames.Length == 0 ? 0 : sortedFrames[(int)((sortedFrames.Length - 1) * 0.95)], graphics_tier = RenderCapability.Tier >> 16, dpi_x = VisualTreeHelper.GetDpi(window).PixelsPerInchX, method = "WPF CompositionTarget.Rendering timestamps while switching populated sections; event cadence is not GPU present timing or a guaranteed animation FPS" }));
        }
        try
        {
            Check(!UiTheme.IsDark && window.Tabs.SelectedIndex == 0, "fresh_workspace_starts_in_light_overview");
            Check(window.Title.StartsWith("Auto Scope") && window.Icon != null && typeof(MainWindow).Assembly.GetName().Name == "AutoScope", "scope_brand_title_icon_executable");
            Check(window.SoundToggle.IsChecked == true && File.Exists(Path.Combine(AppContext.BaseDirectory,"Assets","control.wav")), "quiet_local_sound_available_by_default");
            window.SoundToggle.IsChecked = false;
            window.SoundToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            using(var preferences = JsonDocument.Parse(File.ReadAllText(Path.Combine(window.CurrentWorkspace.Root,"ui-preferences.json"))))
                Check(!preferences.RootElement.GetProperty("Sound").GetBoolean(), "sound_off_preference_is_persisted");
            window.SoundToggle.IsChecked = true;
            window.SoundToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(!await window.ExecuteShortcutAsync(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None), "idle_escape_preserves_control_editing");
            window.ApplyAppearance(false, false);
            Check(!window.MotionEnabled && !UiTheme.IsDark, "appearance_light_reduced_motion");
            var originalInk = UiTheme.Get("Ink");
            window.ApplyAppearance(false, false);
            Check(ReferenceEquals(originalInk, UiTheme.Get("Ink")), "unchanged_palette_does_not_rebuild_resources");
            Check(!window.AnalysisToolbar.IsVisible && !window.DatasetPanel.IsVisible,"duplicate_toolbar_and_sidebar_data_hidden");
            Check(window.NavigationPanel.Children.OfType<System.Windows.Controls.RadioButton>().Where(x=>x.Tag?.ToString() is "2" or "5").All(x=>!x.IsVisible),"requested_sidebar_entries_hidden");
            var reducedStarted = new TaskCompletionSource();
            var reducedRelease = new TaskCompletionSource();
            var reducedJob = window.RunJob("読込中", async (_, _) => { reducedStarted.SetResult(); await reducedRelease.Task; return "完了"; });
            await reducedStarted.Task;
            Check(window.BusySpinner.Visibility == Visibility.Visible && !window.JobProgress.IsIndeterminate && !window.BusyRotation.HasAnimatedProperties, "reduced_motion_static_busy_feedback");
            reducedRelease.SetResult();
            await reducedJob;
            window.ApplyAppearance(true, true);
            Check(UiTheme.IsDark, "appearance_dark_palette");
            Check(ReferenceEquals(window.StatusText.Foreground, UiTheme.Get("Muted")), "status_foreground_tracks_theme");
            await window.ExecuteShortcutAsync(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);
            Check(!UiTheme.IsDark, "keyboard_theme_toggle");
            window.ApplyAppearance(true, true);
            await window.ExecuteShortcutAsync(System.Windows.Input.Key.D2, System.Windows.Input.ModifierKeys.Alt);
            Check(window.Tabs.SelectedIndex == 1, "keyboard_section_navigation");
            await window.ExecuteShortcutAsync(System.Windows.Input.Key.D1, System.Windows.Input.ModifierKeys.Alt);
            Check(window.Tabs.SelectedIndex == 0, "appearance_navigation_preserved");
            for (int outcome = 0; outcome < 3; outcome++)
            {
                var started = new TaskCompletionSource<IProgress<WorkProgress>>();
                var release = new TaskCompletionSource();
                int mode = outcome;
                var job = window.RunJob("読込・計算中", async (token, progress) =>
                {
                    started.SetResult(progress);
                    await release.Task.WaitAsync(token);
                    if (mode == 2) throw new InvalidOperationException("QA: expected failure");
                    return "完了";
                });
                var reporter = await started.Task;
                Check(window.IsBusy && window.BusySpinner.Visibility == Visibility.Visible && window.JobProgress.IsIndeterminate == window.MotionEnabled, "busy_unknown_progress_" + outcome);
                if (outcome == 0) {
                    window.Tabs.SelectedIndex = 6;
                    Check(!window.ConditionsPanel.IsEnabled, "busy_navigation_disables_newly_visible_conditions");
                    window.Tabs.SelectedIndex = 0;
                    Check(!window.FiltersPanel.IsEnabled && !window.ImportPanel.IsEnabled, "busy_navigation_disables_returned_toolbar");
                    await Render("busy-indeterminate");
                }
                reporter.Report(new(0.4, "40% 計算中"));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(!window.JobProgress.IsIndeterminate && window.JobProgress.Value == 40, "busy_known_progress_" + outcome);
                // A background estimate can finish while this foreground job is
                // in flight; its pending status cleanup must not hide the outcome.
                if(outcome>0)typeof(MainWindow).GetField("backgroundStatusActive",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,true);
                if (outcome == 1) window.CancelJob();
                release.SetResult();
                await job;
                reporter.Report(new(0.8, "stale"));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(!window.IsBusy && window.BusySpinner.Visibility == Visibility.Hidden && !window.JobProgress.IsIndeterminate && window.StatusText.Text != "stale", "busy_cleanup_and_stale_progress_" + outcome);
                if(outcome==1)Check(window.StatusText.Text.Contains("キャンセル"),"foreground_cancel_survives_background_completion");
                if(outcome==2)Check(window.LastError=="QA: expected failure" && window.StatusText.Text==UserError.Message(new InvalidOperationException("QA: expected failure")),"foreground_error_survives_background_completion");
            }
            window.ApplyAppearance(false, true);
            Check(!MainWindow.CanDisplayPips(["不明", "不明"]) && !MainWindow.CanDisplayPips([" "]) && MainWindow.CanDisplayPips(["EURUSD", "USDJPY"]) && MainWindow.CanDisplayPips(["EURUSD"]), "unknown_symbols_cannot_enable_pips");
            var wideCorrelation = MainWindow.CorrelationForView([new(new DateTime(1900, 1, 1).Ticks, 100, 1), new(new DateTime(2022, 1, 1).Ticks, 100, 2)]);
            Check(wideCorrelation.Rows.Length == 0 && wideCorrelation.Note.Contains("合成損益"), "long_period_correlation_limit_does_not_block_amount_analysis");
            if (existing)
            {
                await window.AnalyzeAsync();
                Check(window.LastError == null && window.CurrentAnalysis?.Count > 0, "provided_trade_report_analyzed");
                window.PipSizeBox.Text = pipWidth ?? "";
                await window.ShowHoldingAsync();
                await Render("holding-real");
                Check(window.HoldingProfitChart.DrawnPoints > 0 && window.HoldingProfitChart.DrawnPoints <= 6008, "provided_report_holding_scatter_bounded");
                Check(window.HoldingPipsChart.DrawnPoints > 0, "provided_report_explicit_pip_width");
                heartbeat.Stop();
                File.WriteAllText(Path.Combine(output, "smoke.json"), JsonSerializer.Serialize(new
                {
                    passed = true,
                    checks,
                    count = window.CurrentAnalysis!.Count,
                    points = window.HoldingProfitChart.DrawnPoints,
                    method = "Provided MT4 report in a separate private QA workspace; pip width explicitly supplied"
                }, new JsonSerializerOptions { WriteIndented = true }));
                Application.Current.Shutdown(0);
                return;
            }
            if (large)
            {
                var watch = Stopwatch.StartNew();
                await window.AnalyzeAsync();
                double analyzeMs = watch.Elapsed.TotalMilliseconds;
                Check(window.LastError == null && window.CurrentAnalysis?.Count == 1000000, "million_trade_analysis_in_real_window");
                Check(window.CurrentAnalysis!.Pips.Average != null && window.AveragePipsValue.Text != "—", "million_pips_present_without_details_button");
                Check(window.TrustScoreValue.Text==$"{NumberDisplay.TwoDecimal(StatisticalTrustScore.Compute(window.CurrentConvergence!.Percent)!.Value)}%" && window.CurrentConvergence?.Percent>0 && window.ConvergenceValue.Text.StartsWith("±"), "million_convergence_from_cached_statistics");
                Check(window.CurrentWinRateConvergence?.LowerPercent is >=0 && window.CurrentWinRateConvergence.UpperPercent is <=100 && window.WinRangeValue.Text.Contains("〜"),"million_win_rate_range_from_unrounded_cached_sets");
                Check(window.CurrentAnalysis.Pips.Recovery!=null && window.DrawdownGrid.Rows?.Length==6,"million_drawdown_recovery_automatic");
                Check(window.TradesGrid.Items.Count == 500, "million_trade_list_only_materializes_500_rows");
                Check(!window.IsBusy && !window.MetricEstimateTask.IsCompleted && window.MetricEstimateProgress.Visibility==Visibility.Visible,"million_metric_simulation_runs_after_basic_analysis");
                var metricTimer=Stopwatch.StartNew();int ticksBefore=intervals.Count;
                await Render("million-trades");
                await ProbeMotion();
                Check(window.MainChart.DrawnPoints <= 4 * window.MainChart.ActualWidth + 2, "million_trade_curve_is_resolution_bounded");
                double renderMs = window.MainChart.LastRenderMilliseconds;
                await window.PageAsync(1);
                Check(window.PageLabel.Text.StartsWith("501"), "million_trade_pagination");
                var holdingWatch = Stopwatch.StartNew();
                window.PipSizeBox.Text = "0.0001";
                await window.ShowHoldingAsync();
                holdingWatch.Stop();
                await Render("million-holding");
                Check(window.HoldingProfitChart.DrawnPoints <= 6008 && window.HoldingProfitChart.DrawnPoints > 0, "million_trade_holding_scatter_bounded");
                Check(window.CurrentTradeDetails?.Count == 1000000 && window.MedianHoldingValue.Text != "—", "million_holding_metrics_automatic_all_rows");
                var detailsItems = window.CurrentTradeDetails;
                var cachedAnalysisWatch = Stopwatch.StartNew();
                await window.AnalyzeAsync();
                double cachedAnalysisMs = cachedAnalysisWatch.Elapsed.TotalMilliseconds;
                Check(ReferenceEquals(detailsItems,window.CurrentTradeDetails), "million_holding_cache_reused_during_analysis");
                window.HoldingMetrics.BringIntoView();
                await Render("million-pips-holding");
                Check(window.TotalHoldingValue.IsVisible && window.AveragePipsValue.IsVisible, "million_metrics_always_expanded");
                await window.MetricEstimateTask;
                Check(window.LastMetricError==null && window.CurrentMetricEstimate?.Trials==1000 && window.PfEstimateValue.Text.Contains("〜") && window.DdEstimateValue.Text.Contains("〜"),"million_automatic_metric_ranges_finish");
                var reportClock=Stopwatch.StartNew();await window.ExportReportAsync(Path.Combine(output,"million-report.pdf"));reportClock.Stop();
                File.WriteAllText(Path.Combine(output,"pdf-timing.json"),JsonSerializer.Serialize(new{milliseconds=reportClock.Elapsed.TotalMilliseconds,bytes=new FileInfo(Path.Combine(output,"million-report.pdf")).Length}));
                Check(File.Exists(Path.Combine(output,"million-report.pdf")),"million_pdf_report_export");
                Check(intervals.Count>ticksBefore+10,"million_dispatcher_continues_during_metric_simulation");
                var simulated=window.CurrentMetricEstimate;int starts=window.MetricSimulationStarts;
                await window.AnalyzeAsync();await window.MetricEstimateTask;
                Check(starts==window.MetricSimulationStarts && ReferenceEquals(simulated,window.CurrentMetricEstimate),"million_metric_range_cache_hit");
                window.ScoreComparison.BringIntoView();await Render("million-estimates");
                await ProbeChartPlayback();
                heartbeat.Stop();
                var sorted = intervals.Order().ToArray();
                File.WriteAllText(Path.Combine(output, "smoke.json"), JsonSerializer.Serialize(new
                {
                    passed = true,
                    checks,
                    startup_ms = startup,
                    metric_simulation_ms = window.CurrentMetricEstimate!.Milliseconds,
                    analysis_ms = analyzeMs,
                    chart_render_ms = renderMs,
                    cached_analysis_ms = cachedAnalysisMs,
                    holding_read_and_display_ms = holdingWatch.Elapsed.TotalMilliseconds,
                    dispatcher_tick_p95_ms = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * 0.95)],
                    dispatcher_tick_max_ms = sorted.LastOrDefault(),
                    graphics_tier = RenderCapability.Tier >> 16,
                    peak_working_set = Process.GetCurrentProcess().PeakWorkingSet64,
                    method = "Actual self-contained WPF app, 1 million synthetic trades; screenshot encoding excluded from dispatcher sampling"
                }, new JsonSerializerOptions { WriteIndented = true }));
                Application.Current.Shutdown(0);
                return;
            }
            await window.LoadDemoAsync();
            await window.SlippageTask;
            Check(window.SlippageValue.Text.StartsWith("約"),"slippage background estimate available");
            Check(System.Windows.Controls.Grid.GetColumn(window.SlippageCard)==0 && System.Windows.Controls.Grid.GetColumn(window.PfComparisonCard)==1,"pips/slippage before PF");
            Check(window.FontFamily.Source.Contains("Mincho"),"Mincho typography");
            Check(window.InitialDdComparisonCard.Children[0] is System.Windows.Controls.Viewbox && window.DdComparisonCard.Children[0] is System.Windows.Controls.Viewbox,"DD values fit on one line");
            Check(window.LastError == null && window.CurrentAnalysis?.Count == 2400, "demo_import_and_portfolio_2400");
            var observed=window.CurrentAnalysis!;
            var beforeReportRange=window.MainChart.VisibleRange;
            await window.ExportReportAsync(Path.Combine(output,"analysis-report.pdf"));
            await window.ExportReportAsync(Path.Combine(output,"analysis-report-repeat.pdf"));
            Check(ReferenceEquals(observed,window.CurrentAnalysis) && window.MainChart.VisibleRange==beforeReportRange && File.ReadAllText(Path.Combine(output,"analysis-report.pdf")).StartsWith("%PDF-1.4"),"pdf_report_repeated_export_preserves_analysis_and_view");
            using(var canceledReport=new CancellationTokenSource()){
                canceledReport.Cancel();bool canceled=false;
                try{await window.ExportReportAsync(Path.Combine(output,"canceled-report.pdf"),canceledReport.Token);}catch(OperationCanceledException){canceled=true;}
                Check(canceled && !File.Exists(Path.Combine(output,"canceled-report.pdf")),"pdf_report_cancel_does_not_leave_partial_output");
            }
            double winProbability=(double)observed.Wins/observed.Count;
            double referenceSets=observed.Count/Math.Max(observed.AverageWinStreak!.Value*(1-winProbability),observed.AverageLossStreak!.Value*winProbability);
            double referenceWidth=.79788456*observed.SampleDeviation!.Value/Math.Sqrt(referenceSets)/observed.Net*observed.Count*100;
            Check(window.FindName("ConvergenceValue") is System.Windows.Controls.TextBlock convergence && convergence.Text==$"±{NumberDisplay.TwoDecimal(referenceWidth)}%", "convergence_uses_new_unrounded_total_sets_and_two_decimal_places");
            Check(window.TrustComparisonLabel.Text=="総損益 / 収束総損益 / 総損益収束率", "profit_convergence_indicator_uses_requested_display_name");
            string expectedCounts=$"2,400 / {Math.Round(referenceSets,MidpointRounding.AwayFromZero):N0}";
            Check(window.CountValue.Text==expectedCounts && ((System.Windows.Controls.StackPanel)window.CountValue.Parent).Children.OfType<System.Windows.Controls.TextBlock>().Last().Text=="総取引回数 / 総セット数", "trade_count_displays_new_total_sets_and_label");
            Check(window.ConvergenceHint.Text.Contains("条件付き平均変動幅（係数0.79788456）") && window.ConvergenceHint.Text.Contains("中央約57.51%") && window.ConvergenceHint.Text.Contains("約42.49%") && !window.ConvergenceHint.Text.Contains("90%") && !window.ConvergenceHint.Text.Contains("68.27%"),"convergence_help_matches_conditional_mean_variation");
            Check(window.MoneyDdComparison.Children.Count==4 && ReferenceEquals(window.MoneyDdComparison.Children[0],window.NetComparisonCard) && ReferenceEquals(window.MoneyDdComparison.Children[1],window.InitialDdComparisonCard) && ReferenceEquals(window.MoneyDdComparison.Children[2],window.DdComparisonCard), "initial_capital_dd_is_immediately_left_of_balance_dd");
            Check(!window.IsBusy && window.MetricSimulationStarts>0,"metric_estimates_auto_start_without_global_busy");
            await window.MetricEstimateTask;
            Check(window.LastMetricError==null && window.CurrentMetricEstimate is {Trials:1000,ProfitFactor.Lower:double,DrawdownPercent.Lower:double} && window.PfEstimateValue.Text.Contains("〜") && window.DdEstimateValue.Text.Contains("〜"),"automatic_pf_dd_estimates_publish_both_ranges");
            var initialRange=window.CurrentMetricEstimate!.DrawdownAmount;
            long referenceCapital=Amount.Parse("1000000");
            Check(window.InitialDdPercentValue.Text==NumberDisplay.TwoDecimal((double)observed.Drawdown/referenceCapital*100)+"%" &&
                window.InitialDdEstimateValue.Text==NumberDisplay.TwoDecimal(initialRange.Lower!.Value/referenceCapital*100)+"〜"+NumberDisplay.TwoDecimal(initialRange.Upper!.Value/referenceCapital*100)+"%" &&
                window.InitialDdBasisValue.Text=="初期資本の"+window.InitialDdPercentValue.Text && window.InitialDdEstimateHint.Text.Contains("初資比DD（初期証拠金比最大DD） = 最大DD金額"),"initial_capital_dd_uses_observed_and_same_simulated_drawdown_amounts");
            decimal upper=window.CurrentConvergence!.UpperTotal!.Value;
            Check(window.NetEstimateValue.Text=="±"+NumberDisplay.GroupedTwoDecimal((upper-observed.Net)/Amount.Scale) && window.DdPercentValue.Text==NumberDisplay.TwoDecimal(observed.DrawdownPercent!.Value)+"%", "net_plus_minus_amount_matches_existing_width_and_observed_dd_is_unchanged");
            Check(window.PfEstimateHint.Text.Contains("中央値") && window.DdEstimateHint.Text.Contains("含み損は含みません") && window.MetricEstimateProgress.Visibility==Visibility.Collapsed,"metric_ranges_document_conditional_means_and_finish_progress");
            var metricResult=window.CurrentMetricEstimate;int metricStarts=window.MetricSimulationStarts;
            await window.AnalyzeAsync();await window.MetricEstimateTask;
            Check(window.MetricSimulationStarts==metricStarts && ReferenceEquals(metricResult,window.CurrentMetricEstimate),"metric_estimates_reuse_identical_input_cache");
            window.MetricRetry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await window.MetricEstimateTask;
            Check(window.MetricSimulationStarts==metricStarts+1 && !ReferenceEquals(metricResult,window.CurrentMetricEstimate),"explicit_metric_retry_recomputes_cached_input");
            metricStarts=window.MetricSimulationStarts;
            window.CapitalBox.Text="2000000";await window.AnalyzeAsync();await window.MetricEstimateTask;
            Check(window.MetricSimulationStarts==metricStarts+1 && !ReferenceEquals(metricResult,window.CurrentMetricEstimate),"metric_estimates_invalidate_when_initial_capital_changes");
            Check(window.InitialDdPercentValue.Text==NumberDisplay.TwoDecimal((double)observed.Drawdown/Amount.Parse("2000000")*100)+"%","initial_capital_dd_recalculates_on_capital_change");
            window.CapitalBox.Text="1000000";await window.AnalyzeAsync();
            window.MetricRetry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            window.CancelMetricEstimates();await window.MetricEstimateTask;
            Check(window.InitialDdEstimateValue.Text=="中止" && window.PfEstimateValue.Text=="中止" && window.MetricEstimateProgress.Visibility==Visibility.Collapsed,"metric_estimate_cancel_does_not_publish_stale_ranges");
            await window.AnalyzeAsync();await window.MetricEstimateTask;
            Check(window.CurrentAnalysis!.Net==observed.Net && window.CurrentAnalysis.Equity.SequenceEqual(observed.Equity),"metric_estimates_leave_observed_totals_and_chart_intact");
            double winMargin=.79788456*Math.Sqrt(winProbability*(1-winProbability)/referenceSets);
            string expectedWinRate=$"{NumberDisplay.TwoDecimal(winProbability*100)}% / {NumberDisplay.TwoDecimal((winProbability-winMargin)*100)}〜{NumberDisplay.TwoDecimal((winProbability+winMargin)*100)}% / 44.7%";
            Check(string.Concat(window.WinRateValues.Children.OfType<System.Windows.Controls.TextBlock>().Select(t=>t.Text))==expectedWinRate && window.WinRateLabel.Text=="勝率 / 収束勝率 / 損益分岐勝率", "win_rate_comparison_matches_order_format_and_existing_breakeven");
            Check(window.WinRateValues.TransformToAncestor(window).Transform(new Point()).Y < window.WinRateLabel.TransformToAncestor(window).Transform(new Point()).Y, "win_rate_numbers_are_above_the_label");
            Check(NumberDisplay.TwoDecimal(56.00)=="56" && NumberDisplay.TwoDecimal(56.20)=="56.2" && NumberDisplay.TwoDecimal(56.02)=="56.02" && NumberDisplay.TwoDecimal(0m)=="0" && NumberDisplay.TwoDecimal(100m)=="100", "fractional_zero_trimming_preserves_integer_zeros_and_significant_digits");
            Check(ReferenceEquals(window.StatisticalTrustCard.Parent,window.EquityChartLayer) && !window.StatisticalTrustCard.IsHitTestVisible && window.StatisticalTrustCard.Margin.Top>window.PeriodTrustCard.Margin.Top,"statistical_score_below_period_score_on_chart");
            Check(ReferenceEquals(window.ConvergenceValue.Parent,window.NetEstimateValue.Parent) && ReferenceEquals(window.PfComparisonCard.Parent,window.ScoreComparison),"convergence_rate_with_net_and_pf_top_row");
            void CheckPeriodScore()
            {
                var points=window.CurrentSnapshot!.Points;
                string expected=points.Length==0?"算出不可":$"{PeriodTrustScore.Compute(new DateTime(points[0].Ticks).Date,new DateTime(points[^1].Ticks).Date):F2}%";
                Check(window.PeriodTrustValue.Text==expected,"period_score_tracks_analyzed_dates");
            }
            CheckPeriodScore();
            Check(ReferenceEquals(window.PeriodTrustCard.Parent,window.EquityChartLayer) && !window.PeriodTrustCard.IsHitTestVisible && window.PeriodTrustValue.FontSize==24.3,"period_score_chart_overlay_keeps_chart_input");
            Check(MainWindow.PeriodScoreColor(85)=="ScoreBlue" && MainWindow.PeriodScoreColor(84.99)=="ScoreYellow" && MainWindow.PeriodScoreColor(75)=="ScoreYellow" && MainWindow.PeriodScoreColor(74.99)=="ScoreRed" && MainWindow.PeriodScoreColor(null)=="Muted","period_score_color_boundaries");
            Check(window.TrustScoreValue.Text==$"{NumberDisplay.TwoDecimal(100m-window.CurrentConvergence!.Percent!.Value)}%" &&
                window.TrustScoreValue.ToolTip.ToString()!.Contains("信頼水準"), "score_uses_existing_unrounded_width_and_is_labeled_independent_rating");
            FrameworkElement OutcomeCard(System.Windows.Controls.TextBlock value)=>value.Parent is System.Windows.Controls.Viewbox fit?(FrameworkElement)fit.Parent:(FrameworkElement)value.Parent;
            var outcomeCards=(System.Windows.Controls.Panel)OutcomeCard(window.WinsCountValue).Parent;
            Check(ReferenceEquals(outcomeCards.Children[0],OutcomeCard(window.WinsCountValue)) && ReferenceEquals(outcomeCards.Children[1],OutcomeCard(window.LossesCountValue)) && ReferenceEquals(outcomeCards.Children[2],OutcomeCard(window.ZeroValue)) && window.ZeroValue.Text=="66", "win_loss_draw_counts_are_adjacent_and_unchanged");
            Check(window.WinRangeValue.ToolTip?.ToString()?.Contains("厳密な信頼区間ではありません")==true,"win_rate_range_is_labeled_armada_approximation");
            Check(window.FindName("UpperScenario")==null && window.FindName("StandardScenario")==null && window.FindName("LowerScenario")==null && window.FindName("ScenarioInformation")==null,
                "simulation_toggle_and_information_panel_completely_withdrawn");
            Check(window.ConvergenceHint.Text.Contains("厳密な統計的保証") && window.NetValue.Text=="+33,946.12", "convergence_is_labeled_estimate_and_leaves_observed_net_unchanged");
            window.SummaryRows.BringIntoView();await Render("convergence-metric");
            await ProbeChartPlayback();
            Check(window.CurrentAnalysis!.ChartDrawdowns.Length==3 && window.MainChart.DrawnBands==3 && window.DrawdownChart.DrawnBands==0,"top_three_dd_bands_only_on_profit_chart");
            Check(!window.MainChart.AnnotationBounds.Where((a,i)=>window.MainChart.AnnotationBounds.Skip(i+1).Any(b=>a.IntersectsWith(b))).Any(),"drawdown_annotation_boxes_do_not_overlap");
            Check(window.MainChart.YearGridLines>0 && window.TotalReturnValue.Text=="3.39%" && window.AnnualReturnValue.Text=="3.1%" && ReferenceEquals(window.ChartReturns.Parent,window.EquityChartLayer) && !window.ChartReturns.IsHitTestVisible,"year_grid_and_return_rates_visible");
            double originalWidth=window.Width;
            window.Width=1180;window.SummaryRows.BringIntoView();await Render("summary-compact");
            Check(window.WinRateValues.Orientation == System.Windows.Controls.Orientation.Horizontal &&
                window.WinRateFit.ActualWidth <= window.WinRateComparison.ActualWidth + 1 &&
                Math.Abs(window.WinValue.TransformToAncestor(window).Transform(new Point()).Y - window.BreakEvenValue.TransformToAncestor(window).Transform(new Point()).Y) < 1,
                "win_rate_all_three_values_stay_on_one_line_at_1180");
            window.ApplyAppearance(true, false); await Render("scope-dark-summary");
            Check(((SolidColorBrush)UiTheme.Get("Canvas")).Color == Color.FromRgb(11,13,16), "scope_dark_carbon_palette");
            window.ApplyAppearance(false, true);
            Check(((SolidColorBrush)UiTheme.Get("Accent")).Color == Color.FromRgb(220,23,35), "scope_light_red_actions");
            window.Width=originalWidth;
            string actualNet=window.NetValue.Text,estimatedNet=window.NetEstimateValue.Text;
            window.NetValue.Text="+11,299,974.43";window.NetEstimateValue.Text="±898,651.86";
            window.SummaryRows.BringIntoView();await Render("summary-long-values");
            window.NetValue.Text=actualNet;window.NetEstimateValue.Text=estimatedNet;
            Check(window.FindName("TotalHoldingValue") is System.Windows.Controls.TextBlock holding && holding.Text != "—",
                "holding_metrics_populated_automatically_without_expanding_or_button");
            Check(window.TotalHoldingValue.Text == "300日\n0時間0分" && window.MaxHoldingValue.Text == "0日\n3時間0分" &&
                window.MinHoldingValue.Text == "0日\n3時間0分" && window.AverageHoldingValue.Text == "0日\n3時間0分" && window.MedianHoldingValue.Text == "0日\n3時間0分",
                "automatic_duration_cards_match_2400_three_hour_trades");
            Check(!window.PayoffValue.IsVisible && !window.DeviationValue.IsVisible && !window.RecoveryValue.IsVisible && !window.SqnValue.IsVisible && !window.MinHoldingValue.IsVisible && !window.MedianHoldingValue.IsVisible,"requested_metrics_hidden_without_changing_calculations");
            Check(window.HoldingMetrics.Columns==3,"three_remaining_holding_cards");
            Check(window.CurrentTradeDetails is { } detail &&
                window.MaxPositionsValue.Text==detail.Positions.Maximum.ToString("N0") &&
                window.LongPositionsValue.Text==detail.Positions.LongMaximum.ToString("N0") &&
                window.ShortPositionsValue.Text==detail.Positions.ShortMaximum.ToString("N0") &&
                detail.Positions.Maximum>0,"concurrent_position_cards_populated");
            window.PositionMetrics.BringIntoView();await Render("concurrent-positions");
            var priorHolding = window.CurrentTradeDetails;
            var canceledAnalysis = window.AnalyzeAsync();
            window.CancelJob();
            await canceledAnalysis;
            Check(ReferenceEquals(priorHolding, window.CurrentTradeDetails) && window.TotalHoldingValue.Text == "300日\n0時間0分",
                "canceled_analysis_preserves_published_holding_metrics");
            Check(window.CurrentAnalysis!.Pips.Average != null && window.AveragePipsValue.Text != "—" && window.TotalPipsValue.Text != "—", "pips_six_metrics_present_in_normal_analysis");
            Check(window.DrawdownGrid.Rows?.Length==5 && window.DrawdownGrid.Rows.All(r=>!r.Name.Contains("pips")),"drawdown_recovery_five_rows_without_pips");
            var prior=window.CurrentAnalysis;
            window.DdAmountBox.Text="1000000000"; await window.AnalyzeAsync();
            Check(window.LastError==null && window.CurrentAnalysis!.Recovery!.Money.Count==0 && window.CurrentAnalysis.Recovery.Money.Maximum==prior.Recovery!.Money.Maximum && !ReferenceEquals(prior,window.CurrentAnalysis),"dd_threshold_invalidates_summary_not_maximum");
            var cached=window.CurrentAnalysis;var cachedConvergence=window.CurrentConvergence;var cachedWinRate=window.CurrentWinRateConvergence;var ddRows=window.DrawdownGrid.Rows;await window.AnalyzeAsync();Check(ReferenceEquals(cached,window.CurrentAnalysis) && ReferenceEquals(ddRows,window.DrawdownGrid.Rows),"dd_unchanged_analysis_reuses_result_and_rows");
            Check(ReferenceEquals(cachedConvergence,window.CurrentConvergence),"unchanged_analysis_reuses_display_only_convergence_result");
            Check(ReferenceEquals(cachedWinRate,window.CurrentWinRateConvergence),"unchanged_analysis_reuses_win_rate_range");
            Check(window.CountValue.Text==expectedCounts,"cached_analysis_preserves_combined_trade_and_set_counts");
            window.DdAmountBox.Text="0";await window.AnalyzeAsync();Check(window.LastError!=null && ReferenceEquals(cached,window.CurrentAnalysis),"dd_invalid_threshold_preserves_previous_result");
            window.DdRelativeBox.Text="0.0001";window.DdAmountBox.Text="250000";await window.AnalyzeAsync();
            Check(window.DrawdownGrid.Rows![1].Threshold=="最高残高の 0.0001%","dd_applied_threshold_retains_input_precision");
            window.DdRelativeBox.Text="10";await window.AnalyzeAsync();
            window.DrawdownGrid.BringIntoView();await Render("drawdown-metrics");
            Check(Descendants(window.DrawdownGrid).OfType<System.Windows.Controls.TextBlock>().Any(t=>t.Text.Contains("金額DD")),"dd_rows_materialize_when_scrolled_into_view");
            Check(window.TradesGrid.Items.Count == 500, "trade_page_contains_only_500_rows");
            Check(window.CorrelationGrid.Items.Count == 1, "portfolio_daily_correlation");
            Check(window.MonthlyGrid.Items.Count == 2, "monthly_matrix_has_two_years");
            Check(window.MonthlyGrid.Columns.Count == 14, "monthly_matrix_has_all_twelve_months_and_total");
            Check(window.CurrentTradeDetails?.Count == 2400 && window.MedianHoldingValue.Text != "—", "holding_metrics_from_all_rows_in_normal_analysis");
            var cachedDetails = window.CurrentTradeDetails;
            await window.AnalyzeAsync();
            Check(ReferenceEquals(cachedDetails,window.CurrentTradeDetails), "unchanged_analysis_reuses_holding_result");
            window.HoldingMetrics.BringIntoView();
            await Render("pips-holding-metrics");
            Check(window.HoldingMetrics.IsVisible && window.PipMetrics.IsVisible &&
                window.PipMetrics.TransformToAncestor(window).Transform(new Point()).Y < window.HoldingMetrics.TransformToAncestor(window).Transform(new Point()).Y &&
                window.HoldingMetrics.TransformToAncestor(window).Transform(new Point()).Y < window.DrawdownGrid.TransformToAncestor(window).Transform(new Point()).Y,
                "pips_and_holding_cards_always_expanded_above_drawdowns");
            Check(!Descendants(window.OverviewScroll).OfType<System.Windows.Controls.Expander>().Any() && window.FindName("MetricsDetail") == null,
                "overview_has_no_metric_expander_or_duplicate_summary");
            window.GrossValue.BringIntoView();
            Check(window.AverageWinsValue.Text == $"{window.CurrentAnalysis!.AverageWinStreak:F2} / 2.16" &&
                window.AverageLossesValue.Text == $"{window.CurrentAnalysis.AverageLossStreak:F2} / 1.86",
                "average_streak_cards_show_observed_and_theoretical_values");
            Check(((System.Windows.Controls.StackPanel)OutcomeCard(window.AverageWinsValue)).Children.OfType<System.Windows.Controls.TextBlock>().Last().Text == "平均連勝数 / 理論平均" &&
                ((System.Windows.Controls.StackPanel)OutcomeCard(window.AverageLossesValue)).Children.OfType<System.Windows.Controls.TextBlock>().Last().Text == "平均連敗数 / 理論平均",
                "average_streak_cards_label_comparison_without_changing_convergence");
            await Render("trade-metrics");
            window.AveragePipsValue.BringIntoView();
            await Render("pips-metrics");
            window.OverviewScroll.ScrollToTop();
            await Render("overview");
            int unloads = 0;
            RoutedEventHandler onUnload = (_, _) => unloads++;
            window.OverviewScroll.Unloaded += onUnload;
            await ProbeMotion();
            window.OverviewScroll.Unloaded -= onUnload;
            Check(unloads == 0 && window.Tabs.RetainedPageCount <= 7, "visited_pages_remain_attached_and_cache_is_bounded");
            int samples = window.MainChart.SamplingRuns;
            window.MainChart.InvalidateVisual();
            await Render("overview-repaint");
            Check(window.MainChart.SamplingRuns == samples, "chart_repaint_reuses_sample");
            await window.ShowBreakdownAsync(DashboardPeriod.Month);
            Check(window.BreakdownSection.Visibility == Visibility.Visible, "monthly_bar_charts_visible");
            await Render("monthly-analysis");
            window.MonthlyGrid.BringIntoView();
            await Render("monthly-table");
            Task canceledPeriod = window.ShowBreakdownAsync(DashboardPeriod.Hour);
            window.CancelJob();
            await canceledPeriod;
            Check(window.ProfitBars.BucketCount == 0, "canceled_period_clears_previous_bars");
            window.OverviewScroll.ScrollToTop();
            foreach (var period in Enum.GetValues<DashboardPeriod>())
                await window.ShowBreakdownAsync(period);
            Check(window.BreakdownSection.Visibility == Visibility.Visible, "all_six_period_views_work");
            window.EquitySection.Visibility = Visibility.Visible;
            window.BreakdownSection.Visibility = Visibility.Collapsed;
            window.PeriodTabs.Children.OfType<System.Windows.Controls.RadioButton>().First().IsChecked = true;
            Check(window.MainChart.DrawnPoints <= 4 * window.MainChart.ActualWidth + 2, "chart_output_bounded_by_resolution");
            Check(window.DrawdownChart.Underwater && window.DrawdownChart.DrawnPoints > 0, "underwater_drawdown_visible_with_equity");
            Check(window.DrawdownChart.VisibleRange == window.MainChart.VisibleRange, "both_charts_share_visible_range");
            double paint = window.MainChart.LastRenderMilliseconds;
            await window.PageAsync(1);
            Check(window.PageLabel.Text.StartsWith("501"), "trade_pagination");
            window.FromDate.SelectedDate = new DateTime(2020, 2, 1);
            window.ToDate.SelectedDate = new DateTime(2020, 2, 29);
            await window.AnalyzeAsync();
            Check(window.CurrentAnalysis?.Count == 174, "date_filter_uses_inclusive_end_day");
            Check(window.TrustScoreValue.Text==(StatisticalTrustScore.Compute(window.CurrentConvergence!.Percent) is decimal updatedScore ? $"{NumberDisplay.TwoDecimal(updatedScore)}%" : "—") && !ReferenceEquals(cachedConvergence,window.CurrentConvergence),"changed_history_recalculates_convergence");
            CheckPeriodScore();
            Check(!ReferenceEquals(cachedWinRate,window.CurrentWinRateConvergence),"changed_history_recalculates_win_rate_range");
            Check(window.CurrentTradeDetails?.Count == 174 && !ReferenceEquals(cachedDetails,window.CurrentTradeDetails) && window.AverageHoldingValue.Text != "—", "changed_filter_recomputes_automatic_holding_metrics");
            window.FromDate.SelectedDate = window.ToDate.SelectedDate = null;
            await window.AnalyzeAsync();
            window.Tabs.SelectedIndex = 6;
            Check(window.ConditionsDataGrid.Items.Count == 2 && window.MinutesChoices.Children.Count == 60, "dedicated_conditions_page_with_data_and_sixty_minutes");
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            var minuteExpander = (System.Windows.Controls.Expander)((FrameworkElement)window.MinutesChoices.Parent).Parent;
            var expansionPeer = new System.Windows.Automation.Peers.ExpanderAutomationPeer(minuteExpander);
            var expansion = (System.Windows.Automation.Provider.IExpandCollapseProvider)expansionPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse)!;
            expansion.Collapse(); window.UpdateLayout();
            Check(!window.MinutesChoices.IsVisible, "calendar_automation_collapses_content");
            expansion.Expand(); window.UpdateLayout();
            Check(window.MinutesChoices.IsVisible, "calendar_automation_expands_content");
            var header = (System.Windows.Controls.Primitives.ToggleButton)minuteExpander.Template.FindName("HeaderSite", minuteExpander);
            if (header != null)
            {
                var toggleHeader = (System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(header).GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)!;
                toggleHeader.Toggle(); window.UpdateLayout();
                Check(!minuteExpander.IsExpanded, "calendar_header_toggle_updates_expansion");
                toggleHeader.Toggle(); window.UpdateLayout();
                Check(minuteExpander.IsExpanded, "calendar_header_toggle_restores_expansion");
            }
            var minuteBox = window.MinutesChoices.Children.OfType<System.Windows.Controls.CheckBox>().First();
            Check(VisualTreeHelper.GetChildrenCount(minuteBox) == 0, "calendar_checkbox_has_no_template_subtree");
            var checkToggle = (System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(minuteBox).GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)!;
            checkToggle.Toggle();
            Check(minuteBox.IsChecked == false, "calendar_checkbox_native_toggle_preserved");
            checkToggle.Toggle();
            Check(minuteBox.IsChecked == true, "calendar_checkbox_selection_restored");
            minuteBox.BringIntoView(); minuteBox.Focus(); window.UpdateLayout();
            Check(minuteBox.IsKeyboardFocused, "calendar_keyboard_focus_reaches_choice");
            var keyboardSource = PresentationSource.FromVisual(minuteBox)!;
            foreach (var keyEvent in new[] { System.Windows.Input.Keyboard.KeyDownEvent, System.Windows.Input.Keyboard.KeyUpEvent })
                minuteBox.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, keyboardSource, 0, System.Windows.Input.Key.Space) { RoutedEvent = keyEvent });
            Check(minuteBox.IsChecked == false, "calendar_space_key_toggles_choice");
            checkToggle.Toggle();

            window.ConditionsDataGrid.BringIntoView();
            await Render("conditions");
            var editGrid = window.ConditionsDataGrid;
            editGrid.SelectedItem = editGrid.Items[0];
            editGrid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(editGrid.Items[0], editGrid.Columns.Single(c=>c.Header?.ToString()=="固定ウェイト"));
            editGrid.Focus();
            Check(editGrid.BeginEdit(), "lightweight_grid_preserves_weight_editing");
            editGrid.UpdateLayout();
            Check(Descendants(editGrid).OfType<System.Windows.Controls.TextBox>().Any(box => box.IsVisible), "lightweight_grid_creates_real_editing_textbox");
            editGrid.CancelEdit(System.Windows.Controls.DataGridEditingUnit.Cell);
            editGrid.CancelEdit(System.Windows.Controls.DataGridEditingUnit.Row);
            Check(editGrid.SelectedItems.Count == 1 && editGrid.Columns[1].CanUserSort, "lightweight_grid_preserves_selection_and_sorting");
            var nameHeader = Descendants(editGrid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(header => header.Column == editGrid.Columns[1]);
            // Invoke the same protected click path as the header's keyboard/mouse handler.
            typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(nameHeader, null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(editGrid.Columns[1].SortDirection == System.ComponentModel.ListSortDirection.Ascending && editGrid.Items.SortDescriptions.Count == 1, "lightweight_grid_header_invocation_sorts_rows");


            window.MinutesChoices.BringIntoView();
            await Render("minute-settings");
            foreach (var box in window.MinutesChoices.Children.OfType<System.Windows.Controls.CheckBox>())
                box.IsChecked = (int)box.Tag == 1;
            await window.ApplyConditionsAsync();
            Check(window.CurrentAnalysis?.Count == 0, "entry_minute_filter_changes_real_analysis");
            await window.MetricEstimateTask;
            Check(window.InitialDdPercentValue.Text=="算出不可" && window.InitialDdEstimateValue.Text=="算出不可" && window.PfEstimateValue.Text=="算出不可" && window.DdEstimateValue.Text=="算出不可","empty_history_does_not_retain_estimated_ranges");
            Check(window.TrustScoreValue.Text=="—" && window.PeriodTrustValue.Text=="算出不可" && window.ConvergenceValue.Text=="算出不可" && window.ConvergenceHint.Text.Contains("記録") && window.CountValue.Text=="0 / 算出不可","empty_data_displays_unavailable_set_count_and_width_with_reason");
            Check(window.WinValue.Text=="算出不可" && window.WinRangeValue.Text=="算出不可" && window.BreakEvenValue.Text=="算出不可", "empty_data_clears_entire_win_rate_comparison");
            Check(window.CurrentTradeDetails?.Count == 0 && window.TotalHoldingValue.Text == "—" && window.MedianHoldingValue.Text == "—",
                "empty_filtered_result_clears_previous_holding_values");
            window.ResetConditions();
            await window.AnalyzeAsync();
            Check(window.CurrentAnalysis?.Count == 2400, "conditions_reset_restores_all_trades");
            await window.ShowHoldingAsync();
            await Render("holding-multiple");
            Check(window.HoldingProfitChart.DrawnPoints > 0 && window.HoldingProfitChart.DrawnPoints <= 6008, "holding_profit_scatter_is_bounded");
            Check(window.HoldingPipsChart.DrawnPoints > 0, "portfolio_pips_converted_per_symbol");
            window.Tabs.SelectedIndex = 1;
            window.CapitalBox.Text = "2000000";
            await window.AnalyzeAsync();
            window.SideBox.SelectedIndex = 1;
            await window.AnalyzeAsync();
            window.Tabs.SelectedIndex = 0;
            while (window.IsBusy) await Task.Delay(10);
            await Render("holding-after-other-page-analysis");
            Check(window.HoldingProfitChart.DrawnPoints > 0 && window.LastError == null, "holding_reloads_when_returning_after_changed_analysis");
            window.SideBox.SelectedIndex = 0;
            window.CapitalBox.Text = "1000000";
            await window.AnalyzeAsync();

            int selectedIndex = 0;
            foreach (var row in window.ConditionsDataGrid.Items.Cast<DatasetView>())
                row.Selected = selectedIndex++ == 0;
            await window.AnalyzeAsync();
            window.PipSizeBox.Text = "0.0001";
            await window.ShowHoldingAsync();
            await Render("holding-time");
            Check(window.HoldingPipsChart.DrawnPoints > 0, "single_symbol_explicit_pip_width_enables_pips");
            window.PipSizeBox.Text = "";
            await window.ShowHoldingAsync();
            Check(window.HoldingPipsChart.DrawnPoints > 0 && window.HoldingSummary.Text.Contains("各通貨ペアの単位で換算"), "single_symbol_dukascopy_auto_pips");
            Check(window.WinsCountValue.Text != "—" && window.AverageProfitValue.Text != "—", "trade_derived_extra_metrics_visible");

            foreach (var row in window.ConditionsDataGrid.Items.Cast<DatasetView>())
                row.Selected = true;
            window.ResetConditions();
            await window.AnalyzeAsync();
            window.Tabs.SelectedIndex = 4;
            await Render("auto-backtest");
            Check(window.AutoJobsGrid != null, "auto_backtest_replaces_monte_carlo_page");
            string plugin = Path.Combine(AppContext.BaseDirectory, "Plugins", "ArmadaAnalyzer.SamplePlugins.dll");
            await window.LoadPluginAsync(plugin);
            Check(window.LastError == null && window.PluginsGrid.Items.Count == 6 && window.ReaderBox.Items.Count == 2, "six_plugin_types_and_reader_menu");
            window.PluginsGrid.SelectedItem = window.PluginsGrid.Items.Cast<PluginView>().First(p => p.Plugin is ArmadaAnalyzer.Contracts.IAnalysisPlugin);
            await window.RunSelectedPluginAsync();
            Check(window.LastError == null && window.PluginMetricsGrid.Items.Count == 2, "external_metric_plugin_from_ui");
            window.Tabs.SelectedIndex = 5;
            await Render("plugins");
            string csv = Path.Combine(output, "export.csv");
            await Task.Run(() => window.CurrentWorkspace.ExportCsv(new(window.CurrentWorkspace.ListDatasets().Select(d => d.Id).ToArray()), csv));
            Check(File.ReadLines(csv).Count() == 2401, "csv_export_has_all_2400_trades");
            string unionFolder = Path.Combine(window.CurrentWorkspace.Root, "union-demo");
            string early = DemoReports.Create(unionFolder, 12, seed: 501, symbol: "EURUSD");
            string late = DemoReports.Create(unionFolder, 12, seed: 502, symbol: "GBPUSD");
            File.WriteAllText(late, File.ReadAllText(late).Replace("2020.", "2022."));
            foreach (var row in window.ConditionsDataGrid.Items.Cast<DatasetView>())
                row.Selected = false;
            await window.ImportPathsAsync([early, late]);
            var pair = window.ConditionsDataGrid.Items.Cast<DatasetView>().Where(row => row.Selected).ToArray();
            window.CurrentWorkspace.SetCurrency(pair[0].Data.Id, "JPY");
            window.CurrentWorkspace.SetCurrency(pair[1].Data.Id, "USD");
            await window.AnalyzeAsync();
            Check(window.LastError == null && window.CurrentAnalysis?.Count == 24, "mixed_currency_metadata_and_disjoint_periods_combine_in_ui");
            Check(window.MainChart.TimeExtent is var span && span.HasValue && new DateTime(span.Value.First).Year == 2020 && new DateTime(span.Value.Last).Year == 2022, "portfolio_displays_entire_union_period");
            Check(window.AnalysisDetail.Text.Contains("金額（通貨の区別なし）"), "currency_labels_replaced_by_unitless_amounts");
            window.Tabs.SelectedIndex = 0;
            window.PeriodTabs.Children.OfType<System.Windows.Controls.RadioButton>().First().IsChecked = true;
            window.OverviewScroll.ScrollToTop();
            await Render("portfolio-full-period");
            int sampleRuns = window.MainChart.SamplingRuns;
            window.ApplyAppearance(false, false);
            await Render("light-overview");
            Check(window.MainChart.SamplingRuns == sampleRuns, "theme_switch_reuses_chart_samples");
            window.ApplyAppearance(true, false);
            await Render("dark-overview");
            window.Tabs.SelectedIndex = 6;
            window.ConditionsScroll.ScrollToTop();
            await Render("dark-conditions");
            var conditionHeading = Descendants(window.ConditionsPanel).OfType<System.Windows.Controls.TextBlock>().Single(text => text.Text == "計算条件設定");
            Check(((SolidColorBrush)conditionHeading.Foreground).Color == ((SolidColorBrush)UiTheme.Get("Ink")).Color, "dark_conditions_text_tracks_palette");
            window.Tabs.SelectedIndex = 0;
            window.Width = 1200;
            await Render("compact-overview");
            Check(window.SidebarColumn.Width.Value == 0, "adaptive_compact_navigation");
            window.Width = 1540;
            window.ApplyAppearance(false, true);

            await window.AnalyzeAsync();
            Check(window.PluginMetricsGrid.Items.Count == 0, "new_analysis_clears_plugin_results");
            await window.RunSelectedPluginAsync();
            var deletionRows=window.ConditionsDataGrid.ItemsSource.Cast<DatasetView>().ToArray();
            Check(deletionRows.Length>1 && window.DeleteSelectedDataButton.Content.ToString()=="選択したデータを削除" && window.ConditionsDataGrid.Columns.Last() is System.Windows.Controls.DataGridTemplateColumn,"conditions_single_and_bulk_delete_controls");
            string capitalBeforeDelete=window.CapitalBox.Text;
            await window.DeleteDatasetsAsync([deletionRows[0].Data.Id]);
            Check(window.ConditionsDataGrid.Items.Count==deletionRows.Length-1 && window.CapitalBox.Text==capitalBeforeDelete && window.CurrentSnapshot?.Points.All(p=>p.DatasetId!=deletionRows[0].Data.Id)==true,"single_delete_refreshes_analysis_preserves_conditions");
            await window.DeleteDatasetsAsync(window.ConditionsDataGrid.ItemsSource.Cast<DatasetView>().Select(d=>d.Data.Id).ToArray());
            await Render("deleted-all");
            Check(window.ConditionsDataGrid.Items.Count==0 && window.CurrentAnalysis==null && window.TrustScoreValue.Text=="—" && window.MainChart.DrawnPoints==0,"bulk_delete_clears_all_stale_results");
            window.SwitchWorkspace(Path.Combine(output, "empty-workspace"));
            Check(window.PluginMetricsGrid.Items.Count == 0, "workspace_switch_clears_plugin_results");
            Check(window.CurrentMetricEstimate==null && window.InitialDdPercentValue.Text=="算出不可" && window.InitialDdEstimateValue.Text=="算出不可" && window.MetricEstimatePanel.Visibility==Visibility.Collapsed,"workspace_switch_clears_metric_estimates");
            Check(window.CurrentConvergence==null && window.ConvergenceValue.Text=="算出不可" && window.TrustScoreValue.Text=="—", "workspace_switch_clears_convergence");
            Check(window.CurrentWinRateConvergence==null && window.WinRangeValue.Text=="算出不可", "workspace_switch_clears_win_rate_range");
            Check(window.CurrentTradeDetails == null && window.TotalHoldingValue.Text == "—" && window.MedianHoldingValue.Text == "—", "workspace_switch_clears_holding_cards");
            window.Tabs.SelectedIndex=0;
            window.EquitySection.Visibility=Visibility.Visible;
            await Task.Delay(150);
            window.MainChart.Replay();await Task.Delay(220);
            Check(!window.MainChart.Playback.Running,"empty_chart_replay_cannot_use_stale_geometry");
            window.Tabs.SelectedIndex = 0;
            heartbeat.Stop();
            double[] sortedIntervals = intervals.Order().ToArray();
            var result = new
            {
                passed = true,
                checks,
                startup_ms = startup,
                overview_chart_render_ms = paint,
                dispatcher_tick_p95_ms = sortedIntervals.Length == 0 ? 0 : sortedIntervals[(int)((sortedIntervals.Length - 1) * 0.95)],
                dispatcher_tick_max_ms = sortedIntervals.LastOrDefault(),
                peak_working_set = Process.GetCurrentProcess().PeakWorkingSet64,
                graphics_tier = System.Windows.Media.RenderCapability.Tier >> 16,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                method = "Actual WPF window, same application methods as UI actions; RenderTargetBitmap captures client visual; heartbeat is paused for QA screenshot encoding"
            };
            File.WriteAllText(Path.Combine(output, "smoke.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(0);
        }
        catch { heartbeat.Stop(); throw; }
    }
}
