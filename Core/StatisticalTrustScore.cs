namespace ArmadaAnalyzer;

/// <summary>ARMADA display score; this is not a statistical confidence level.</summary>
public static class StatisticalTrustScore
{
    public static decimal? Compute(decimal? convergencePercent) =>
        convergencePercent is decimal percent ? Math.Clamp(100m - percent, 0m, 100m) : null;
}
