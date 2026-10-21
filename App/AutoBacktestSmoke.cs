using MT4BatchReporter;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace ArmadaAnalyzer.Desktop;
internal static class AutoBacktestSmoke
{
    internal static async Task Run(MainWindow w,string output,string config,string? replay=null)
    {
        Directory.CreateDirectory(output);var s=BatchSettings.Load(config);s.OutputFolder=Path.Combine(output,"reports");s.RetryCount=0;s.TimeoutMinutes=Math.Clamp(s.TimeoutMinutes,1,1440);
        var before=s.Jobs.Select(j=>Path.Combine(s.DataFolder,"tester",Path.GetFileNameWithoutExtension(j.EaFile)+".ini")).Distinct().ToDictionary(p=>p,p=>File.Exists(p)?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))):"");
        w.SetAutoSettings(s);w.Tabs.SelectedIndex=4;
        Func<System.Diagnostics.ProcessStartInfo,CancellationToken,Task>? executor=null;
        if(replay!=null)
        {
            var reports=Directory.GetFiles(replay,s.IsMt5?"*.htm":"report.htm",SearchOption.AllDirectories).Where(f=>!f.Contains("scope-analysis")).Order().ToArray();int index=0;
            executor=(start,token)=>{var lines=File.ReadAllLines(s.IsMt5?start.ArgumentList[0][8..]:start.ArgumentList[0],s.IsMt5?System.Text.Encoding.Unicode:System.Text.Encoding.GetEncoding(932));string relative=s.IsMt5?lines.First(l=>l.StartsWith("Report="))[7..]:lines.First(l=>l.StartsWith("TestReport="))[11..];string destination=Path.Combine(s.DataFolder,relative);string source=reports[index++];File.Copy(source,destination);foreach(var gif in Directory.GetFiles(Path.GetDirectoryName(source)!,s.IsMt5?"*.png":"*.gif"))File.Copy(gif,Path.Combine(Path.GetDirectoryName(destination)!,Path.GetFileName(gif)));return Task.CompletedTask;};
        }
        var temporaryReports=Path.Combine(Path.GetTempPath(),"ArmadaScopeFX-Report");
        var existingTemporary=Directory.Exists(temporaryReports)?Directory.GetDirectories(temporaryReports):Array.Empty<string>();
        var watch=System.Diagnostics.Stopwatch.StartNew();var running=w.RunAutoBacktestAsync(s,executor);
        if(!running.IsCompleted && (w.AutoProgressPanel.Visibility!=Visibility.Visible || w.AutoProgress.IsIndeterminate!=w.MotionEnabled))
            throw new Exception("auto loading feedback missing");
        await running;
        var checks=new List<string>();void Check(bool yes,string name){if(!yes)throw new Exception(name+" / "+w.AutoLog.Text);checks.Add(name);}
        Check(w.AutoProgressPanel.Visibility==Visibility.Collapsed&&!w.AutoProgress.IsIndeterminate,"auto_loading_stops_after_completion");
        if(s.UseTds==true)
        {
            var ui=w.ReadAutoSettings();
            Check(w.AutoUseTds.IsVisible&&ui.UseTds==true&&ui.VariableSpreadTds==!s.IsMt5,"TDS_checkbox_and_platform_settings");
        }
        var queue=(System.Collections.ObjectModel.ObservableCollection<TestJob>)w.AutoJobsGrid.ItemsSource;
        var n1=new TestJob{Name=""};var n2=new TestJob{Name="テスト 1"};var n3=new TestJob{Name="テスト２"};queue.Add(n1);queue.Add(n2);queue.Add(n3);
        w.ApplyAutoEa(n1,"sample.ex4");w.ApplyAutoEa(n2,"sample.ex4");w.ApplyAutoEa(n3,"sample.ex4");
        Check(n1.Name=="sample_EURUSD" && n2.Name==n1.Name+"（2）" && n3.Name==n1.Name+"（3）","EA_auto_names_increment");
        n1.Name="手入力名";w.ApplyAutoEa(n1,"other.ex4");Check(n1.Name=="手入力名","explicit_name_preserved");
        queue.Remove(n1);queue.Remove(n2);queue.Remove(n3);
        w.UpdateLayout();
        IEnumerable<System.Windows.DependencyObject> Desc(System.Windows.DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var c=VisualTreeHelper.GetChild(root,i);yield return c;foreach(var child in Desc(c))yield return child;}}
        var date=Desc(w.AutoJobsGrid).OfType<System.Windows.Controls.DatePicker>().First();
        date.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.UIElement.PreviewMouseLeftButtonDownEvent});
        Check(date.IsDropDownOpen,"date_cell_click_opens_calendar");date.IsDropDownOpen=false;
        var oldDate=date.SelectedDate;date.SelectedDate=oldDate!.Value.AddDays(-1);
        Check(((TestJob)date.DataContext).From==date.SelectedDate,"date_selection_updates_job");date.SelectedDate=oldDate;
        Check(!w.AutoJobsGrid.IsReadOnly && !w.AutoJobsGrid.CanUserSortColumns,"queue_is_editable_and_keeps_execution_order");
        Check(s.Jobs.All(j=>j.Status=="両レポート保存完了" || j.Status=="両保存完了（警告あり）"),"all_jobs_native_and_scope_reports_saved");
        Check(w.CurrentWorkspace.ListDatasets().All(d=>d.Name!="report"),"imported_data_has_test_names");
        int uniqueReports=s.Jobs.Select(j=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(j.SavedReport)))).Distinct().Count();
        Check(w.CurrentWorkspace.ListDatasets().Length==uniqueReports,"all_unique_reports_appear_in_data_selection");
        foreach(var j in s.Jobs){Check(File.Exists(j.SavedReport),"native_html_"+j.Name);Check(new FileInfo(ReportFiles.ScopePdfPath(Path.GetDirectoryName(j.SavedReport)!,j.Name)).Length>1000,"scope_pdf_"+j.Name);}
        Check(Directory.GetFiles(s.OutputFolder,"*",SearchOption.AllDirectories).All(f=>new[]{".htm",".html",".gif",".png",".jpg",".jpeg",".pdf"}.Contains(Path.GetExtension(f).ToLowerInvariant())),"output_contains_only_reports_and_native_images");
        Check(!Directory.Exists(temporaryReports)||Directory.GetDirectories(temporaryReports).Except(existingTemporary).Count()==0,"scope_temporary_workspaces_removed");
        Check(before.All(kv=>(File.Exists(kv.Key)?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(kv.Key))):"")==kv.Value),"original_tester_ini_restored_exactly");
        Check(!BatchRunner.HasRecovery(s.DataFolder),"no_pending_recovery");
        Check(!w.AutoData.IsVisible,"data_folder_selection_is_hidden");
        var detected=w.AutoMt4Picker.Items.OfType<Mt4Installation>().FirstOrDefault(x=>s.IsMt5?Mt4Installations.SamePath(x.TerminalFile,s.TerminalFile):x.Name=="FXCM MT4");
        if(detected!=null){w.AutoMt4Picker.SelectedItem=detected;Check(Mt4Installations.SamePath(w.AutoTerminal.Text,detected.TerminalFile) && Mt4Installations.SamePath(w.AutoData.Text,detected.DataFolder) && Directory.Exists(Path.Combine(w.AutoData.Text,s.IsMt5?"MQL5":"MQL4","Experts")),"named_terminal_selection_updates_experts_folder");}
        await w.Dispatcher.InvokeAsync(()=>w.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        var v=(FrameworkElement)w.Content;var b=new RenderTargetBitmap((int)v.ActualWidth,(int)v.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(v);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(b));using(var f=File.Create(Path.Combine(output,"auto-backtest.png")))enc.Save(f);
        w.ApplyAppearance(true,false);w.Width=1180;w.Height=780;
        await w.Dispatcher.InvokeAsync(()=>w.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        b=new RenderTargetBitmap((int)v.ActualWidth,(int)v.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(v);enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(b));using(var f=File.Create(Path.Combine(output,"auto-backtest-dark-compact.png")))enc.Save(f);
        w.ApplyAppearance(false,false);
        if(replay!=null)
        {
            await w.ResetAllDataAsync();Check(w.CurrentWorkspace.ListDatasets().Length==0 && w.CurrentAnalysis==null,"all_reset_removes_imported_data_and_result");
            Check(s.Jobs.All(j=>File.Exists(j.SavedReport)&&File.Exists(ReportFiles.ScopePdfPath(Path.GetDirectoryName(j.SavedReport)!,j.Name))),"all_reset_preserves_source_reports");
            Check(w.CapitalBox.Text=="1000000","all_reset_restores_conditions");
        }
        File.WriteAllText(Path.Combine(output,"smoke.json"),JsonSerializer.Serialize(new{passed=true,checks,elapsed_seconds=watch.Elapsed.TotalSeconds,jobs=s.Jobs.Select(j=>new{j.Name,j.Status,j.SavedReport,j.Assessment})},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(0);
    }
}
