using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls;
namespace ArmadaAnalyzer.Desktop;
public partial class MainWindow
{
    private bool requestedMotion = true;
    private bool compactLayout;
    private bool transitionMeasured, lightweightTransitions;
    internal bool LightweightTransitions => lightweightTransitions;
    internal bool MotionEnabled => requestedMotion && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    private sealed record AppearancePreferences(bool Dark = false, bool Motion = true, bool Sound = true);
    private string AppearancePath => Path.Combine(workspace.Root, "ui-preferences.json");
    private void InitializeAppearance()
    {
        AppearancePreferences settings = new();
        try { if (File.Exists(AppearancePath)) settings = JsonSerializer.Deserialize<AppearancePreferences>(File.ReadAllText(AppearancePath)) ?? new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        ApplyAppearance(settings.Dark, settings.Motion);
        SoundToggle.IsChecked = settings.Sound;
        AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(PlayInteractionSound), true);
        PreviewKeyDown += async (_, e) =>
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (await ExecuteShortcutAsync(key, Keyboard.Modifiers)) e.Handled = true;
        };
        SizeChanged += (_, _) => UpdateAdaptiveLayout();
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
    }
    private void SystemAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
            Dispatcher.Invoke(() => ApplyAppearance(UiTheme.IsDark, requestedMotion));
    }
    internal void ApplyAppearance(bool dark, bool motion)
    {
        requestedMotion = motion;
        ChartPlayback.Configure(MotionEnabled);
        bool paletteChanged = UiTheme.Apply(dark);
        MotionToggle.IsChecked = motion;
        ThemeButton.Content = dark ? "ライト" : "ダーク";
        if (paletteChanged) foreach (var chart in new FrameworkElement[] { MainChart, DrawdownChart, ProfitBars, AverageBars, CountBars, DrawdownBars, HoldingProfitChart, HoldingPipsChart, ExtensionChart }) chart.InvalidateVisual();
        if (paletteChanged) MonthlyGrid.Items.Refresh();
        SetBusyFeedback(busy);
        AutoProgress.IsIndeterminate=autoRunning&&MotionEnabled;
        StopTransition();
    }
    private void SaveAppearance()
    {
        try { File.WriteAllText(AppearancePath, JsonSerializer.Serialize(new AppearancePreferences(UiTheme.IsDark, requestedMotion, SoundToggle.IsChecked == true))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StatusText.Text = "外観設定は今回の起動中のみ適用されます（保存先は読み取り専用）。"; }
    }
    private void Theme_Click(object sender, RoutedEventArgs e) { ApplyAppearance(!UiTheme.IsDark, requestedMotion); SaveAppearance(); }
    private void Motion_Click(object sender, RoutedEventArgs e) { ApplyAppearance(UiTheme.IsDark, MotionToggle.IsChecked == true); SaveAppearance(); }
    internal bool HasTransitionSurface => TransitionSurface != null;
    private FrameworkElement? TransitionSurface => Tabs.SelectedSurface;
    private void StopTransition() { TransitionSurface?.BeginAnimation(OpacityProperty, null); }
    private void AnimateSection()
    {
        StopTransition();
        if (MotionEnabled && !busy && !lightweightTransitions && (RenderCapability.Tier >> 16) > 0 && TransitionSurface is FrameworkElement surface)
        {
            surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            if (!transitionMeasured) { transitionMeasured = true; _ = CheckTransitionBudgetAsync(surface); }
        }
    }
    private async Task CheckTransitionBudgetAsync(FrameworkElement surface)
    {
        int frames = 0;
        TimeSpan previous = TimeSpan.MinValue;
        EventHandler listener = (_, e) => { var now = ((RenderingEventArgs)e).RenderingTime; if (now != previous) { frames++; previous = now; } };
        CompositionTarget.Rendering += listener;
        try { await Task.Delay(180); }
        finally { CompositionTarget.Rendering -= listener; }
        // Avoid repeated full-panel opacity layers when the observed compositor is slow.
        // This is a conservative presentation fallback, not a GPU/FPS benchmark.
        if (frames < 5) { lightweightTransitions = true; surface.BeginAnimation(OpacityProperty, null); }
    }
    private void UpdateAdaptiveLayout()
    {
        bool narrow = ActualWidth < 1320;
        if (narrow != compactLayout) { compactLayout = narrow; SidebarColumn.Width = new GridLength(narrow ? 0 : 232); }
    }
    internal async Task<bool> ExecuteShortcutAsync(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape && busy) { CancelJob(); return true; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.T) { Theme_Click(this, new()); return true; }
        if (modifiers == ModifierKeys.Control && key == Key.Enter) { await AnalyzeAsync(); return true; }
        if (modifiers == ModifierKeys.Control && key == Key.O && !busy) { Import_Click(this, new()); return true; }
        if (modifiers == ModifierKeys.Alt && key >= Key.D1 && key <= Key.D7 && !busy) { Tabs.SelectedIndex = key - Key.D1; return true; }
        if (key == Key.F1 && modifiers == ModifierKeys.None)
        {
            MessageBox.Show(this, "Ctrl+O  データ取込\nCtrl+Enter  分析\nCtrl+Shift+T  ライト / ダーク\nAlt+1〜7  各画面へ移動\nEscape  実行中の処理をキャンセル\n\nヘッダーの「動き」でアニメーションを無効にできます。\nグラフ: ホイールで拡大、ドラッグで移動。", "キーボード操作", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        return false;
    }
}
