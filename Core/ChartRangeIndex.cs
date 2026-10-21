namespace ArmadaAnalyzer;
// Exact extrema index: 32 KiB per million values, preserving first-index ties.
public sealed class ChartRangeIndex
{
    private const int Block=256;
    private readonly long[] values;
    private readonly int[] minima,maxima;
    public bool Matches(long[] source)=>ReferenceEquals(values,source);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public ChartRangeIndex(long[] values)
    {
        this.values=values;minima=new int[(values.Length+Block-1)/Block];maxima=new int[minima.Length];
        for(int b=0;b<minima.Length;b++)
        {
            int min=b*Block,max=min,end=Math.Min(values.Length,min+Block);
            for(int i=min+1;i<end;i++){if(values[i]<values[min])min=i;if(values[i]>values[max])max=i;}
            minima[b]=min;maxima[b]=max;
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public (int Min,int Max) Query(int start,int end)
    {
        if(start<0 || end>values.Length || start>=end)throw new ArgumentOutOfRangeException(nameof(start));
        int min=start,max=start,i=start;
        while(i<end && (i%Block)!=0){if(values[i]<values[min])min=i;if(values[i]>values[max])max=i;i++;}
        while(i<=end-Block)
        {
            int lo=minima[i/Block],hi=maxima[i/Block];if(values[lo]<values[min])min=lo;if(values[hi]>values[max])max=hi;i+=Block;
        }
        while(i<end){if(values[i]<values[min])min=i;if(values[i]>values[max])max=i;i++;}
        return(min,max);
    }
}
