using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace ArmadaAnalyzer.Desktop;

internal sealed record DrawdownRow(string Name,string Threshold,string Maximum,string Count,string Recovered,string Average,string Minimum,string Longest,string Ongoing);

/// <summary>Six fixed summary rows: no DataGrid containers, bindings, selection or per-cell templates.</summary>
public sealed class DrawdownTable : Grid
{
    private readonly TextBlock[,] cells=new TextBlock[6,8];
    internal DrawdownRow[]? Rows {get;private set;}
    private bool initialized;
    private ScrollViewer? viewport;
    public DrawdownTable()
    {
        Loaded+=(_,_)=>{
            if(initialized)return;
            DependencyObject? parent=VisualTreeHelper.GetParent(this);
            while(parent!=null && parent is not ScrollViewer)parent=VisualTreeHelper.GetParent(parent);
            viewport=parent as ScrollViewer;
            if(viewport!=null)viewport.ScrollChanged+=ViewportChanged;
            MaterializeVisibleRows();
        };
        Unloaded+=(_,_)=>DetachViewport();
        SizeChanged+=(_,_)=>MaterializeVisibleRows();
        IsVisibleChanged+=(_,_)=>MaterializeVisibleRows();
    }
    private void DetachViewport(){if(viewport!=null)viewport.ScrollChanged-=ViewportChanged;viewport=null;}
    private void ViewportChanged(object sender,ScrollChangedEventArgs e)=>MaterializeVisibleRows();
    private void MaterializeVisibleRows()
    {
        if(initialized || Rows==null || !IsVisible || ActualHeight<=0)return;
        if(viewport!=null)
        {
            var bounds=TransformToAncestor(viewport).TransformBounds(new Rect(RenderSize));
            if(!bounds.IntersectsWith(new Rect(0,0,viewport.ActualWidth,viewport.ActualHeight)))return;
        }
        InitializeTable();DetachViewport();RefreshCells();
    }
    private void InitializeTable()
    {
        if(initialized)return;initialized=true;
        double[] widths=[1.35,.85,.6,1.05,1.2,1.2,1.2,1.9];
        foreach(double width in widths)ColumnDefinitions.Add(new(){Width=new GridLength(width,GridUnitType.Star)});
        string[] titles=["指標 / 適用した基準","最大DD","DD回数","回復済 / 期間判明","平均回復期間","最短回復期間","最長回復期間","基準以上の未回復区間"];
        for(int r=0;r<7;r++)
        {
            RowDefinitions.Add(new(){Height=new GridLength(r==0?40:64)});
            var background=new Border{BorderThickness=new Thickness(0,0,0,1)};
            background.SetResourceReference(Border.BorderBrushProperty,"Outline");
            background.SetResourceReference(Border.BackgroundProperty,r%2==0?"Raised":"Surface");
            SetRow(background,r);SetColumnSpan(background,8);Children.Add(background);
            for(int c=0;c<8;c++)
            {
                var text=new TextBlock{FontSize=11,Margin=new Thickness(6,3,6,3),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
                text.SetResourceReference(TextBlock.ForegroundProperty,r==0?"Muted":"Ink");
                SetRow(text,r);SetColumn(text,c);Children.Add(text);
                if(r==0)text.Text=titles[c];else cells[r-1,c]=text;
            }
        }
    }
    internal void SetRows(DrawdownRow[]? rows)
    {
        if(ReferenceEquals(Rows,rows))return;
        Rows=rows;
        if(!initialized){MaterializeVisibleRows();return;}
        RefreshCells();
    }
    private void RefreshCells()
    {
        var rows=Rows;
        for(int r=0;r<6;r++)
        {
            var row=rows is {Length:>0} && r<rows.Length?rows[r]:null;
            string[] values=row==null?["—","—","—","—","—","—","—","—"]:[row.Name,row.Maximum,row.Count,row.Recovered,row.Average,row.Minimum,row.Longest,row.Ongoing];
            for(int c=0;c<8;c++){cells[r,c].Text=values[c];cells[r,c].ToolTip=values[c];}
        }
    }
}
