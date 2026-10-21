using System.Globalization;
using System.Windows;
using System.Windows.Media;
namespace ArmadaAnalyzer.Desktop;

public sealed class HoldingScatterChart : FrameworkElement
{
    private readonly ChartPlayback playback;
    private HoldingPoint[] points = [];
    private decimal pipSize;
    public bool IsPips
    {
        get; set;
    }
    public int DrawnPoints
    {
        get; private set;
    }
    public void SetData(HoldingPoint[] value, decimal pip = 0)
    {
        if(value.Length>0)playback.Request();else if(value.Length==0)playback.Clear();
        points = IsPips && pip<=0?value.Where(p=>p.Pips!=null).ToArray():value;
        pipSize = pip;
        InvalidateVisual();
    }
    public HoldingScatterChart()
    {
        playback=new(this);
        ClipToBounds = true;
        SizeChanged += (_, _) => InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(UiTheme.Get("Surface"), null, new Rect(RenderSize));
        DrawnPoints = 0;
        void Text(string text, double x, double y, Brush? brush = null) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Yu Mincho,MS PMincho"), 10, brush ?? UiTheme.Get("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        if (ActualWidth < 150 || ActualHeight < 150)
            return;
        if (points.Length == 0)
        {
            Text(IsPips ? "pips単位を確認できる取引がありません" : "取引時間の結果がここに表示されます", 16, 80);
            return;
        }
        double Value(HoldingPoint p) => IsPips ? (double)(pipSize>0?(decimal)p.PriceMove / Amount.Scale / pipSize:p.Pips!.Value) : (double)p.Profit / Amount.Scale;
        var plot = new Rect(70, 30, ActualWidth - 90, ActualHeight - 82);
        double xmax = Math.Max(1.0 / 60, points.Max(p => (double)p.Duration / TimeSpan.TicksPerHour));
        double min = Math.Min(0, points.Min(Value)), max = Math.Max(0, points.Max(Value));
        if (min == max)
            max = min + 1;
        double X(HoldingPoint p) => plot.Left + (double)p.Duration / TimeSpan.TicksPerHour / xmax * plot.Width;
        double Y(double v) => plot.Top + (max - v) / (max - min) * plot.Height;
        var grid = new Pen(UiTheme.Get("ChartGrid"), 1);
        for (int i = 0; i <= 5; i++)
        {
            double y = plot.Top + plot.Height * i / 5;
            dc.DrawLine(grid, new(plot.Left, y), new(plot.Right, y));
            Text((max - (max - min) * i / 5).ToString("#,0.##", CultureInfo.InvariantCulture), 0, y - 7);
            double x = plot.Left + plot.Width * i / 5;
            dc.DrawLine(grid, new(x, plot.Top), new(x, plot.Bottom));
            double hours = xmax * i / 5;
            Text(hours < 1 ? $"{hours * 60:F0}分" : $"{hours:F1}時間", Math.Max(0, x - 18), plot.Bottom + 12);
        }
        dc.DrawLine(new Pen(UiTheme.Get("Muted"), 1), new(plot.Left, Y(0)), new(plot.Right, Y(0)));
        var gain = new StreamGeometry();
        var loss = new StreamGeometry();
        using (var positive = gain.Open())
        using (var negative = loss.Open())
            foreach (var point in points)
            {
                double value = Value(point), x = X(point), y = Y(value);
                var path = value >= 0 ? positive : negative;
                path.BeginFigure(new(x, y - 2), true, true);
                path.LineTo(new(x + 2, y), false, false);
                path.LineTo(new(x, y + 2), false, false);
                path.LineTo(new(x - 2, y), false, false);
            }
        gain.Freeze();
        loss.Freeze();
        var layer=new DrawingGroup();
        using(var marks=layer.Open())
        {
            marks.DrawGeometry(new SolidColorBrush(Color.FromArgb(145, 93, 82, 239)), null, gain);
            marks.DrawGeometry(new SolidColorBrush(Color.FromArgb(145, 244, 67, 67)), null, loss);
        }
        playback.Attach(dc,layer,plot,Y(0));
        DrawnPoints = points.Length;
        Text("● 利益", plot.Left + plot.Width / 2 - 45, 4, new SolidColorBrush(Color.FromRgb(93, 82, 239)));
        Text("● 損失", plot.Left + plot.Width / 2 + 15, 4, Brushes.IndianRed);
    }
}
