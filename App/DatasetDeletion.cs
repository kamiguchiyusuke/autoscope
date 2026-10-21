using System.Windows;
using System.Windows.Controls;
namespace ArmadaAnalyzer.Desktop;
public partial class MainWindow
{
    private async void DeleteDataset_Click(object sender,RoutedEventArgs e)
    {
        if(sender is FrameworkElement {DataContext:DatasetView row})await ConfirmDeleteDatasets([row]);
    }
    private async void DeleteSelectedDatasets_Click(object sender,RoutedEventArgs e)
    {
        ConditionsDataGrid.CommitEdit(DataGridEditingUnit.Cell,true);
        ConditionsDataGrid.CommitEdit(DataGridEditingUnit.Row,true);
        await ConfirmDeleteDatasets(datasets.Where(d=>d.Selected).ToArray());
    }
    private async Task ConfirmDeleteDatasets(DatasetView[] rows)
    {
        if(busy || rows.Length==0)return;
        string names=string.Join("\n",rows.Take(8).Select(d=>d.Name));
        if(MessageBox.Show(this,$"{rows.Length}件の取込データを削除しますか？\n{names}"+(rows.Length>8?"\n…":"")+"\n元のHTMLファイルは削除しません。", "データを削除",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        await DeleteDatasetsAsync(rows.Select(d=>d.Data.Id).ToArray());
    }
    internal async Task DeleteDatasetsAsync(long[] ids)
    {
        if(busy || ids.Length==0)return;
        bool deleted=false;
        await RunJob("取込データを削除中",async(token,_)=>{
            await Task.Run(()=>workspace.DeleteDatasets(ids,token));deleted=true;
            SwitchWorkspace(workspace.Root,resetConditions:false);
            AnalysisDetail.Text="取込データを削除しました";
            return "取込データを削除しました（元ファイルは保持）";
        });
        if(deleted && datasets.Any(d=>d.Selected && decimal.TryParse(d.Weight,out var weight) && weight!=0))await AnalyzeAsync();
    }
}
