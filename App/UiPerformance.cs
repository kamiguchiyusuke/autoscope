using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
namespace ArmadaAnalyzer.Desktop;
internal static class UiPerformance
{
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(nint thread, out long created, out long exited, out long kernel, out long user);
    private static double CpuMilliseconds() { GetThreadTimes((nint)(-2), out _, out _, out long kernel, out long user); return (kernel + user) / 10000.0; }
    private static int CountVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = parent is T ? 1 : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) count += CountVisual<T>(VisualTreeHelper.GetChild(parent, i));
        return count;
    }
    internal static async Task Run(MainWindow window, string output)
    {
        Directory.CreateDirectory(output);
        var initialWatch = Stopwatch.StartNew();
        await window.AnalyzeAsync();
        double initialAnalysis = initialWatch.Elapsed.TotalMilliseconds;
        if (window.LastError != null || window.CurrentAnalysis?.Count != 1000000) throw new InvalidOperationException("Expected normalized million-trade workspace: " + window.LastError);
        window.ApplyAppearance(false, false);
        bool nativeVisible = IsWindowVisible(new WindowInteropHelper(window).Handle);
        bool foreground = GetForegroundWindow() == new WindowInteropHelper(window).Handle;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var readyWatch = Stopwatch.StartNew();
        while (window.Tabs.PreparedPages < 6 && readyWatch.ElapsedMilliseconds < 3000)
            await Task.Delay(25);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        double idlePreparationWait = readyWatch.Elapsed.TotalMilliseconds;
        var repeatAnalysis = new List<double>();
        var initialResult = window.CurrentAnalysis;
        for (int repeat = 0; repeat < 3; repeat++)
        {
            var repeatWatch = Stopwatch.StartNew(); await window.AnalyzeAsync();
            repeatAnalysis.Add(repeatWatch.Elapsed.TotalMilliseconds);
            if (!ReferenceEquals(initialResult, window.CurrentAnalysis)) throw new InvalidOperationException("unchanged analysis was rebuilt");
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        int loaded = 0, unloaded = 0;
        window.OverviewScroll.Loaded += (_, _) => loaded++;
        window.OverviewScroll.Unloaded += (_, _) => unloaded++;
        var rows = new List<object>();
        var all = new List<double>();
        for (int cycle = 0; cycle < 5; cycle++)
            foreach (int index in Environment.GetEnvironmentVariable("ARMADA_CONDITIONS_FIRST") == "1" ? new[] { 6, 0, 1, 2, 3, 4, 5 } : new[] { 0, 1, 2, 3, 4, 5, 6 })
            {
                if (window.Tabs.SelectedIndex == index) continue;
                double cpuStart = CpuMilliseconds();
                var watch = Stopwatch.StartNew();
                window.Tabs.SelectedIndex = index;
                double dispatch = watch.Elapsed.TotalMilliseconds;
                window.UpdateLayout();
                double synchronousLayout = watch.Elapsed.TotalMilliseconds;
                double synchronousCpu = CpuMilliseconds() - cpuStart;
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                double layout = watch.Elapsed.TotalMilliseconds;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                double settled = watch.Elapsed.TotalMilliseconds;
                rows.Add(new { cycle, page = index, dispatch_ms = dispatch, synchronous_layout_ms = synchronousLayout, ui_cpu_ms = synchronousCpu, layout_ms = layout, settled_ms = settled, realized_grid_rows_total_retained = cycle == 0 ? CountVisual<System.Windows.Controls.DataGridRow>(window.Tabs) : -1, realized_active_grid_rows = cycle == 0 ? CountVisual<System.Windows.Controls.DataGridRow>(window.Tabs.SelectedSurface ?? window.Tabs) : -1 });
                if (cycle > 0) all.Add(layout);
                await Task.Delay(25); // excluded from every navigation measurement
            }
        window.Tabs.SelectedIndex = 6;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var interactions = new List<object>();
        async Task MeasureInteraction(string name, Action action)
        {
            double cpuStart = CpuMilliseconds(); var watch = Stopwatch.StartNew();
            action(); window.UpdateLayout();
            double cpu = CpuMilliseconds() - cpuStart;
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
            interactions.Add(new { name, layout_ms = watch.Elapsed.TotalMilliseconds, ui_cpu_ms = cpu });
        }
        await MeasureInteraction("first_scroll_to_minutes", () => window.MinutesChoices.BringIntoView());
        foreach (var panel in new[] { window.MonthsChoices, window.DaysChoices, window.WeekdaysChoices, window.HoursChoices })
        {
            var expander = (System.Windows.Controls.Expander)((FrameworkElement)panel.Parent).Parent;
            await MeasureInteraction("expand_" + panel.Name, () => { expander.IsExpanded = true; panel.BringIntoView(); });
            expander.IsExpanded = false;
        }
        window.ConditionsScroll.ScrollToTop();
        if (unloaded != 0) throw new InvalidOperationException("visited overview detached during navigation");
        window.Tabs.SelectedIndex = 0;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        window.ApplyAppearance(false, true);
        var motionNavigation = new List<double>();
        for (int i = 0; i < 14; i++)
        {
            var motionWatch = Stopwatch.StartNew();
            window.Tabs.SelectedIndex = i % 7;
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
            motionNavigation.Add(motionWatch.Elapsed.TotalMilliseconds);
            await Task.Delay(180); // allow the bounded transition to finish, excluded
        }
        window.Tabs.SelectedIndex = 0;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        bool activated = window.Activate();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        bool foregroundDuringAnimation = GetForegroundWindow() == new WindowInteropHelper(window).Handle;
        var frames = new List<double>();
        TimeSpan? previous = null;
        EventHandler listener = (_, args) => { var now = ((RenderingEventArgs)args).RenderingTime; if (previous.HasValue && now > previous.Value) frames.Add((now - previous.Value).TotalMilliseconds); previous = now; };
        CompositionTarget.Rendering += listener;
        try
        {
            window.AnalysisTitle.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(200)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            await Task.Delay(1200);
        }
        finally { window.AnalysisTitle.BeginAnimation(UIElement.OpacityProperty, null); CompositionTarget.Rendering -= listener; }
        double Percentile(List<double> values, double p) { var sorted = values.Order().ToArray(); return sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * p)]; }
        File.WriteAllText(Path.Combine(output, "navigation.json"), JsonSerializer.Serialize(new { passed = true, idle_preparation_wait_ms = idlePreparationWait, prepared_pages = window.Tabs.PreparedPages, preparation_ms = window.Tabs.PreparationMilliseconds, maximum_preparation_slice_ms = window.Tabs.MaxPreparationSliceMilliseconds, animation_window_activated = activated, animation_window_foreground = foregroundDuringAnimation, animation_title_visible = window.AnalysisTitle.IsVisible, warm_motion_layout_p95_ms = Percentile(motionNavigation,0.95), lightweight_transition_fallback = window.LightweightTransitions, native_window_visible = nativeVisible, foreground_at_start = foreground, warm_layout_p95_within_16_67_ms = Percentile(all,0.95) <= 16.67, warm_layout_budget_hit_fraction = (double)all.Count(ms => ms <= 16.67) / all.Count, initial_analysis_ms = initialAnalysis, repeat_analysis_ms = repeatAnalysis, count = window.CurrentAnalysis.Count, conditions_first = Environment.GetEnvironmentVariable("ARMADA_CONDITIONS_FIRST") == "1", interactions, rows, warm_layout_median_ms = Percentile(all,0.5), warm_layout_p95_ms = Percentile(all,0.95), warm_layout_max_ms = all.Max(), overview_loaded = loaded, overview_unloaded = unloaded, active_animation_frame_samples = frames.Count, active_animation_interval_median_ms = Percentile(frames,0.5), active_animation_interval_p95_ms = Percentile(frames,0.95), graphics_tier = RenderCapability.Tier >> 16, peak_working_set = Process.GetCurrentProcess().PeakWorkingSet64, method = "Same 1m workspace, all seven sections, four measured warm rounds after one first-visit round; navigation times exclude 25ms pacing. Frames measured separately during continuous small-title animation; WPF event intervals, not GPU present FPS." }, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(0);
    }
}
