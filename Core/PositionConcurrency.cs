namespace ArmadaAnalyzer;

public readonly record struct PositionConcurrency(int Maximum, int LongMaximum, int ShortMaximum);

// Identity is scoped to a dataset. Partial closes share one position until its
// last selected close. Weights change money, never the number of positions.
internal readonly record struct PositionInterval(long Dataset, long Ticket, long Open, long Close, int Side);

internal static class ConcurrentPositions
{
    private readonly record struct Event(long Time, int Phase, int Side);
    public static PositionConcurrency Calculate(PositionInterval[] positions, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Array.Sort(positions, 0, count, Comparer<PositionInterval>.Create((a,b)=>{
            int c=a.Dataset.CompareTo(b.Dataset);if(c!=0)return c;
            c=a.Ticket.CompareTo(b.Ticket);if(c!=0)return c;
            c=a.Open.CompareTo(b.Open);return c!=0?c:a.Side.CompareTo(b.Side);
        }));
        token.ThrowIfCancellationRequested();
        var events=new Event[checked(count*2)];int used=0;
        for(int i=0;i<count;)
        {
            if((i&8191)==0)token.ThrowIfCancellationRequested();
            var p=positions[i++];long close=p.Close;
            while(i<count && positions[i].Dataset==p.Dataset && positions[i].Ticket==p.Ticket &&
                  positions[i].Open==p.Open && positions[i].Side==p.Side)
                close=Math.Max(close,positions[i++].Close);
            // Close before open on timestamp ties. Zero-duration trades still
            // count at their timestamp, and close after that timestamp's opens.
            events[used++]=new(p.Open,1,p.Side);
            events[used++]=new(close,close==p.Open?2:0,p.Side);
        }
        Array.Sort(events,0,used,Comparer<Event>.Create((a,b)=>{
            int c=a.Time.CompareTo(b.Time);return c!=0?c:a.Phase.CompareTo(b.Phase);
        }));
        int all=0,longs=0,shorts=0,max=0,longMax=0,shortMax=0;
        for(int i=0;i<used;i++)
        {
            if((i&8191)==0)token.ThrowIfCancellationRequested();
            var e=events[i];int delta=e.Phase==1?1:-1;
            all+=delta;if(e.Side==1)longs+=delta;else if(e.Side==2)shorts+=delta;
            max=Math.Max(max,all);longMax=Math.Max(longMax,longs);shortMax=Math.Max(shortMax,shorts);
        }
        return new(max,longMax,shortMax);
    }
}
