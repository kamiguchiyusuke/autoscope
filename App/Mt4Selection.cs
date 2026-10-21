using MT4BatchReporter;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace ArmadaAnalyzer.Desktop;
public partial class MainWindow
{
    private readonly ObservableCollection<Mt4Installation> mt4Choices=[];
    private bool selectingMt4;
    private void RefreshMt4Choices()
    {
        string current=AutoTerminal.Text,data=AutoData.Text;bool portable=AutoPortable.IsChecked==true;
        selectingMt4=true;
        try
        {
            mt4Choices.Clear();
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"MetaQuotes","Terminal");
            foreach(var item in Mt4Installations.Discover(root,[Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)]))mt4Choices.Add(item);
            AutoMt4Picker.ItemsSource=mt4Choices;
        }
        finally{selectingMt4=false;}
        if(!string.IsNullOrWhiteSpace(current))SelectMt4(current,data,portable);
    }
    private void SelectMt4(string exe,string data,bool portable)
    {
        selectingMt4=true;
        try
        {
            if(string.IsNullOrWhiteSpace(exe)){AutoMt4Picker.SelectedItem=null;AutoTerminal.Text=AutoData.Text="";return;}
            var item=mt4Choices.FirstOrDefault(x=>Mt4Installations.SamePath(x.TerminalFile,exe)&&string.Equals(x.DataFolder,data,StringComparison.OrdinalIgnoreCase));
            if(item==null){item=Mt4Installations.Create(exe,data,portable);mt4Choices.Add(item);}
            AutoMt4Picker.SelectedItem=item;AutoTerminal.Text=exe;AutoData.Text=data;AutoPortable.IsChecked=portable;AutoMt4Picker.ToolTip=item.Detail;
        }
        finally{selectingMt4=false;}
        RefreshAutoEnvironment();
    }
    private void AutoMt4_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(selectingMt4 || AutoMt4Picker.SelectedItem is not Mt4Installation item)return;
        AutoTerminal.Text=item.TerminalFile;AutoData.Text=item.DataFolder;AutoPortable.IsChecked=item.Portable;AutoMt4Picker.ToolTip=item.Detail;
        RefreshAutoEnvironment();
        AutoLogLine(item.Name+"を選択しました。EA選択で選択したMTのExpertsフォルダーを開きます。");
    }
    private void AutoDetect_Click(object sender,RoutedEventArgs e)=>RefreshMt4Choices();
}
