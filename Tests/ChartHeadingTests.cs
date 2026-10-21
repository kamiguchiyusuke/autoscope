using ArmadaAnalyzer;
static class ChartHeadingTests
{
    public static void Run()
    {
        Program.Test("chart_headings_left_cluster",()=>Check([100,105,110],[85,85,85],100,650));
        Program.Test("chart_headings_right_cluster",()=>Check([650,660,665],[85,85,85],100,650));
        Program.Test("chart_headings_preserve_clear_positions",()=>{
            var boxes=Check([120,300,590],[85,100,85],100,650);
            Program.Assert(boxes[0].Left==120 && boxes[1].Left==300 && boxes[2].Left==590,"clear headings stay aligned");
        });
        Program.Test("chart_headings_narrow_width",()=>{
            var boxes=Check([0,1,2],[80,120,80],0,50);
            Program.Assert(boxes.All(b=>b.Width>0) && boxes[1].Width>boxes[0].Width,"constrained widths preserve proportions");
        });
        Program.Test("chart_headings_empty",()=>Program.Assert(ChartHeadingLayout.Arrange([],[],0,50).Length==0,"empty chart"));
    }
    static ChartHeadingBox[] Check(double[] preferred,double[] widths,double left,double width)
    {
        var boxes=ChartHeadingLayout.Arrange(preferred,widths,left,width);
        Program.Assert(boxes.Length==widths.Length,"all headings retained");
        for(int i=0;i<boxes.Length;i++){
            Program.Assert(boxes[i].Left>=left-1e-8 && boxes[i].Left+boxes[i].Width<=left+width+1e-8,"inside plot");
            if(i>0)Program.Assert(boxes[i].Left>=boxes[i-1].Left+boxes[i-1].Width-1e-8,"headings do not overlap");
        }
        return boxes;
    }
}
