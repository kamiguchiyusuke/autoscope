using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ArmadaAnalyzer.Desktop;

// Retained geometry is built once by the chart. Frames mutate only its clip and
// the tiny tracer drawing; no chart resampling, statistics, layout or full repaint.
internal sealed class ChartPlayback
{
    private static readonly List<WeakReference<ChartPlayback>> instances=[];
    private static bool enabled=true;
    internal static void Configure(bool value)
    {
        enabled=value;
        for(int i=instances.Count-1;i>=0;i--)
            if(instances[i].TryGetTarget(out var item)){if(!value)item.Stop();}
            else instances.RemoveAt(i);
    }
    private readonly FrameworkElement owner;
    private System.Windows.Controls.ScrollContentPresenter? viewport;
    private bool InViewport()=>viewport==null || owner.TransformToAncestor(viewport).TransformBounds(new Rect(owner.RenderSize)).IntersectsWith(new Rect(viewport.RenderSize));
    private readonly Stopwatch clock=new();
    private DrawingGroup? layer,temporary,annotations;
    private readonly DrawingGroup tracer=new();
    private RectangleGeometry? clip;
    private Rect plot;
    private double baseline;
    private bool grow,pending;
    private Func<double,Point>? pointAt;
    private Func<double,string>? valueAt;
    private TimeSpan previousFrame;
    private int slowFrames;
    internal bool Running {get;private set;}
    internal double Progress {get;private set;}=1;
    internal bool Lightweight {get;private set;}
    internal List<double> FrameIntervals {get;}=[];
    internal List<double> FrameCosts {get;}=[];
    internal ChartPlayback(FrameworkElement owner)
    {
        this.owner=owner;instances.Add(new(this));
        owner.Unloaded+=(_,_)=>Stop();
        owner.IsVisibleChanged+=(_,_)=>{if(!owner.IsVisible)Stop();else {Request();owner.InvalidateVisual();}};
        owner.Loaded+=(_,_)=>{
            viewport=null;
            for(DependencyObject? parent=VisualTreeHelper.GetParent(owner);parent!=null;parent=VisualTreeHelper.GetParent(parent))
                if(parent is System.Windows.Controls.ScrollContentPresenter content){viewport=content;break;}
            if(owner.IsVisible){Request();owner.InvalidateVisual();}
        };
        owner.SizeChanged+=(_,_)=>Stop();
        owner.PreviewMouseDown+=(_,_)=>Stop();
        owner.PreviewMouseWheel+=(_,_)=>Stop();
    }
    internal void Clear(){Stop();pending=false;layer=null;temporary=null;annotations=null;clip=null;pointAt=null;valueAt=null;}
    internal bool HasRequest=>pending;
    internal void Request(){Stop();pending=true;}
    internal void Attach(DrawingContext dc,DrawingGroup data,Rect bounds,double zero,bool vertical=false,
        Func<double,Point>? point=null,Func<double,string>? value=null,DrawingGroup? transientArea=null,DrawingGroup? completionAnnotations=null)
    {
        bool start=pending;Stop();pending=start;
        layer=data;plot=bounds;baseline=Math.Clamp(zero,plot.Top,plot.Bottom);grow=vertical;
        pointAt=point;valueAt=value;temporary=transientArea;annotations=completionAnnotations;
        clip=new RectangleGeometry(plot);layer.ClipGeometry=clip;
        if(temporary!=null){temporary.ClipGeometry=clip;temporary.Opacity=0;dc.DrawDrawing(temporary);}
        dc.DrawDrawing(layer);dc.DrawDrawing(tracer);
        if(pending && owner.IsVisible)Replay();
    }
    internal void Replay()
    {
        Stop();pending=false;
        if(!enabled || !owner.IsVisible || !InViewport() || layer==null || clip==null || plot.Width<=0)return;
        Progress=0;Running=true;Lightweight=(RenderCapability.Tier>>16)==0;
        FrameIntervals.Clear();FrameCosts.Clear();slowFrames=0;previousFrame=TimeSpan.Zero;
        clock.Restart();Frame(0);CompositionTarget.Rendering+=OnFrame;
    }
    internal void Stop()
    {
        if(Running)CompositionTarget.Rendering-=OnFrame;
        Running=false;clock.Stop();Progress=1;
        if(clip!=null)clip.Rect=plot;
        if(temporary!=null)temporary.Opacity=0;
        if(annotations!=null)annotations.Opacity=1;
        using(var dc=tracer.Open()){}
    }
    private void OnFrame(object? sender,EventArgs args)
    {
        if(!owner.IsVisible || !enabled || !InViewport()){Stop();return;}
        var now=((RenderingEventArgs)args).RenderingTime;
        if(now==previousFrame)return;
        if(previousFrame!=TimeSpan.Zero)
        {
            double elapsed=(now-previousFrame).TotalMilliseconds;
            if(FrameIntervals.Count<400)FrameIntervals.Add(elapsed);
            if(elapsed>25 && ++slowFrames>=6)Lightweight=true;
        }
        previousFrame=now;
        double seconds=clock.Elapsed.TotalSeconds;
        if(seconds>=.75){Stop();return;}
        long started=Stopwatch.GetTimestamp();
        Frame(seconds);
        if(FrameCosts.Count<400)FrameCosts.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
    private void Frame(double seconds)
    {
        if(annotations!=null)annotations.Opacity=0;
        double t=Math.Clamp((seconds-.05)/.62,0,1);
        Progress=1-Math.Pow(1-t,1.5);
        clip!.Rect=grow
            ?new Rect(plot.Left,baseline+(plot.Top-baseline)*Progress,plot.Width,plot.Height*Progress)
            :new Rect(plot.Left,plot.Top,plot.Width*Progress,plot.Height);
        if(temporary!=null)temporary.Opacity=Lightweight?0:Math.Max(0,1-Math.Max(0,(seconds-.67)/.08));
        using var dc=tracer.Open();
        dc.DrawLine(new Pen(UiTheme.Get("Muted"),1),new(plot.Left,baseline),new(plot.Left+plot.Width*Math.Clamp(seconds/.05,0,1),baseline));
        if(seconds<.05 || grow || pointAt==null)return;
        Point p=pointAt(Progress);p.Y=Math.Clamp(p.Y,plot.Top,plot.Bottom);
        Brush color=UiTheme.Get(p.Y<=baseline?"Gain":"Loss");
        double pulse=seconds>.67?1+Math.Sin((seconds-.67)/.08*Math.PI)*.7:1;
        if(!Lightweight)
        {
            dc.PushOpacity(.10);dc.DrawEllipse(color,null,p,13*pulse,13*pulse);dc.Pop();
            dc.PushOpacity(.22);dc.DrawEllipse(color,null,p,7*pulse,7*pulse);dc.Pop();
            Point tail=pointAt(Math.Max(0,Progress-.006));
            dc.PushOpacity(.45);dc.DrawLine(new Pen(color,3),tail,p);dc.Pop();
        }
        dc.DrawEllipse(color,new Pen(UiTheme.Get("Surface"),1),p,3*pulse,3*pulse);
        if(valueAt!=null)
        {
            var text=new FormattedText(valueAt(Progress),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,
                new Typeface("Yu Mincho,MS PMincho"),14,color,VisualTreeHelper.GetDpi(owner).PixelsPerDip);
            double x=Math.Max(plot.Left,plot.Right-text.Width-8);
            dc.DrawRectangle(UiTheme.Get("Surface"),null,new Rect(x-3,plot.Top+5,text.Width+6,text.Height+4));
            dc.DrawText(text,new(x,plot.Top+7));
        }
    }
}
