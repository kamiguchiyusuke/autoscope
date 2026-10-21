using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Diagnostics;
namespace ArmadaAnalyzer.Desktop;
/// <summary>Keeps the seven visited page visuals attached; hidden pages retain measured size without drawing.</summary>
public sealed class RetainedTabControl : TabControl
{
    private Grid? pageHost;
    private readonly Dictionary<TabItem, ContentPresenter> pages = new();
    internal FrameworkElement? SelectedSurface { get; private set; }
    internal int RetainedPageCount => pages.Count;
    internal int PreparedPages { get; private set; }
    internal double MaxPreparationSliceMilliseconds { get; private set; }
    internal double PreparationMilliseconds { get; private set; }
    private bool preparing;
    internal Func<bool>? CanPrepare { get; set; }
    internal async void PrepareWhenIdle()
    {
        if (preparing) return;
        preparing = true;
        // Build at most two rows of off-screen visuals per idle turn. Input/render have priority.
        foreach (var item in Items.OfType<TabItem>().ToArray())
        {
            if (!IsLoaded || Dispatcher.HasShutdownStarted) break;
            if (pages.ContainsKey(item)) continue;
            ContentPresenter? presenter = null;
            double height = 0;
            while (IsLoaded && !Dispatcher.HasShutdownStarted)
            {
                bool complete = false;
                bool paused = false;
                try { await Dispatcher.InvokeAsync(() =>
                {
                    if (CanPrepare?.Invoke() == false) { paused = true; return; }
                    if (pageHost == null || !Items.Contains(item)) { complete = true; return; }
                    var watch = Stopwatch.StartNew();
                    if (presenter == null)
                    {
                        if (pages.TryGetValue(item, out presenter)) { complete = true; return; }
                        presenter = new ContentPresenter { Content = item.Content, ContentTemplate = item.ContentTemplate, SnapsToDevicePixels = true, Visibility = Visibility.Hidden, MaxHeight = 72 };
                        pages.Add(item, presenter); pageHost.Children.Add(presenter);
                    }
                    if (SelectedItem == item) { presenter.MaxHeight = double.PositiveInfinity; complete = true; }
                    else
                    {
                        height += 66;
                        complete = height >= Math.Max(ActualHeight, 1);
                        presenter.MaxHeight = complete ? double.PositiveInfinity : height;
                        presenter.UpdateLayout();
                    }
                    watch.Stop(); PreparationMilliseconds += watch.Elapsed.TotalMilliseconds;
                    MaxPreparationSliceMilliseconds = Math.Max(MaxPreparationSliceMilliseconds, watch.Elapsed.TotalMilliseconds);
                }, DispatcherPriority.ContextIdle); }
                catch (TaskCanceledException) { return; }
                if (paused) { await Task.Delay(50); continue; }
                if (complete) { if (presenter != null && SelectedItem != item && !KeepLayout(item)) presenter.Visibility = Visibility.Collapsed; PreparedPages++; break; }
                await Task.Delay(1);
            }
        }
    }
    public override void OnApplyTemplate()
    {
        if (pageHost != null) pageHost.Children.Clear();
        pages.Clear();
        base.OnApplyTemplate();
        pageHost = GetTemplateChild("PART_PageHost") as Grid;
        ShowSelection();
    }
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        ShowSelection();
        base.OnSelectionChanged(e);
    }
    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        foreach (var item in pages.Keys.Where(item => !Items.Contains(item)).ToArray())
        {
            pageHost?.Children.Remove(pages[item]);
            pages.Remove(item);
        }
        ShowSelection();
    }
    private bool KeepLayout(TabItem item) => Items.IndexOf(item) is 0 or 6;
    private void ShowSelection()
    {
        if (pageHost == null || SelectedItem is not TabItem item) return;
        foreach (var pair in pages) { pair.Value.BeginAnimation(OpacityProperty, null); pair.Value.Visibility = KeepLayout(pair.Key) ? Visibility.Hidden : Visibility.Collapsed; }
        if (!pages.TryGetValue(item, out var presenter))
        {
            presenter = new ContentPresenter { Content = item.Content, ContentTemplate = item.ContentTemplate, SnapsToDevicePixels = true };
            pages.Add(item, presenter);
            pageHost.Children.Add(presenter);
        }
        presenter.MaxHeight = double.PositiveInfinity;
        presenter.Visibility = Visibility.Visible;
        SelectedSurface = presenter;
    }
}
