namespace ArmadaAnalyzer;

public static class ChartSampling
{
    public static double TimeFraction(SeriesPoint[] dates, int first, int count, int index)
    {
        if (count < 1 || first < 0 || first > dates.Length - count || index < first || index >= first + count)
            throw new ArgumentOutOfRangeException(nameof(index));
        long extent = dates[first + count - 1].Ticks - dates[first].Ticks;
        return extent > 0 ? (double)(dates[index].Ticks - dates[first].Ticks) / extent : (double)(index - first) / Math.Max(1, count - 1);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static ChartPoint[] SampleTimed(long[] values,SeriesPoint[] dates,int start,int count,int width)
    {
        if(values.Length!=dates.Length||start<0||count<0||start>values.Length-count)throw new ArgumentOutOfRangeException(nameof(count));
        if(count==0)return [];
        width=Math.Clamp(width,1,16000);
        long firstTick=dates[start].Ticks,span=dates[start+count-1].Ticks-firstTick;
        if(span<=0)return Sample(values,start,count,width);
        var result=new List<ChartPoint>(Math.Min(count,4*width+2));
        void Add(int index){if(result.Count==0||result[^1].Index!=index)result.Add(new(index,values[index]));}
        int bucket=-1,first=start,last=start,min=start,max=start;
        long nextBoundary=firstTick;
        void Flush()
        {
            Span<int> indices=stackalloc int[4]{first,min,max,last};indices.Sort();
            foreach(int index in indices)Add(index);
        }
        for(int i=start;i<start+count;i++)
        {
            long tick=dates[i].Ticks;
            if(bucket<0||tick>=nextBoundary)
            {
                if(bucket>=0)Flush();
                int current=Math.Min(width-1,(int)((double)(tick-firstTick)/span*width));
                bucket=Math.Min(width-1,Math.Max(bucket+1,current));
                nextBoundary=bucket==width-1?long.MaxValue:firstTick+(long)Math.Ceiling((double)span*(bucket+1)/width);
                first=last=min=max=i;
            }
            last=i;
            if(values[i]<values[min])min=i;
            if(values[i]>values[max])max=i;
        }
        Flush();return result.ToArray();
    }
    public static ChartPoint[] SampleTimed(long[] values,SeriesPoint[] dates,int start,int count,int width,ChartRangeIndex index)
    {
        if(!index.Matches(values) || values.Length!=dates.Length || start<0 || count<0 || start>values.Length-count)throw new ArgumentOutOfRangeException(nameof(count));
        if(count==0)return [];
        width=Math.Clamp(width,1,16000);long firstTick=dates[start].Ticks,span=dates[start+count-1].Ticks-firstTick;
        if(span<=0)return Sample(values,start,count,width);
        var result=new List<ChartPoint>(Math.Min(count,4*width+2));int bucket=-1,end=start+count;
        Span<int> candidates=stackalloc int[4];
        for(int first=start;first<end;)
        {
            int current=Math.Min(width-1,(int)((double)(dates[first].Ticks-firstTick)/span*width));
            bucket=Math.Min(width-1,Math.Max(bucket+1,current));
            long next=bucket==width-1?long.MaxValue:firstTick+(long)Math.Ceiling((double)span*(bucket+1)/width);
            int lo=first+1,hi=end;
            while(lo<hi){int mid=lo+(hi-lo)/2;if(dates[mid].Ticks<next)lo=mid+1;else hi=mid;}
            var extrema=index.Query(first,lo);
            candidates[0]=first;candidates[1]=extrema.Min;candidates[2]=extrema.Max;candidates[3]=lo-1;candidates.Sort();
            foreach(int i in candidates)if(result.Count==0 || result[^1].Index!=i)result.Add(new(i,values[i]));
            first=lo;
        }
        return result.ToArray();
    }
    public static ChartPoint[] Sample(long[] values, int start, int count, int width)
    {
        if (start < 0 || count < 0 || start > values.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0)
            return [];
        width = Math.Clamp(width, 1, 16000);
        var points = new List<ChartPoint>(Math.Min(count, 2 * width + 2));
        void Add(int i)
        {
            if (points.Count == 0 || points[^1].Index != i)
                points.Add(new(i, values[i]));
        }
        Add(start);
        for (int b = 0; b < Math.Min(width, count); b++)
        {
            int buckets = Math.Min(width, count), lo = start + (int)((long)count * b / buckets), hi = start + (int)((long)count * (b + 1) / buckets);
            int min = lo, max = lo;
            for (int i = lo + 1; i < hi; i++)
            {
                if (values[i] < values[min])
                    min = i;
                if (values[i] > values[max])
                    max = i;
            }
            if (min < max)
            {
                Add(min);
                Add(max);
            }
            else
            {
                Add(max);
                Add(min);
            }
        }
        Add(start + count - 1);
        return points.ToArray();
    }
}
