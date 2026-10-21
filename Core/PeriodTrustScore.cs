namespace ArmadaAnalyzer;

/// <summary>Duration-only ARMADA rating; not a statistical confidence level.</summary>
public static class PeriodTrustScore
{
    private static readonly double Exponent=Math.Log(Math.Log(0.1)/Math.Log(0.3))/Math.Log(2);
    public static double? Compute(DateTime? start,DateTime? end)
    {
        if(start is not DateTime first || end is not DateTime last || last<first)return null;
        double years=(last-first).TotalDays/365.2425;
        return Math.Clamp(100*(1-Math.Pow(0.3,Math.Pow(years/5,Exponent))),0,100);
    }
}
