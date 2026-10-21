using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ArmadaAnalyzer.Desktop;

public sealed class EquityChart : FrameworkElement
{
    internal double SampleMilliseconds, AxisMilliseconds, GeometryMilliseconds, AnnotationMilliseconds;
    internal ChartPlayback Playback {get;}
    internal int RenderPasses {get;private set;}
    internal long ReplayValue(double progress)=>values.Length==0?0:checked((Underwater?-values[IndexAt(progress)]:values[IndexAt(progress)])+offset);
    internal void Replay(){if(values.Length>0)Playback.Replay();}
    private long[] values = [];
    private ChartRangeIndex? rangeIndex;
    private ChartPoint[] sampled = [];
    private int sampledFirst = -1, sampledVisible = -1, sampledWidth = -1;
    private SeriesPoint[]? dates;
    private int first, visible;
    private long offset;
    private Point? dragOrigin;
    private int dragFirst;
    private ChartDrawdown[] bands=[];
    internal List<Rect> AnnotationBounds {get;}=[];
    public int DrawnBands { get; private set; }
    public int YearGridLines { get; private set; }
    internal void SetDrawdownBands(ChartDrawdown[] data){bands=data;InvalidateVisual();}
    private Brush line = new SolidColorBrush(Color.FromRgb(8, 126, 139));
    public int SamplingRuns
    {
        get; private set;
    }
    public bool Underwater
    {
        get; set;
    }
    public event Action<int, int>? ViewChanged;
    public (int First, int Count) VisibleRange => (first, visible);
    public void SetView(int start, int count)
    {
        Playback.Stop();
        first = start;
        visible = count;
        InvalidateVisual();
    }
    public bool PercentAxis
    {
        get; set;
    }
    public double LastRenderMilliseconds
    {
        get; private set;
    }
    public int DrawnPoints
    {
        get; private set;
    }

    public EquityChart()
    {
        Playback=new(this);
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
        SizeChanged += (_, _) => InvalidateVisual();
        MouseWheel += (_, e) =>
        {
            if (values.Length < 2)
                return;
            double anchor = Math.Clamp((e.GetPosition(this).X - 90) / Math.Max(1, ActualWidth - 110), 0, 1);
            int next = Math.Clamp((int)(visible * (e.Delta > 0 ? 0.75 : 1.3334)), Math.Min(10, values.Length), values.Length);
            first = Math.Clamp(first + (int)((visible - next) * anchor), 0, values.Length - next);
            visible = next;
            ViewChanged?.Invoke(first, visible);
            InvalidateVisual();
            e.Handled = true;
        };
        MouseLeftButtonDown += (_, e) => { dragOrigin = e.GetPosition(this); dragFirst = first; CaptureMouse(); };
        MouseLeftButtonUp += (_, _) => { dragOrigin = null; ReleaseMouseCapture(); };
        MouseMove += (_, e) =>
        {
            Point position = e.GetPosition(this);
            if (dragOrigin is Point origin && values.Length > 0)
            {
                first = Math.Clamp(dragFirst - (int)((position.X - origin.X) * visible / Math.Max(1, ActualWidth - 110)), 0, values.Length - visible);
                ViewChanged?.Invoke(first, visible);
                InvalidateVisual();
            }
            if (values.Length > 0)
            {
                int index = IndexAt(Math.Clamp((position.X - 90) / Math.Max(1, ActualWidth - 110), 0, 1));
                string label = dates != null ? new DateTime(dates[index].Ticks).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : PercentAxis ? $"{100.0 * index / Math.Max(1, values.Length - 1):F2}%" : $"#{index + 1:N0}";
                ToolTip = label + "  " + Amount.Display(Underwater ? checked(-values[index]) : checked(values[index] + offset));
            }
        };
    }

