namespace ArmadaAnalyzer;

public sealed record ConvergenceWidthResult(decimal? Percent, decimal? UpperTotal, double? Multiplier,
    double? EffectiveCount, string? Reason)
{
    public long? TotalSets { get; init; }
}

/// <summary>Display-only ARMADA approximation. Does not alter analysis, trades, equity or drawdowns.</summary>
public static class ConvergenceWidth
{
    public const double ConditionalMeanCoefficient = 0.79788456;
    public const int MinimumCount = 30;

    public static ConvergenceWidthResult Compute(AnalysisResult a)
    {
        double? multiplier=null, effectiveCount=null;
        long? roundedSets=null;
        ConvergenceWidthResult Missing(string reason)=>new(null,null,multiplier,effectiveCount,reason){TotalSets=roundedSets};
        if(a.Count<=0 || a.Wins<=0 || a.Losses<=0 || a.WinRuns<=0 || a.LossRuns<=0 || a.WinRuns>a.Wins || a.LossRuns>a.Losses ||
            a.Zeros<0 || (long)a.Wins+a.Losses+a.Zeros!=a.Count)
            return Missing("勝ち・負けと連勝・連敗の記録が必要です。勝率0%・100%や連続記録不足では算出できません。");
        double p=(double)a.Wins/a.Count;
        double theoreticalWin=1/(1-p),theoreticalLoss=1/p;
        double winFactor=((double)a.Wins/a.WinRuns)/theoreticalWin;
        double lossFactor=((double)a.Losses/a.LossRuns)/theoreticalLoss;
        double k=Math.Max(winFactor,lossFactor);
        if(!double.IsFinite(k) || k<=0) return Missing("総セット数の補正倍率Xが0以下、または不正です。");
        double effective=a.Count/k;
        if(!double.IsFinite(effective) || effective<=0 || effective>=long.MaxValue)
            return Missing("総セット数が数値の表現範囲外のため算出できません。");
        multiplier=k; effectiveCount=effective;
        // Compare the count ratios exactly and round the rational N / X, so 4.5 cannot
        // become 4.499999999999999 through binary floating point and round down.
        bool winIsLarger=(Int128)(a.Count-a.Wins)*a.LossRuns >= (Int128)a.Losses*a.WinRuns;
        Int128 numerator=(Int128)a.Count*a.Count*(winIsLarger?a.WinRuns:a.LossRuns);
        Int128 denominator=(Int128)a.Wins*(winIsLarger?a.Count-a.Wins:a.Losses);
        roundedSets=checked((long)(numerator/denominator+(numerator%denominator*2>=denominator?1:0)));
        // Total sets remain available even when the convergence width cannot be estimated.
        if(a.Net<=0) return Missing("総損益が0以下のため、相対的な±幅は算出できません。");
        if(a.Count<MinimumCount) return Missing("総取引回数が30未満のため収束幅は算出できません。");
        if(effective<MinimumCount) return Missing("総セット数（丸め前）が30未満のため収束幅は算出できません。");
        if(a.SampleDeviation is not double s || !double.IsFinite(s) || s<0)
            return Missing("取引損益の標本標準偏差を計算できません。");
        try
        {
            decimal mean=(decimal)a.Net/a.Count;
            decimal margin=(decimal)(ConditionalMeanCoefficient*s/Math.Sqrt(effective));
            decimal upperTotal=(mean+margin)*a.Count;
            // Algebraically (upperTotal / net - 1) * 100, avoiding cancellation near zero width.
            decimal percent=margin/a.Net*a.Count*100m;
            return new(percent,upperTotal,k,effective,null){TotalSets=roundedSets};
        }
        catch(OverflowException)
        {
            return Missing("期待値収束幅が数値の表現範囲を超えるため算出できません。");
        }
    }
}
