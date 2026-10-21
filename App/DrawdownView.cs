using System.Globalization;
namespace ArmadaAnalyzer.Desktop;

public partial class MainWindow
{
    private AnalysisResult? displayedDrawdowns;
    private DrawdownOptions ReadDrawdownOptions()=>new(Amount.Parse(DdAmountBox.Text),RelativePercent:decimal.Parse(DdRelativeBox.Text,CultureInfo.InvariantCulture));
    private static string RecoveryDuration(decimal? ticks)
    {
        if(ticks is not decimal value)return "—";
        return $"{decimal.Floor(value/TimeSpan.TicksPerDay):N0}日 {decimal.Floor(value/TimeSpan.TicksPerHour)%24:0}時間 {decimal.Floor(value/TimeSpan.TicksPerMinute)%60:0}分";
    }
    private void UpdateDrawdowns(DrawdownOptions options)
    {
        if(ReferenceEquals(displayedDrawdowns,analysis) && DrawdownGrid.Rows!=null)return;
        displayedDrawdowns=analysis;
        var d=analysis?.Recovery;
        DrawdownRow Row(string name,DrawdownSummary? summary,string threshold,bool percent=false)=>new (
            $"{name}\n{threshold}",threshold,
            summary==null?"—":(-summary.Maximum).ToString("#,0.00######")+(percent?"%":""),
            summary?.Count.ToString("N0")??"—",summary==null?"—":$"{summary.Recovered:N0} / {summary.TimedRecovered:N0}",
            RecoveryDuration(summary?.AverageTicks),RecoveryDuration(summary?.MinimumTicks),RecoveryDuration(summary?.MaximumTicks),
            summary==null?"—":!summary.IsOngoing?"なし":summary.OngoingStart is long t?$"{new DateTime(t):yyyy/MM/dd HH:mm} から\n{RecoveryDuration(summary.OngoingTicks)}":"未回復（起点日時不明）"
        );
        DrawdownGrid.SetRows(new[]{
            Row("金額DD",d?.Money,Amount.Exact(options.AmountThreshold)),
            Row("相対DD",d?.Relative,$"最高残高の {options.RelativePercent:0.########}%",true),
            Row("初資比DD 5%",d?.Initial5,"初期資本の 5%",true),
            Row("初資比DD 10%",d?.Initial10,"初期資本の 10%",true),
            Row("初資比DD 15%",d?.Initial15,"初期資本の 15%",true)});
    }
}
