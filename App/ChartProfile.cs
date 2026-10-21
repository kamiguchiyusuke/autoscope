using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
namespace ArmadaAnalyzer.Desktop;
internal static class ChartProfile
{
    internal static async Task Run(MainWindow w,string output)
    {
        Directory.CreateDirectory(output);await w.AnalyzeAsync();w.CancelMetricEstimates();await w.MetricEstimateTask;await w.SlippageTask;
        w.ApplyAppearance(false,false);w.OverviewScroll.ScrollToTop();await w.Dispatcher.InvokeAsync(()=>w.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        var chart=w.MainChart;int count=w.CurrentAnalysis!.Count;var rows=new List<object>();
        for(int i=0;i<15;i++)
        {
            chart.SetView(i*1000,count-i*2000);await w.Dispatcher.InvokeAsync(()=>w.UpdateLayout(),DispatcherPriority.Render);
            rows.Add(new{total=chart.LastRenderMilliseconds,sample=chart.SampleMilliseconds,axis=chart.AxisMilliseconds,geometry=chart.GeometryMilliseconds,annotations=chart.AnnotationMilliseconds});
        }
        chart.ResetView();w.ApplyAppearance(false,true);await Task.Delay(200);chart.Replay();await Task.Delay(1200);
        File.WriteAllText(Path.Combine(output,"chart-profile.json"),JsonSerializer.Serialize(new{count,rows,frames=chart.Playback.FrameIntervals,costs=chart.Playback.FrameCosts},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown();
    }
}
