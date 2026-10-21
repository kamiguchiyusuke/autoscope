using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace ArmadaAnalyzer.Desktop;
internal static class LayoutAudit
{
    static IEnumerable<DependencyObject> All(DependencyObject root)
    {yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var x in All(VisualTreeHelper.GetChild(root,i)))yield return x;}
    internal static async Task Run(MainWindow w,string output)
    {
        Directory.CreateDirectory(output);await w.LoadDemoAsync();await w.MetricEstimateTask;await w.SlippageTask;
        w.Tabs.SelectedIndex=4;
        var queue=(System.Collections.ObjectModel.ObservableCollection<MT4BatchReporter.TestJob>)w.AutoJobsGrid.ItemsSource;
        var namingJob=new MT4BatchReporter.TestJob{Name="",Symbol="USDJPY"};
        foreach(var symbol in new[]{"USDJPY","EURUSD"})if(!w.AutoSymbols.Contains(symbol))w.AutoSymbols.Add(symbol);
        queue.Add(namingJob);w.ApplyAutoEa(namingJob,"NamingCheck.ex4");
        await w.Dispatcher.InvokeAsync(()=>w.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        var pair=All(w.AutoJobsGrid).OfType<ComboBox>().First(c=>ReferenceEquals(c.DataContext,namingJob)&&ReferenceEquals(c.ItemsSource,w.AutoSymbols));
        pair.SelectedItem="EURUSD";
        await w.Dispatcher.InvokeAsync(()=>w.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        if(namingJob.Name!="NamingCheck_EURUSD"||!All(w.AutoJobsGrid).OfType<TextBlock>().Any(t=>t.Text==namingJob.Name))throw new Exception("通貨変更による自動名・画面表示の更新に失敗");
        namingJob.Name="手入力名";pair.SelectedItem="USDJPY";
        if(namingJob.Name!="手入力名")throw new Exception("手入力名が変更された");
        queue.Remove(namingJob);
        File.WriteAllText(Path.Combine(output,"naming-ui.json"),"{\"pair_change_updates_visible_name\":true,\"manual_name_preserved\":true}");
        await w.ShowHoldingAsync();
        await w.LoadPluginAsync(Path.Combine(AppContext.BaseDirectory,"Plugins","ArmadaAnalyzer.SamplePlugins.dll"));
        w.PluginsGrid.SelectedItem=w.PluginsGrid.Items.Cast<PluginView>().First(p=>p.Plugin is ArmadaAnalyzer.Contracts.IAnalysisPlugin);await w.RunSelectedPluginAsync();
        var warnings=new List<object>();int captures=0;
        async Task Capture(string name)
        {
            await w.Dispatcher.InvokeAsync(()=>w.UpdateLayout(),DispatcherPriority.Render);await Task.Delay(80);
            var v=(FrameworkElement)w.Content;var image=new RenderTargetBitmap((int)v.ActualWidth,(int)v.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(v);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(output,name+".png")))encoder.Save(file);captures++;
            foreach(var text in All(w.Tabs.SelectedSurface??w.Tabs).OfType<TextBlock>().Where(t=>t.IsVisible && t.ActualWidth>0 && t.Text.Length>0 && t.TextTrimming==TextTrimming.None))
            {
                var ft=new FormattedText(text.Text,System.Globalization.CultureInfo.CurrentCulture,text.FlowDirection,new Typeface(text.FontFamily,text.FontStyle,text.FontWeight,text.FontStretch),text.FontSize,text.Foreground,VisualTreeHelper.GetDpi(text).PixelsPerDip);
                if(text.TextWrapping!=TextWrapping.NoWrap)ft.MaxTextWidth=Math.Max(1,text.ActualWidth);
                if(ft.Width>text.ActualWidth+2 || ft.Height>text.ActualHeight+3)warnings.Add(new {screen=name,text=text.Text,width=text.ActualWidth,needed=ft.Width,height=text.ActualHeight,neededHeight=ft.Height});
            }
        }
        foreach(int width in new[]{1540,1180})foreach(bool dark in new[]{false,true})
        {
            w.Width=width;w.Height=1000;w.ApplyAppearance(dark,false);
            w.EquitySection.Visibility=Visibility.Visible;w.HoldingSection.Visibility=Visibility.Collapsed;w.BreakdownSection.Visibility=Visibility.Collapsed;
            string prefix=$"{width}-{(dark?"dark":"light")}";
            for(int page=0;page<7;page++)
            {
                w.Tabs.SelectedIndex=page;await w.Dispatcher.InvokeAsync(()=>w.UpdateLayout(),DispatcherPriority.ApplicationIdle);
                var scroll=page==0?w.OverviewScroll:page==6?w.ConditionsScroll:null;
                scroll?.ScrollToTop();await Capture(prefix+"-page"+page+"-top");
                if(scroll!=null)
                {
                    for(double offset=scroll.ViewportHeight*.8;offset<scroll.ExtentHeight;offset+=scroll.ViewportHeight*.8){scroll.ScrollToVerticalOffset(offset);await Capture(prefix+"-page"+page+"-scroll"+(int)offset);}
                    scroll.ScrollToTop();
                }
            }
            w.Tabs.SelectedIndex=0;
            foreach(var period in Enum.GetValues<DashboardPeriod>()){await w.ShowBreakdownAsync(period);w.OverviewScroll.ScrollToTop();await Capture(prefix+"-breakdown-"+period);}
            await w.ShowHoldingAsync();w.OverviewScroll.ScrollToTop();await Capture(prefix+"-holding");
            w.Tabs.SelectedIndex=6;
            foreach(var panel in new[]{w.MonthsChoices,w.DaysChoices,w.WeekdaysChoices,w.HoursChoices})
            {
                var expander=(Expander)((FrameworkElement)panel.Parent).Parent;expander.IsExpanded=true;panel.BringIntoView();await Capture(prefix+"-conditions-"+panel.Name);expander.IsExpanded=false;
            }
        }
        File.WriteAllText(Path.Combine(output,"layout.json"),JsonSerializer.Serialize(new{captures,warnings},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(0);
    }
}
