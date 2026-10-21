using System.Globalization;
using System.Windows;
using System.Windows.Controls;
namespace ArmadaAnalyzer.Desktop;

public partial class MainWindow
{
    private bool synchronizingSide;
    private void InitializeConditions()
    {
        ConditionsDataGrid.ItemsSource = datasets;
        Populate(MonthsChoices, 12, 1);
        Populate(DaysChoices, 31, 1);
        Populate(WeekdaysChoices, 7, 0, new[] { "日", "月", "火", "水", "木", "金", "土" });
        Populate(HoursChoices, 24, 0);
        Populate(MinutesChoices, 60, 0);
    }
    private static void Populate(Panel panel, int count, int first, string[]? labels = null)
    {
        for (int i = 0; i < count; i++)
            panel.Children.Add(new CalendarCheckBox { Content = labels?[i] ?? (i + first).ToString(CultureInfo.InvariantCulture), IsChecked = true, Tag = i, Margin = new Thickness(8, 10, 8, 10), FontSize = 14 });
    }
    private Panel CalendarPanel(string key) => key switch { "months" => MonthsChoices, "days" => DaysChoices, "weekdays" => WeekdaysChoices, "hours" => HoursChoices, _ => MinutesChoices };
    private static long Mask(Panel panel)
    {
        long bits = 0;
        foreach (var box in panel.Children.OfType<CheckBox>())
            if (box.IsChecked == true)
                bits |= 1L << (int)box.Tag;
        return bits;
    }
    private EntryCalendar? ReadEntryCalendar()
    {
        var selection = new EntryCalendar((int)Mask(MonthsChoices), (int)Mask(DaysChoices), (int)Mask(WeekdaysChoices), (int)Mask(HoursChoices), Mask(MinutesChoices));
        return selection == new EntryCalendar() ? null : selection;
    }
    private void CalendarSelect_Click(object sender, RoutedEventArgs e)
    {
        string[] parts = ((Button)sender).Tag.ToString()!.Split(':');
        foreach (var box in CalendarPanel(parts[0]).Children.OfType<CheckBox>())
            box.IsChecked = parts[1] == "all" || (parts[1] == "five" && (int)box.Tag % 5 == 0);
    }
    private async void ResetConditions_Click(object sender, RoutedEventArgs e)
    {
        if(busy || autoRunning){StatusText.Text="実行中の処理が終了してから全リセットしてください。";return;}
        if(datasets.Count>0 && MessageBox.Show(this,"取り込んだ全データを削除して、計算条件を初期化しますか？\n元のHTML・PDFファイルは削除しません。","全リセット",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        await ResetAllDataAsync();
    }
    internal async Task ResetAllDataAsync()
    {
        if(busy || autoRunning)throw new InvalidOperationException("実行中は全リセットできません。");
        await DeleteDatasetsAsync(datasets.Select(d=>d.Data.Id).ToArray());
        if(datasets.Count==0)ResetConditions();
    }
    internal void ResetConditions()
    {
        synchronizingSide = true;
        FromDate.SelectedDate = ToDate.SelectedDate = null;
        SideBox.SelectedIndex = 0;
        CapitalBox.Text = "1000000";
        DdAmountBox.Text="250000";DdRelativeBox.Text="10";
        PortfolioName.Text = "";
        foreach (var key in new[] { "months", "days", "weekdays", "hours", "minutes" })
            foreach (var box in CalendarPanel(key).Children.OfType<CheckBox>())
                box.IsChecked = true;
        foreach (var data in datasets)
            data.Weight = "1";
        foreach (var radio in SideOptions.Children.OfType<RadioButton>())
            radio.IsChecked = radio.Tag?.ToString() == "0";
        synchronizingSide = false;
    }
    private void SyncSideSelection(int side)
    {
        synchronizingSide = true;
        foreach (var radio in SideOptions.Children.OfType<RadioButton>())
            radio.IsChecked = radio.Tag?.ToString() == side.ToString(CultureInfo.InvariantCulture);
        synchronizingSide = false;
    }
    private async void ApplyConditions_Click(object sender, RoutedEventArgs e) => await ApplyConditionsAsync();
    internal async Task ApplyConditionsAsync()
    {
        if (busy)
            return;
        ConditionsDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ConditionsDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
        await AnalyzeAsync();
        if (LastError == null && !lastJobCanceled)
            Tabs.SelectedIndex = 0;
    }
}
