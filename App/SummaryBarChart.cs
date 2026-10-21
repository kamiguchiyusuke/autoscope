using System.Globalization;
using System.Windows;
using System.Windows.Media;
namespace ArmadaAnalyzer.Desktop;

public sealed class SummaryBarChart : FrameworkElement
{
    internal ChartPlayback Playback {get;}
    private DashboardBucket[] buckets = [];
    public int BucketCount => buckets.Length;
    public string Metric { get; set; } = "Profit";
    public void SetData(DashboardBucket[] data)
    {
        if(data.Length>0)Playback.Request();else if(data.Length==0)Playback.Clear();
        buckets = data;
        InvalidateVisual();
    }
    public SummaryBarChart()
    {
        Playback=new(this);
        SizeChanged += (_, _) => InvalidateVisual();
        MouseMove += (_, e) =>
        {
            if (buckets.Length == 0)
                return;
            int i = Math.Clamp((int)((e.GetPosition(this).X - 65) / Math.Max(1, ActualWidth - 85) * buckets.Length), 0, buckets.Length - 1);
            var b = buckets[i];
            ToolTip = $"{b.Label} : {b.Count:N0}取引\n損益 {Amount.Display(b.Profit)} / DD {Amount.Display(b.Drawdown)}";
        };
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(UiTheme.Get("Surface"), null, new Rect(RenderSize));
        if (ActualWidth < 150 || ActualHeight < 100 || buckets.Length == 0)
            return;
        var plot = new Rect(65, 38, ActualWidth - 85, ActualHeight - 76);
        bool count = Metric == "Count";
        double[] Values(DashboardBucket b) => Metric switch
        {
            "Count" => [b.Wins, b.Losses],
            "Average" => [b.Count == 0 ? 0 : (double)b.Profit / b.Count / Amount.Scale],
            "Drawdown" => [-(double)b.Drawdown / Amount.Scale],
            _ => [(double)b.Profit / Amount.Scale, (double)b.GrossProfit / Amount.Scale, (double)b.GrossLoss / Amount.Scale]
        };
        var all = buckets.Select(Values).ToArray();
        double min = Math.Min(0, all.Min(x => x.Min())), max = Math.Max(0, all.Max(x => x.Max()));
        if (max == min)
            max = min + 1;
        double Y(double v) => plot.Top + (max - v) / (max - min) * plot.Height;
        void Text(string text, double x, double y, Brush? brush = null) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Yu Mincho,MS PMincho"), 10, brush ?? UiTheme.Get("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        var blue = UiTheme.Get("Gain");
        Brush[] colors = [blue, new SolidColorBrush(Color.FromRgb(145, 198, 111)), new SolidColorBrush(Color.FromRgb(250, 199, 86))];
        string[] legend = Metric switch
        {
            "Count" => ["勝数", "負数"],
            "Average" => ["平均損益"],
            "Drawdown" => ["最大DD"],
            _ => ["総損益", "利益", "損失"]
        };
        for (int i = 0; i < legend.Length; i++)
        {
            double x = Math.Max(65, (ActualWidth - legend.Length * 78) / 2) + i * 78;
            dc.DrawRectangle(colors[i], null, new Rect(x, 8, 13, 9));
            Text(legend[i], x + 18, 4);
        }
        var pen = new Pen(UiTheme.Get("ChartGrid"), 1);
        for (int tick = 0; tick <= 4; tick++)
        {
            double value = max - (max - min) * tick / 4;
            double y = Y(value);
            dc.DrawLine(pen, new(plot.Left, y), new(plot.Right, y));
            Text(value.ToString(count ? "N0" : "#,0.##", CultureInfo.InvariantCulture), 1, y - 7);
        }
        double stride = plot.Width / buckets.Length;
        var barLayer=new DrawingGroup();
        using(var barDc=barLayer.Open())
        {
        for (int i = 0; i < buckets.Length; i++)
        {
            var values = all[i];
            for (int j = 0; j < values.Length; j++)
            {
                double width = Math.Max(0.5, stride * 0.8 / values.Length - 1), y = Y(values[j]), zero = Y(0);
                barDc.DrawRectangle(colors[j], null, new Rect(plot.Left + i * stride + stride * 0.1 + j * stride * 0.8 / values.Length, Math.Min(y, zero), width, Math.Max(0.3, Math.Abs(zero - y))));
            }
            if (i % Math.Max(1, (int)Math.Ceiling(buckets.Length / 16.0)) == 0)
                Text(buckets[i].Label, plot.Left + i * stride + stride * 0.25, plot.Bottom + 10);
        }
        }
        Playback.Attach(dc,barLayer,plot,Y(0),vertical:true);
    }
}
