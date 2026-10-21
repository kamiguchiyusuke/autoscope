namespace ArmadaAnalyzer;
public sealed record ChartDrawdown(int Start,int End,int Trough,long Depth,bool Recovered,decimal? InitialPercent,decimal? BalancePercent);

// Top three distinct peak-to-recovery episodes, O(1) extra memory during analysis.
internal sealed class ChartDrawdownTracker(long initial)
{
    private readonly List<ChartDrawdown> top=new(4);
    private long peak,depth;
    private int start=-1,trough;
    public void Observe(long cumulative,int index)
    {
        if(cumulative>=peak)
        {
            if(depth>0)Include(index,true);
            peak=cumulative;start=index;depth=0;
        }
        else
        {
            long fall=checked(peak-cumulative);
            if(fall>depth){depth=fall;trough=index;}
        }
    }
    private void Include(int end,bool recovered)
    {
        if(top.Count==3 && depth<=top[2].Depth)return;
        var value=new ChartDrawdown(start,end,trough,depth,recovered,initial>0?100m*depth/initial:null,
            initial>0?100m*depth/((decimal)initial+peak):null);
        int slot=top.FindIndex(x=>x.Depth<depth);
        if(slot<0)slot=top.Count;
        if(slot<3){top.Insert(slot,value);if(top.Count>3)top.RemoveAt(3);}
    }
    public ChartDrawdown[] Finish(int end){if(depth>0)Include(end,false);return top.ToArray();}
}
