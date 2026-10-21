namespace ArmadaAnalyzer;

public readonly record struct ChartHeadingBox(double Left,double Width);

public static class ChartHeadingLayout
{
    // Inputs follow chronological band order. Preserve preferred positions unless
    // an adjacent heading or a plot edge requires displacement.
    public static ChartHeadingBox[] Arrange(double[] preferred,double[] widths,double left,double width,double gap=3)
    {
        if(preferred.Length!=widths.Length)throw new ArgumentException("Heading counts differ.");
        if(!double.IsFinite(left)||!double.IsFinite(width)||width<=0||!double.IsFinite(gap)||gap<0)
            throw new ArgumentOutOfRangeException(nameof(width));
        int count=widths.Length;
        if(count==0)return [];
        if(widths.Any(w=>!double.IsFinite(w)||w<=0)||preferred.Any(p=>!double.IsFinite(p)))
            throw new ArgumentOutOfRangeException(nameof(widths));
        // Only constrain text when even tightly packed natural widths cannot fit.
        double spacing=Math.Min(gap,width/(count*2.0));
        double scale=Math.Min(1,(width-spacing*(count-1))/widths.Sum());
        var boxes=new ChartHeadingBox[count];
        double next=left;
        for(int i=0;i<count;i++){
            double w=widths[i]*scale;
            double x=Math.Max(next,Math.Clamp(preferred[i],left,left+width-w));
            boxes[i]=new(x,w);next=x+w+spacing;
        }
        double right=left+width;
        for(int i=count-1;i>=0;i--){
            boxes[i]=boxes[i] with {Left=Math.Min(boxes[i].Left,right-boxes[i].Width)};
            right=boxes[i].Left-spacing;
        }
        return boxes;
    }
}
