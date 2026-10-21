namespace ArmadaAnalyzer.Desktop;

public partial class MainWindow
{
    private AnalysisResult? convergenceSource;
    internal ConvergenceWidthResult? CurrentConvergence { get; private set; }
    internal WinRateConvergenceResult? CurrentWinRateConvergence { get; private set; }
    internal static string PeriodScoreColor(double? score) => score is >=85 ? "ScoreBlue" : score is >=75 ? "ScoreYellow" : score.HasValue ? "ScoreRed" : "Muted";

    private void UpdateConvergence(AnalysisResult source)
    {
        var points=snapshot?.Points;
        DateTime? start=points is {Length:>0}?new DateTime(points[0].Ticks).Date:null;
        DateTime? end=points is {Length:>0}?new DateTime(points[^1].Ticks).Date:null;
        PeriodTrustValue.Text=PeriodTrustScore.Compute(start,end) is double periodScore
            ? $"{periodScore:F2}%":"算出不可";
        PeriodTrustValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,PeriodScoreColor(PeriodTrustScore.Compute(start,end)));
        PeriodTrustValue.ToolTip="分析対象の最初～最後の決済日を使用（元レポートのテスト設定期間ではありません）。日数÷365.2425で年換算。S(t)=100×(1−0.3^((t/5)^a))、a=ln(ln(0.1)/ln(0.3))/ln(2)。期間の長さだけによる独自評価で、統計的保証や収益性を表しません。"+
            (start.HasValue && end.HasValue?$"\n{start:yyyy/MM/dd} ～ {end:yyyy/MM/dd}":"\n期間データがありません。");
        if(ReferenceEquals(convergenceSource,source)) return;
        convergenceSource=source;
        CurrentConvergence=ConvergenceWidth.Compute(source);
        CurrentWinRateConvergence=WinRateConvergence.Compute(source.Count,source.Wins,CurrentConvergence.EffectiveCount);
        WinRangeValue.Text=CurrentWinRateConvergence is {LowerPercent:double low,UpperPercent:double high}
            ? $"{NumberDisplay.TwoDecimal(low)}〜{NumberDisplay.TwoDecimal(high)}%" : "算出不可";
        WinRangeValue.ToolTip="ARMADA独自の近似的な収束幅であり、厳密な信頼区間ではありません。pは勝数÷総取引回数（引き分けを含む）。範囲 = p ± 0.79788456 × √(p×(1−p)÷丸め前の総セット数)。0〜100%に制限。総取引回数・総セット数がともに30以上の場合に表示します。"+
            (CurrentWinRateConvergence.Reason is string reason ? "\n"+reason : "");
        string sets=CurrentConvergence.TotalSets?.ToString("N0") ?? "算出不可";
        CountValue.Text=$"{source.Count:N0} / {sets}";
        CountValue.ToolTip="総取引回数 / 総セット数。ARMADA独自の統計的補正による推定値です。X = max(連勝倍率, 連敗倍率)、総セット数 = 総取引回数 / X。表示のみ四捨五入。"+
            (CurrentConvergence.TotalSets.HasValue ? $"\nX {CurrentConvergence.Multiplier:0.000000} / 総セット数（丸め前）{CurrentConvergence.EffectiveCount:N6}" : "\n"+CurrentConvergence.Reason);
        ConvergenceValue.Text=CurrentConvergence.Percent is decimal x ? $"±{NumberDisplay.TwoDecimal(x)}%" : "算出不可";
        TrustScoreValue.Text=StatisticalTrustScore.Compute(CurrentConvergence.Percent) is decimal score
            ? $"{NumberDisplay.TwoDecimal(score)}%" : "—";
        TrustScoreValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,PeriodScoreColor(StatisticalTrustScore.Compute(CurrentConvergence.Percent) is decimal trust?(double)trust:null));
        string basis="ARMADA独自の有効取引回数補正と、正規分布の上下各側の条件付き平均変動幅（係数0.79788456）による推定幅です。標準誤差の正規近似で中央約57.51%、両端合計約42.49%を除外する幅に相当します。正規近似が成立しない場合も含め、実際の確率範囲や厳密な統計的保証を与えません。将来の資産曲線の予測幅ではなく、グラフや既存計算には影響しません。";
        ConvergenceHint.Text=basis+"\n"+(CurrentConvergence.Reason ?? $"X {CurrentConvergence.Multiplier:0.000000} / 総セット数（丸め前）{CurrentConvergence.EffectiveCount:N6}。推定誤差には総セット数、総額換算には元の総取引回数を使用。")+
            "\nX = max(連勝倍率, 連敗倍率)。記録された取引損益を使用。総取引回数・丸め前の総セット数がともに30以上の場合に収束幅を表示します。";
    }

    private void ClearConvergence()
    {
        ClearMetricEstimates();
        convergenceSource=null;CurrentConvergence=null;
        CurrentWinRateConvergence=null;WinRangeValue.Text="算出不可";WinRangeValue.ToolTip=null;
        CountValue.Text="—";CountValue.ToolTip=null;
        ConvergenceValue.Text="算出不可";
        TrustScoreValue.Text="—";
        TrustScoreValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,"Muted");
        PeriodTrustValue.Text="算出不可";PeriodTrustValue.ToolTip=null;
        PeriodTrustValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,"Muted");
        ConvergenceHint.Text="取引を分析すると総損益収束率を表示します。";
    }
}
