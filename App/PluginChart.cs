using ArmadaAnalyzer.Contracts;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
namespace ArmadaAnalyzer.Desktop;

public sealed class PluginChart : FrameworkElement
{
    private readonly ChartPlayback playback;
    public PluginChart(){playback=new(this);}
    PlotSeries? series;
    public void SetSeries(PlotSeries? value)
    {
        if(value!=null)playback.Request();else if(value==null)playback.Clear();
        series = value;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(UiTheme.Get("Surface"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (series == null || series.Points.Count < 2 || ActualWidth < 100 || ActualHeight < 100)
            return;
        var p = series.Points.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToArray();
        if (p.Length < 2)
            return;
        double minx = p.Min(v => v.X), maxx = p.Max(v => v.X), miny = p.Min(v => v.Y), maxy = p.Max(v => v.Y);
        double dx = Math.Max(1e-12, maxx - minx), dy = Math.Max(1e-12, maxy - miny);
        dc.DrawText(new FormattedText(series.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Yu Mincho,MS PMincho"), 12, UiTheme.Get("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(10, 8));
        var g = new StreamGeometry();
        using (var path = g.Open())
        {
            Point Map(PlotPoint v) => new(20 + (v.X - minx) / dx * (ActualWidth - 40), 40 + (maxy - v.Y) / dy * (ActualHeight - 65));
            path.BeginFigure(Map(p[0]), false, false);
            foreach (var v in p.Skip(1))
                path.LineTo(Map(v), true, false);
        }
        g.Freeze();
        var layer=new DrawingGroup();
        using(var chart=layer.Open())chart.DrawGeometry(null,new Pen(UiTheme.Get("Gain"),2),g);
        playback.Attach(dc,layer,new Rect(20,40,ActualWidth-40,ActualHeight-65),40+(maxy/dy)*(ActualHeight-65));
    }
}
