namespace ArmadaAnalyzer;

public sealed record WinRateConvergenceResult(double? LowerPercent,double? UpperPercent,string? Reason);

/// <summary>Display-only ARMADA approximation using the unrounded total-set count.</summary>
public static class WinRateConvergence
{
    public static WinRateConvergenceResult Compute(int count,int wins,double? totalSets)
    {
        if(count<ConvergenceWidth.MinimumCount)
            return new(null,null,"総取引回数が30未満のため算出できません。");
        if(wins<=0 || wins>=count)
            return new(null,null,"勝率0%・100%、または勝ち数が不正な場合は補正できません。");
        if(totalSets is not double n || !double.IsFinite(n) || n<ConvergenceWidth.MinimumCount)
            return new(null,null,"丸め前の総セット数が算出不可、または30未満のため算出できません。");
        double p=(double)wins/count;
        double margin=ConvergenceWidth.ConditionalMeanCoefficient*Math.Sqrt(p*(1-p)/n);
        return new(Math.Clamp(p-margin,0,1)*100,Math.Clamp(p+margin,0,1)*100,null);
    }
}