    internal (long First, long Last)? TimeExtent => dates is { Length: > 0 } ? (dates[first].Ticks, dates[first + visible - 1].Ticks) : null;
    private int IndexAt(double fraction)
    {
        if (dates == null || visible < 2)
            return Math.Clamp(first + (int)(fraction * Math.Max(0, visible - 1)), 0, values.Length - 1);
        long target = dates[first].Ticks + (long)((dates[first + visible - 1].Ticks - dates[first].Ticks) * fraction);
        int lo = first, hi = first + visible;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (dates[mid].Ticks <= target)
                lo = mid + 1;
            else
                hi = mid;
        }
        return Math.Clamp(lo - 1, first, first + visible - 1);
    }
    public void SetData(long[] data, SeriesPoint[]? context = null, long initial = 0, bool drawdown = false)
    {
        bool changed=!ReferenceEquals(values,data)||!ReferenceEquals(dates,context)||offset!=initial;
        if (changed)
        {
            rangeIndex=context!=null && data.Length>=50000?new ChartRangeIndex(data):null;
            sampled = [];
            sampledFirst = -1;
        }
        values = data;
        bands=[];
        dates = context;
        offset = initial;
        line = new SolidColorBrush(drawdown ? Color.FromRgb(244, 67, 54) : Color.FromRgb(25, 45, 225));
        ResetView();
        if(data.Length>0)Playback.Request();
        else if(data.Length==0)Playback.Clear();
    }

    public void ResetView()
    {
        Playback.Stop();
        first = 0;
        visible = values.Length;
        InvalidateVisual();
    }

    private DrawingGroup? retainedChart;
    private object? retainedKey;
    protected override void OnRender(DrawingContext drawing)
    {
        // Visibility replay reuses the exact retained drawing; no labels/geometry rebuild.
        object key=(values,dates,bands,first,visible,ActualWidth,ActualHeight,offset,Underwater,PercentAxis,UiTheme.IsDark,SystemParameters.HighContrast,VisualTreeHelper.GetDpi(this));
        if(retainedChart==null || !key.Equals(retainedKey))
        {
            retainedChart=new DrawingGroup();
            using(var content=retainedChart.Open())RenderContent(content);
            retainedKey=key;
        }
        else if(Playback.HasRequest)Playback.Replay();
        drawing.DrawDrawing(retainedChart);
    }
    private void RenderContent(DrawingContext drawing)
    {
        RenderPasses++;
        var watch = Stopwatch.StartNew();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawing.DrawRectangle(UiTheme.Get("Surface"), null, bounds);
        DrawnPoints = 0;
        AnnotationBounds.Clear();
        if (ActualWidth < 150 || ActualHeight < 90)
            return;
        bool hasBands=!Underwater && bands.Length>0;
        DrawnBands=YearGridLines=0;
        var plot = new Rect(90, hasBands?32:12, ActualWidth - 110, ActualHeight - (hasBands?72:52));
        DrawingContext? labelTarget=null;
        Brush annotationInk=UiTheme.IsDark?Brushes.White:Brushes.Black;
        void Text(string text, double x, double y, Brush? brush = null, double fontSize = 10) => (labelTarget??drawing).DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Yu Mincho,MS PMincho"), fontSize, brush ?? UiTheme.Get("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        if (visible == 0)
        {
            Text("分析結果の曲線がここに表示されます", 25, ActualHeight / 2);
            return;
        }
        int width = (int)plot.Width;
        if (sampledFirst != first || sampledVisible != visible || sampledWidth != width)
        {
            sampled = dates==null?ChartSampling.Sample(values, first, visible, width):rangeIndex!=null?ChartSampling.SampleTimed(values,dates,first,visible,width,rangeIndex):ChartSampling.SampleTimed(values,dates,first,visible,width);
            sampledFirst = first;
            sampledVisible = visible;
            sampledWidth = width;
            SamplingRuns++;
        }
        SampleMilliseconds=watch.Elapsed.TotalMilliseconds;
        ChartPoint[] sample = sampled;
        DrawnPoints = sample.Length;
        long Transform(long value) => Underwater ? checked(-value) : value;
        long minimum = sample.Min(point => Transform(point.Value)), maximum = sample.Max(point => Transform(point.Value));
        if (Underwater)
            maximum = Math.Max(0, maximum);
        else if(first==0 && visible==values.Length) {minimum=Math.Min(0,minimum);maximum=Math.Max(0,maximum);}
        double range = (double)maximum - minimum;
        if (range == 0)
            range = Math.Max(1, Math.Abs((double)maximum) * 0.01);
        // Reserve equal headroom above/below for the requested alternating annotations.
        double axisMaximum=maximum;
        if(hasBands){axisMaximum+=range*.25;range*=1.5;}
        var gridPen = new Pen(UiTheme.Get("ChartGrid"), 1);
        for (int tick = 0; tick <= 4; tick++)
        {
            double y = plot.Top + plot.Height * tick / 4;
            drawing.DrawLine(gridPen, new(plot.Left, y), new(plot.Right, y));
            decimal label = ((decimal)axisMaximum - (decimal)range * tick / 4 + offset) / Amount.Scale;
            Text(label.ToString("N0", CultureInfo.InvariantCulture), 2, y - 8);
        }
        double X(int index) => plot.Left + plot.Width * (dates != null ? ChartSampling.TimeFraction(dates, first, visible, index) : (double)(index - first) / Math.Max(1, visible - 1));
        double Y(long value) => plot.Top + plot.Height * (axisMaximum - Transform(value)) / range;
        double TimeX(long ticks)=>plot.Left+plot.Width*(ticks-dates![first].Ticks)/(double)Math.Max(1,dates[first+visible-1].Ticks-dates[first].Ticks);
        if(dates is not null && visible>1)
        {
            var leftDate=new DateTime(dates[first].Ticks);var rightDate=new DateTime(dates[first+visible-1].Ticks);
            // Draw every year boundary; only label those that fit without overlap.
            double lastLabel=plot.Left+85;
            for(int year=leftDate.Year+1;year<=rightDate.Year;year++)
            {
                double x=TimeX(new DateTime(year,1,1).Ticks);
                drawing.DrawLine(gridPen,new(x,plot.Top),new(x,plot.Bottom));YearGridLines++;
                if(rightDate.Year-leftDate.Year>=2 && x>=lastLabel && x<plot.Right-85){Text(year.ToString(),x-14,plot.Bottom+12,fontSize:11);lastLabel=x+65;}
            }
            if(rightDate.Year-leftDate.Year<2)
            {
                int slots=Math.Clamp((int)(plot.Width/120),2,12);
                for(int tick=1;tick<slots;tick++)
                {
                    double x=plot.Left+plot.Width*tick/slots;
                    if(x<plot.Left+85 || x>plot.Right-85)continue;
                    long ticks=dates[first].Ticks+(long)((dates[first+visible-1].Ticks-dates[first].Ticks)*(double)tick/slots);
                    Text(new DateTime(ticks).ToString("yyyy/MM/dd"),x-35,plot.Bottom+12,fontSize:11);
                }
            }
        }
        AxisMilliseconds=watch.Elapsed.TotalMilliseconds-SampleMilliseconds;
        var annotations=new DrawingGroup();
        var annotationDc=annotations.Open();
        drawing.DrawDrawing(annotations);
        labelTarget=annotationDc;
        if(hasBands && dates is not null)
        {
            var fill=new SolidColorBrush(Color.FromArgb(UiTheme.IsDark?(byte)45:(byte)28,220,23,35));fill.Freeze();
            int lane=0;
            var headings=new List<(FormattedText Text,double Preferred,double Width)>();
            foreach(var band in bands.OrderBy(b=>b.Start))
            {
                int position=lane++;
                if(band.End<first || Math.Max(0,band.Start)>first+visible-1)continue;
                double left=X(Math.Max(first,Math.Max(0,band.Start))),right=X(Math.Min(first+visible-1,band.End));
                annotationDc.DrawRectangle(fill,null,new Rect(left,plot.Top,Math.Max(1,right-left),plot.Height));DrawnBands++;
                string duration=band.Start<0?"回復期間不明":$"{(dates[band.End].Ticks-dates[band.Start].Ticks)/(double)TimeSpan.TicksPerDay:0.#}日";
                string heading=band.Start<0?(band.Recovered?"回復日数不明":"未回復・開始日不明"):band.Recovered?duration:"未回復・経過 "+duration;
                var headingText=new FormattedText(heading,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Yu Mincho,MS PMincho"),12,annotationInk,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                for(int character=0;character<heading.Length;character++)
                    if(char.IsDigit(heading[character]) || heading[character]=='.')headingText.SetFontSize(13.2,character,1);
                double center=(left+right)/2;
                double headingWidth=Math.Max(1,headingText.Width);
                double headingX=center-(position==0?headingWidth:position==1?headingWidth/2:0);
                headingText.MaxTextWidth=Math.Max(1,headingWidth);
                headingText.TextAlignment=position==0?TextAlignment.Right:position==1?TextAlignment.Center:TextAlignment.Left;
                headings.Add((headingText,headingX,headingWidth));
            }
            var headingBoxes=ChartHeadingLayout.Arrange(headings.Select(h=>h.Preferred).ToArray(),headings.Select(h=>h.Width).ToArray(),plot.Left,plot.Width);
            for(int i=0;i<headings.Count;i++)
            {
                var heading=headings[i];var box=headingBoxes[i];
                // Retain complete text on one baseline even in an unusually narrow plot.
                annotationDc.PushTransform(new TranslateTransform(box.Left,Math.Max(0,plot.Top-20)));
                annotationDc.PushTransform(new ScaleTransform(box.Width/heading.Width,1));
                annotationDc.DrawText(heading.Text,new Point(0,0));
                annotationDc.Pop();annotationDc.Pop();
            }
        }
        labelTarget=null;
        // Project each sampled point once and reuse the exact step vertices for
        // the stroke, transient fill and annotation envelope.
        var projected=new Point[sample.Length];
        for(int i=0;i<sample.Length;i++)projected[i]=new(X(sample[i].Index),Y(sample[i].Value));
        var vertices=new Point[Math.Max(0,sample.Length-1)*(dates!=null?2:1)];int vertex=0;
        for(int i=1;i<sample.Length;i++)
        {
            if(dates!=null)vertices[vertex++]=new(projected[i].X,projected[i-1].Y);
            vertices[vertex++]=projected[i];
        }
        var geometry=new StreamGeometry();
        using(var path=geometry.Open()){path.BeginFigure(projected[0],false,false);path.PolyLineTo(vertices,true,false);}
        geometry.Freeze();
        var area=new StreamGeometry();
        using(var path=area.Open())
        {
            path.BeginFigure(new(projected[0].X,Y(0)),true,true);
            path.LineTo(projected[0],true,false);path.PolyLineTo(vertices,true,false);
            path.LineTo(new(projected[^1].X,Y(0)),true,false);
        }
        area.Freeze();
        var seriesLayer=new DrawingGroup();
        using(var curve=seriesLayer.Open())
        {
            if(Underwater)curve.DrawGeometry(new SolidColorBrush(Color.FromArgb(85,235,90,112)),null,area);
            curve.DrawGeometry(null,new Pen(UiTheme.Get(Underwater?"Loss":"Gain"),Underwater?1.2:1.7),geometry);
        }
        DrawingGroup? gradientLayer=null;
        if(!Underwater)
        {
            gradientLayer=new();var color=((SolidColorBrush)UiTheme.Get("Gain")).Color;
            var gradient=new LinearGradientBrush(Color.FromArgb(55,color.R,color.G,color.B),Color.FromArgb(0,color.R,color.G,color.B),90);gradient.Freeze();
            using(var dc=gradientLayer.Open())dc.DrawGeometry(gradient,null,area);
        }
        GeometryMilliseconds=watch.Elapsed.TotalMilliseconds-SampleMilliseconds-AxisMilliseconds;
        labelTarget=annotationDc;
        if(hasBands)
        {
            // At most three labels: search nearby clear slots, never stack text on text.
            var occupied=new List<Rect> {
                new Rect(plot.Left,plot.Top,120,120),
                new Rect(Math.Max(plot.Left,plot.Right-170),Math.Max(plot.Top,plot.Bottom-120),170,120)
            };
            // Pixel-column envelope of the actual rendered step path, including vertical jumps.
            // Compute once per render; label searches never scan the full trade history.
            int columns=(int)Math.Ceiling(plot.Width)+1;
            var curveTop=Enumerable.Repeat(double.PositiveInfinity,columns).ToArray();
            var curveBottom=Enumerable.Repeat(double.NegativeInfinity,columns).ToArray();
            for(int i=0;i<sample.Length;i++)
            {
                var previous=projected[Math.Max(0,i-1)];var current=projected[i];
                int left=Math.Clamp((int)Math.Floor(previous.X-plot.Left),0,columns-1);
                int right=Math.Clamp((int)Math.Ceiling(projected[i].X-plot.Left),0,columns-1);
                double high=Math.Min(previous.Y,current.Y)-8;
                double low=Math.Max(previous.Y,current.Y)+8;
                for(int column=left;column<=right;column++)
                {curveTop[column]=Math.Min(curveTop[column],high);curveBottom[column]=Math.Max(curveBottom[column],low);}
            }
            int labelPosition=0;
            foreach(var band in bands.OrderBy(b=>b.Start))
            {
                int position=labelPosition++;
                if(band.Trough<first || band.Trough>=first+visible)continue;
                string P(decimal? value)=>value is decimal v?NumberDisplay.TwoDecimal(v)+"%":"算出不可";
                var top=new FormattedText(P(band.InitialPercent),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Yu Mincho,MS PMincho"),12,annotationInk,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                if(band.InitialPercent.HasValue)top.SetFontSize(15.18,0,top.Text.Length-1);
                var bottom=new FormattedText("初比DD",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Yu Mincho,MS PMincho"),12,annotationInk,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var topInk=top.BuildGeometry(new Point()).Bounds; var bottomInk=bottom.BuildGeometry(new Point()).Bounds;
                double labelWidth=Math.Max(top.Width,bottom.Width)+8; double labelHeight=topInk.Height+bottomInk.Height+2;
                double anchorX=X(band.Trough),anchorY=Y(values[band.Trough]);
                double center=(X(Math.Max(first,Math.Max(0,band.Start)))+X(Math.Min(first+visible-1,band.End)))/2;
                double preferredX=Math.Clamp(center-(position==0?labelWidth:position==1?labelWidth/2:0),plot.Left,Math.Max(plot.Left,plot.Right-labelWidth));
                bool above=position!=1;
                Rect chosen=Rect.Empty;double best=double.MaxValue;
                // Search a single safe vertical slot per column instead of a full 2D grid.
                // Include the exact requested anchor before nearby fallback columns.
                for(int slot=-1;slot<=(int)(plot.Width-labelWidth)/12;slot++)
                {
                    double x=slot<0?preferredX:plot.Left+slot*12;
                    int firstColumn=Math.Clamp((int)Math.Floor(x-plot.Left),0,columns-1);
                    int lastColumn=Math.Clamp((int)Math.Ceiling(x+labelWidth-plot.Left),0,columns-1);
                    double high=double.PositiveInfinity,low=double.NegativeInfinity;
                    for(int column=firstColumn;column<=lastColumn;column++)
                    {high=Math.Min(high,curveTop[column]);low=Math.Max(low,curveBottom[column]);}
                    double y=above?Math.Min(plot.Bottom-labelHeight-1,high-labelHeight-1):Math.Max(plot.Top,low);
                    // Move away from the curve if another annotation occupies this slot.
                    for(int attempt=0;attempt<=occupied.Count;attempt++)
                    {
                        var candidate=new Rect(x,y,labelWidth,labelHeight);
                        var collision=occupied.FirstOrDefault(o=>o.IntersectsWith(candidate));
                        if(collision.IsEmpty || collision.Width==0)break;
                        y=above?collision.Top-labelHeight-2:collision.Bottom+2;
                    }
                    if(y<plot.Top || y+labelHeight>plot.Bottom)continue;
                    var clear=new Rect(x,y,labelWidth,labelHeight);
                    if(occupied.Any(o=>o.IntersectsWith(clear)))continue;
                    double distance=Math.Abs(x-preferredX)*4+Math.Abs(y-anchorY)*.1;
                    if(distance<best){best=distance;chosen=clear;}
                }
                if(chosen.IsEmpty)continue;
                occupied.Add(chosen);AnnotationBounds.Add(chosen);
                annotationDc.DrawLine(new Pen(UiTheme.Get("Loss"),.5),new(anchorX,anchorY+(above?-3:3)),new(Math.Clamp(anchorX,chosen.Left,chosen.Right),above?chosen.Bottom:chosen.Top));
                annotationDc.DrawText(top,new Point(chosen.X+(chosen.Width-top.Width)/2,chosen.Y-topInk.Top));
                annotationDc.DrawText(bottom,new Point(chosen.X+(chosen.Width-bottom.Width)/2,chosen.Y+topInk.Height+2-bottomInk.Top));
            }
        }
        AnnotationMilliseconds=watch.Elapsed.TotalMilliseconds-SampleMilliseconds-AxisMilliseconds-GeometryMilliseconds;
        labelTarget=null;annotationDc.Close();
        Playback.Attach(drawing,seriesLayer,plot,Y(0),vertical:Underwater,point:p=>new Point(plot.Left+plot.Width*p,Y(values[IndexAt(p)])),
            value:p=>Amount.Display(ReplayValue(p)),transientArea:gradientLayer,completionAnnotations:annotations);
        string LeftLabel(int index) => dates != null ? new DateTime(dates[index].Ticks).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) : PercentAxis ? $"{100.0 * index / Math.Max(1, values.Length - 1):F0}%" : (index + 1).ToString("N0", CultureInfo.InvariantCulture);
        Text(LeftLabel(first), plot.Left, plot.Bottom + 12,fontSize:11);
        Text(LeftLabel(first + visible - 1), Math.Max(plot.Left, plot.Right - 80), plot.Bottom + 12,fontSize:11);
        watch.Stop();
        LastRenderMilliseconds = watch.Elapsed.TotalMilliseconds;
    }
}
