using Microsoft.Win32;
using MT4BatchReporter;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace ArmadaAnalyzer.Desktop;
public partial class MainWindow
{
    private readonly ObservableCollection<TestJob> autoJobs = [];
    private BatchRunner? autoRunner;
    private CancellationTokenSource? autoCancellation;
    private bool autoRunning, closeAfterAuto;
    private string autoLastOutput = "";
    private string AutoSettingsPath => Path.Combine(workspace.Root,"auto-backtest.json");
    private void InitializeAutoBacktest()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        AutoJobsGrid.ItemsSource=autoJobs;
        autoJobs.CollectionChanged+=(_,_)=>UpdateAutoQueueHeight();
        RefreshMt4Choices();
        tdsInstalled=AutoIsMt5?Mt5Tds.Available:Mt4MouseSettings.TdsInstalled();AutoUseTds.Visibility=tdsInstalled?Visibility.Visible:Visibility.Collapsed;AutoUseTds.IsChecked=tdsInstalled;
        AutoOutput.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Auto Scope","レポート");
        try { if(File.Exists(AutoSettingsPath)) SetAutoSettings(BatchSettings.Load(AutoSettingsPath)); }
        catch(Exception ex){AutoLog.Text="一覧の読込に失敗しました："+ex.Message;}
    }
    internal void SetAutoSettings(BatchSettings s)
    {
        SelectMt4(s.TerminalFile,s.DataFolder,s.Portable);AutoOutput.Text=s.OutputFolder;
        AutoTimeout.Text=s.TimeoutMinutes.ToString();AutoPortable.IsChecked=s.Portable;AutoUseTds.IsChecked=tdsInstalled && (s.UseTds??true);
        autoJobs.Clear();foreach(var j in s.Jobs)autoJobs.Add(j);
        foreach(var j in autoJobs)JobNaming.Apply(j,j.EaFile,j.Symbol,autoJobs);
    }
    internal BatchSettings ReadAutoSettings()
    {
        if(!AutoJobsGrid.CommitEdit(DataGridEditingUnit.Cell,true)||!AutoJobsGrid.CommitEdit(DataGridEditingUnit.Row,true))throw new InvalidOperationException("一覧の入力値を確認してください。");
        return new(){TerminalFile=AutoTerminal.Text.Trim(),DataFolder=AutoData.Text.Trim(),OutputFolder=AutoOutput.Text.Trim(),TimeoutMinutes=int.Parse(AutoTimeout.Text,CultureInfo.InvariantCulture),UseCurrentServer=true,ServerName="",VariableSpreadTds=UseTdsEnabled&&!AutoIsMt5,UseTds=UseTdsEnabled,Portable=AutoPortable.IsChecked==true,DisableTickDataSuite=!UseTdsEnabled,CancelStartupDialogs=true,RetryCount=0,Jobs=autoJobs.ToList()};
    }
    private void AutoLogLine(string text){AutoLog.AppendText(text+Environment.NewLine);if(AutoLog.Text.Length>24000)AutoLog.Text=AutoLog.Text[^16000..];AutoLog.ScrollToEnd();}
    private void AutoBrowse_Click(object sender,RoutedEventArgs e)
    {
        var kind=(sender as FrameworkElement)?.Tag?.ToString();
        if(kind=="terminal")
        {
            var d=new OpenFileDialog{Filter="MT4 / MT5|terminal.exe;terminal64.exe"};if(d.ShowDialog(this)!=true)return;
            RefreshMt4Choices();
            var found=mt4Choices.FirstOrDefault(x=>Mt4Installations.SamePath(x.TerminalFile,d.FileName));
            string home=Path.GetDirectoryName(d.FileName)!;
            SelectMt4(d.FileName,found?.DataFolder??(Directory.Exists(Path.Combine(home,Path.GetFileName(d.FileName)=="terminal64.exe"?"MQL5":"MQL4","Experts"))?home:""),found?.Portable??true);
        }
        else{var d=new OpenFolderDialog();if(d.ShowDialog(this)==true){if(kind=="data")AutoData.Text=d.FolderName;else AutoOutput.Text=d.FolderName;}}
    }
    private void AutoQueue_SizeChanged(object sender,SizeChangedEventArgs e)=>UpdateAutoQueueHeight();
    private void UpdateAutoQueueHeight()
    {
        if(AutoJobsGrid==null || AutoQueueArea==null)return;
        AutoJobsGrid.Height=Math.Min(54+autoJobs.Count*36,Math.Max(54,AutoQueueArea.ActualHeight-48));
    }
    private void AutoAdd_Click(object sender,RoutedEventArgs e){var j=NewAutoJob("");autoJobs.Add(j);AutoJobsGrid.SelectedItem=j;}
    private void AutoDuplicate_Click(object sender,RoutedEventArgs e){if(((sender as FrameworkElement)?.DataContext as TestJob ?? AutoJobsGrid.SelectedItem as TestJob) is TestJob j){var copy=j.Clone();copy.Name+=" コピー";autoJobs.Add(copy);AutoJobsGrid.SelectedItem=copy;}}
    private void AutoDelete_Click(object sender,RoutedEventArgs e){if(((sender as FrameworkElement)?.DataContext as TestJob ?? AutoJobsGrid.SelectedItem as TestJob) is TestJob j)autoJobs.Remove(j);}
    private void AutoMove_Click(object sender,RoutedEventArgs e){if(AutoJobsGrid.SelectedItem is TestJob j){int a=autoJobs.IndexOf(j),b=a+int.Parse(((FrameworkElement)sender).Tag.ToString()!);if(b>=0&&b<autoJobs.Count)autoJobs.Move(a,b);}}
    private void AutoJobFile_CellClick(object sender,System.Windows.Input.MouseButtonEventArgs e)
    {
        if(autoRunning)return;
        if((sender as FrameworkElement)?.DataContext is not TestJob job)return;
        AutoJobsGrid.SelectedItem=job;
        e.Handled=true;
        AutoJobFile_Click(sender,e);
    }
    private void AutoJobFile_Click(object sender,RoutedEventArgs e)
    {
        string dir=Path.Combine(AutoData.Text,AutoIsMt5?"MQL5":"MQL4","Experts");
        if(string.IsNullOrWhiteSpace(AutoTerminal.Text)||!Directory.Exists(dir)){AutoLogLine("先にMT4 / MT5を選択してください。保存場所が見つからない場合は、そのMT4を一度起動してから「再検出」してください。");return;}
        bool ea=((FrameworkElement)sender).Tag.ToString()=="ea";
        var d=new OpenFileDialog{Filter=ea?(AutoIsMt5?"MT5 EA|*.ex5":"MT4 EA|*.ex4"):"設定ファイル|*.set",InitialDirectory=dir,RestoreDirectory=true,ClientGuid=Guid.NewGuid(),AddToRecent=false};
        if(d.ShowDialog(this)==true)
        {
            var j=(sender as FrameworkElement)?.DataContext as TestJob ?? AutoJobsGrid.SelectedItem as TestJob;
            if(j==null){j=NewAutoJob(Path.GetFileNameWithoutExtension(d.FileName));autoJobs.Add(j);AutoJobsGrid.SelectedItem=j;}
            if(ea)ApplyAutoEa(j,d.FileName);else j.SetFile=d.FileName;AutoJobsGrid.Items.Refresh();
        }
    }
    internal void ApplyAutoEa(TestJob j,string file)
    {
        JobNaming.Apply(j,file,j.Symbol,autoJobs);
    }
    private void AutoSymbol_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(autoRunning || sender is not ComboBox {DataContext:TestJob j,SelectedItem:string symbol})return;
        JobNaming.Apply(j,j.EaFile,symbol,autoJobs,e.RemovedItems.OfType<string>().FirstOrDefault());
    }
    private void AutoLoad_Click(object sender,RoutedEventArgs e){try{var d=new OpenFileDialog{Filter="テスト一覧|*.json"};if(d.ShowDialog(this)==true)SetAutoSettings(BatchSettings.Load(d.FileName));}catch(Exception ex){AutoLogLine(ex.Message);}}
    private void AutoSave_Click(object sender,RoutedEventArgs e){try{var s=ReadAutoSettings();var d=new SaveFileDialog{Filter="テスト一覧|*.json",FileName="連続バックテスト.json"};if(d.ShowDialog(this)==true)s.Save(d.FileName);}catch(Exception ex){AutoLogLine(ex.Message);}}
    private void AutoRecover_Click(object sender,RoutedEventArgs e){try{BatchRunner.RecoverSafely(AutoTerminal.Text,AutoData.Text);AutoLogLine("元のテスター設定を復元しました。");}catch(Exception ex){AutoLogLine(ex.Message);}}
    private async void AutoStart_Click(object sender,RoutedEventArgs e){try{await RunAutoBacktestAsync(ReadAutoSettings());}catch(Exception ex){AutoLogLine(ex.Message);}}
    private void AutoStop_Click(object sender,RoutedEventArgs e){autoRunner?.StopAfterCurrent();AutoLogLine("現在のテストと両レポートの保存後に停止します。");}
    private void AutoCancel_Click(object sender,RoutedEventArgs e){autoCancellation?.Cancel();autoRunner?.StopNow();}
    private void AutoOpen_Click(object sender,RoutedEventArgs e){try{string dir=autoLastOutput.Length>0?autoLastOutput:AutoOutput.Text;if(Directory.Exists(dir))Process.Start(new ProcessStartInfo(dir){UseShellExecute=true});}catch(Exception ex){AutoLogLine(ex.Message);}}
    internal async Task RunAutoBacktestAsync(BatchSettings settings,Func<ProcessStartInfo,CancellationToken,Task>? executor=null)
    {
        if(busy)throw new InvalidOperationException("分析処理の完了後に開始してください。");
        if(autoRunning)throw new InvalidOperationException("連続テストを実行中です。");
        JobFiles.Validate(settings);settings.Save(AutoSettingsPath);
        var target=workspace;autoRunning=true;autoCancellation=new();var token=autoCancellation.Token;
        ShowAutoProgress("連続バックテストを準備しています…");
        AutoStart.IsEnabled=AutoSettingsPanel.IsEnabled=AutoJobsGrid.IsEnabled=false;AutoStop.IsEnabled=AutoCancel.IsEnabled=true;
        AutoLog.Clear();autoRunner=new BatchRunner(executor);
        foreach(var j in settings.Jobs){j.Status="待機";j.SavedReport="";j.Assessment=null;}AutoJobsGrid.Items.Refresh();
        var activeRunner=autoRunner;int progressEpoch=0;
        autoRunner.Log=line=>{
            int epoch=Volatile.Read(ref progressEpoch);
            _=Dispatcher.InvokeAsync(()=>{
                if(!ReferenceEquals(autoRunner,activeRunner))return;
                AutoLogLine(line);
                if(epoch==Volatile.Read(ref progressEpoch))ShowAutoLogProgress(line);
            });
        };
        autoRunner.Status=(j,status)=>{
            Interlocked.Increment(ref progressEpoch);
            Dispatcher.Invoke(()=>{AutoJobsGrid.Items.Refresh();ShowAutoProgress($"{settings.Jobs.IndexOf(j)+1} / {settings.Jobs.Count} 件 · {j.Name} · {status}");});
        };
        autoRunner.ReportSavedAsync=async job=>await Dispatcher.InvokeAsync(()=>AutoSaveScopeReportAsync(job,target,token,settings.IsMt5,settings.UseTds==true||settings.VariableSpreadTds)).Task.Unwrap();
        try{await Task.Run(()=>autoRunner.RunAsync(settings));autoLastOutput=autoRunner.RunFolder;AutoLogLine("連続処理が終了しました。一覧の状態と保存先を確認してください。");}
        finally{await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Background);autoLastOutput=autoRunner?.RunFolder??autoLastOutput;autoRunner=null;autoCancellation.Dispose();autoCancellation=null;autoRunning=false;ShowAutoProgress("連続処理を終了しました。一覧の状態を確認してください。");AutoStart.IsEnabled=AutoSettingsPanel.IsEnabled=AutoJobsGrid.IsEnabled=true;AutoStop.IsEnabled=AutoCancel.IsEnabled=false;if(closeAfterAuto){closeAfterAuto=false;Close();}}
    }
    private async Task AutoSaveScopeReportAsync(TestJob job,Workspace target,CancellationToken token,bool mt5,bool tds)
    {
        job.Status="データ取込・PDF作成中";AutoJobsGrid.Items.Refresh();
        ShowAutoProgress(job.Name+" · データ取込・PDFレポートを作成しています…");
        // Use the run's selected workspace even if the user opens another workspace during MT4 execution.
        while(busy)await Task.Delay(100,token);
        await Task.Run(()=>{var imported=target.Import(job.SavedReport,new ImportOptions(),token);target.SetReportOrigin(imported.Dataset.Id,mt5?"MT5":"MT4",tds);},token);
        if(workspace.Root==target.Root)RefreshDatasets();
        string dir=Path.GetDirectoryName(job.SavedReport)!;
        string temporaryRoot=Path.Combine(Path.GetTempPath(),"ArmadaScopeFX-Report",Guid.NewGuid().ToString("N"));
        var reportWindow=new MainWindow(temporaryRoot);
        using var registration=token.Register(()=>reportWindow.Dispatcher.BeginInvoke(()=>{reportWindow.jobCancellation?.Cancel();reportWindow.CancelMetricEstimates();reportWindow.CancelSlippage();}));
        try
        {
            await Task.Run(()=>{var imported=reportWindow.workspace.Import(job.SavedReport,new ImportOptions(),token);reportWindow.workspace.SetReportOrigin(imported.Dataset.Id,mt5?"MT5":"MT4",tds);},token);
            reportWindow.RefreshDatasets();
            var data=reportWindow.workspace.ListDatasets().Single();
            if(data.InitialBalance<=0)throw new InvalidDataException("初期証拠金を取得できないためPDFを作成できません。");
            reportWindow.CapitalBox.Text=Amount.Exact(data.InitialBalance);
            await reportWindow.AnalyzeAsync();token.ThrowIfCancellationRequested();
            if(reportWindow.LastError!=null)throw new InvalidOperationException(reportWindow.LastError);
            await reportWindow.ExportReportAsync(ReportFiles.ScopePdfPath(dir,job.Name),token);
            job.Status=job.Assessment?.Warnings.Count>0?"両保存完了（警告あり）":"両レポート保存完了";AutoJobsGrid.Items.Refresh();
        }
        finally{reportWindow.CancelMetricEstimates();reportWindow.CancelSlippage();await Task.WhenAll(reportWindow.MetricEstimateTask,reportWindow.SlippageTask);reportWindow.Close();
            try{Directory.Delete(temporaryRoot,true);}catch(IOException){AutoLogLine("一時解析データの削除を延期しました: "+temporaryRoot);}catch(UnauthorizedAccessException){AutoLogLine("一時解析データを削除できませんでした: "+temporaryRoot);}
        }
    }
}
