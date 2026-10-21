using MT4BatchReporter;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
namespace ArmadaAnalyzer.Desktop;
public partial class MainWindow
{
    public ObservableCollection<string> AutoSymbols { get; }=[];
    public string[] AutoPeriods => JobFiles.Periods;
    public string[] AutoCurrencies => ["JPY","USD","EUR","GBP","AUD","CHF","CAD"];
    public ObservableCollection<string> AutoModels {get;}=["全ティック","コントロールポイント","始値のみ"];
    private bool AutoIsMt5=>System.IO.Path.GetFileName(AutoTerminal.Text).Equals("terminal64.exe",StringComparison.OrdinalIgnoreCase);
    public static readonly DependencyProperty TdsModelLabelProperty=DependencyProperty.Register(nameof(TdsModelLabel),typeof(string),typeof(MainWindow),new PropertyMetadata("全ティック"));
    public string TdsModelLabel {get=>(string)GetValue(TdsModelLabelProperty);set=>SetValue(TdsModelLabelProperty,value);}
    public static readonly DependencyProperty UseMarketSpreadProperty=DependencyProperty.Register(nameof(UseMarketSpread),typeof(bool),typeof(MainWindow),new PropertyMetadata(false));
    public bool UseMarketSpread {get=>(bool)GetValue(UseMarketSpreadProperty);set=>SetValue(UseMarketSpreadProperty,value);}
    public static readonly DependencyProperty UseTdsEnabledProperty=DependencyProperty.Register(nameof(UseTdsEnabled),typeof(bool),typeof(MainWindow),new PropertyMetadata(false));
    public bool UseTdsEnabled {get=>(bool)GetValue(UseTdsEnabledProperty);set=>SetValue(UseTdsEnabledProperty,value);}
    private bool tdsInstalled;
    private void RefreshAutoEnvironment()
    {
        TdsModelLabel=AutoIsMt5?"リアルティック":"全ティック";
        var savedModels=autoJobs.Select(j=>(j,j.Model)).ToArray();
        AutoModels.Clear();foreach(var label in AutoIsMt5?new[]{"全ティック","1分足OHLC","始値のみ","数学計算（対象外）","リアルティック"}:new[]{"全ティック","コントロールポイント","始値のみ"})AutoModels.Add(label);
        foreach(var (job,model) in savedModels)job.Model=model<AutoModels.Count?model:0;
        AutoSymbols.Clear();
        try{if(AutoIsMt5){foreach(var symbol in Mt5Support.Symbols(AutoData.Text))AutoSymbols.Add(symbol);}else foreach(var s in Mt4MouseSettings.Symbols(AutoData.Text))AutoSymbols.Add(s.Name);}catch(Exception ex){AutoLogLine("MT4の通貨一覧を確認できません："+ex.Message);}
        tdsInstalled=AutoIsMt5?Mt5Tds.Available:Mt4MouseSettings.TdsInstalled();AutoUseTds.Visibility=tdsInstalled?Visibility.Visible:Visibility.Collapsed;
        if(!tdsInstalled)AutoUseTds.IsChecked=false;
        UseTdsEnabled=tdsInstalled && AutoUseTds.IsChecked==true;UseMarketSpread=AutoIsMt5||UseTdsEnabled;
    }
    private void AutoTds_Changed(object sender,RoutedEventArgs e){UseTdsEnabled=tdsInstalled && AutoUseTds.IsChecked==true;UseMarketSpread=AutoIsMt5||UseTdsEnabled;}
    private TestJob NewAutoJob(string name)=>new(){Name=name,Symbol=AutoSymbols.Contains("USDJPY")?"USDJPY":AutoSymbols.FirstOrDefault()??"",Deposit=1000000,Currency="JPY"};
    private void AutoDate_Click(object sender,System.Windows.Input.MouseButtonEventArgs e)
    {
        if(sender is DatePicker picker && !picker.IsDropDownOpen){picker.IsDropDownOpen=true;e.Handled=true;}
    }
    private void AutoNumber_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not Button{DataContext:TestJob j} b)return;
        var args=b.Tag.ToString()!.Split(':');int step=int.Parse(args[1]);
        if(args[0]=="Deposit")j.Deposit=Math.Clamp(j.Deposit+step,1,1000000000000);
        else j.Spread=Math.Clamp(j.Spread+step,0,100000);
        AutoJobsGrid.Items.Refresh();
    }
}
