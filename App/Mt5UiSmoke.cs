using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace ArmadaAnalyzer.Desktop;
internal static class Mt5UiSmoke
{
    // Opt-in local fixture integration test. Reports are never copied into the repository.
    internal static async Task Run(MainWindow window,string output,string[] files)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();
        void Check(bool value,string label){if(!value)throw new InvalidOperationException(label+": "+window.LastError);checks.Add(label);}
        Check((window.ReaderBox.SelectedItem as ComboBoxItem)?.Content?.ToString()=="MT4 / MT5 HTML","automatic_reader_selected");
        var watch=Stopwatch.StartNew();await window.ImportPathsAsync(files);double importMs=watch.Elapsed.TotalMilliseconds;
        Check(window.LastError==null,"mt5_files_imported_through_ui_action");
        Check(window.CurrentWorkspace.ListDatasets().Length==3,"three_mt5_datasets");
        Check(window.CurrentAnalysis?.Count==2867,"all_deals_reconstructed_2867");
        Check(window.CurrentAnalysis?.Net==Amount.Parse("255886.10"),"combined_net_includes_entry_commission");
        Check(window.CurrentAnalysis?.Pips.Total==20652.4m,"gold_pips_for_both_broker_aliases");
        Check(window.CurrentTradeDetails?.Count==2867,"holding_times_calculated");
        await window.MetricEstimateTask;Check(window.CurrentMetricEstimate?.Trials==1000,"existing_pf_dd_simulation_finishes");
        var metadata=window.CurrentWorkspace.LoadReportMetadata(new(window.CurrentWorkspace.ListDatasets().Select(d=>d.Id).ToArray()));
        File.WriteAllText(Path.Combine(output,"metadata.json"),JsonSerializer.Serialize(metadata));
        Check(metadata.All(m=>m.Fields.GetValueOrDefault("形式")=="MT5"),"mt5_original_report_header_detected");
        await window.ExportReportAsync(Path.Combine(output,"mt5-report.pdf"));
        foreach(int page in new[]{1,2,3,6,0}){window.Tabs.SelectedIndex=page;await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);}
        Check(window.MainChart.DrawnPoints>0,"existing_charts_render");
        await window.ImportPathsAsync(files);Check(window.LastError==null && window.CurrentWorkspace.ListDatasets().Length==3 && window.CurrentAnalysis?.Count==2867,"duplicate_import_is_idempotent");
        window.OverviewScroll.ScrollToTop();await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.Render);
        var visual=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,"mt5-overview.png")))encoder.Save(stream);
        File.WriteAllText(Path.Combine(output,"smoke.json"),JsonSerializer.Serialize(new {passed=true,checks,import_and_analysis_ms=importMs,count=window.CurrentAnalysis!.Count,net=Amount.Exact(window.CurrentAnalysis.Net),pips=window.CurrentAnalysis.Pips.Total},new JsonSerializerOptions{WriteIndented=true}));
        Application.Current.Shutdown(0);
    }
}
