using System.Globalization;
using System.Windows;

namespace ArmadaAnalyzer.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? metricCancellation;
    private int metricGeneration;
    private string? metricKey,cachedMetricKey;
    private MetricEstimateResult? cachedMetricResult;
    internal Task MetricEstimateTask { get; private set; }=Task.CompletedTask;
    internal MetricEstimateResult? CurrentMetricEstimate { get; private set; }
    internal int MetricSimulationStarts { get; private set; }
    internal string? LastMetricError { get; private set; }

    private static string SignedRawAmount(decimal raw)
    {
        string text=NumberDisplay.GroupedTwoDecimal(raw/Amount.Scale);
        return raw>0 && text!="0"?"+"+text:text;
    }

    private void ScheduleMetricEstimates()
    {
        if(snapshot==null || analysis==null)return;
        var source=snapshot;var measured=analysis;
        NetValue.Text=SignedRawAmount(measured.Net);
        PfValue.Text=measured.ProfitFactor is double pf?NumberDisplay.TwoDecimal(pf):"算出不可";
        DdValue.Text=NumberDisplay.GroupedTwoDecimal(-(decimal)measured.Drawdown/Amount.Scale);
        InitialDdPercentValue.Text=measured.Count>0 && initialCapital>0
            ? NumberDisplay.TwoDecimal((double)measured.Drawdown/initialCapital*100)+"%":"算出不可";
        InitialDdBasisValue.Text=InitialDdPercentValue.Text=="算出不可"?"初期資本の割合":"初期資本の"+InitialDdPercentValue.Text;
        DdPercentValue.Text=measured.DrawdownPercent is double percent?NumberDisplay.TwoDecimal(percent)+"%":"算出不可";
        NetEstimateValue.Text=CurrentConvergence?.UpperTotal is decimal upper
            ? "±"+NumberDisplay.GroupedTwoDecimal((upper-measured.Net)/Amount.Scale):"算出不可";
        NetEstimateHint.Text="実測総損益 / 収束総損益の±金額幅。表示幅は上限−実測総損益で、下限・上限と同じ対称幅です。既存の総損益収束率と同じ係数・標本標準偏差・丸め前セット数を使用。総額への換算回数は元の総取引回数です。将来の資産経路の予測範囲ではありません。"+
            (CurrentConvergence?.Reason is string reason?"\n"+reason:"");
        var options=new MetricBootstrapOptions(1000,42,Math.Min(2,ResourceLimits.Workers),analyzedMemoryMiB);
        string key="weighted-circle-v1:"+source.Key+":"+initialCapital.ToString(CultureInfo.InvariantCulture)+":"+
            CurrentConvergence?.EffectiveCount?.ToString("R",CultureInfo.InvariantCulture)+":"+options;
        MetricEstimatePanel.Visibility=Visibility.Visible;
        if(metricKey==key && metricCancellation!=null)return;
        CancelMetricCore();metricKey=key;
        if(cachedMetricKey==key && cachedMetricResult!=null)
        {
            MetricEstimateTask=Task.CompletedTask;
            ApplyMetricEstimates(cachedMetricResult,true);return;
        }
        CurrentMetricEstimate=null;LastMetricError=null;
        PfEstimateValue.Text=InitialDdEstimateValue.Text=DdEstimateValue.Text="計算中";
        PfEstimateHint.Text=InitialDdEstimateHint.Text=DdEstimateHint.Text="全取引の加重ブロック再標本化をバックグラウンドで計算しています。";
        MetricEstimateStatus.Text="PF・DDの推定を計算中（実測値・グラフは変更しません）";
        MetricEstimateStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,"Muted");
        MetricEstimateProgress.Value=0;MetricEstimateProgress.Visibility=Visibility.Visible;
        MetricCancel.IsEnabled=true;MetricRetry.IsEnabled=false;
        var cancellation=new CancellationTokenSource();metricCancellation=cancellation;
        UpdateBackgroundStatus();
        int revision=metricGeneration;
        MetricSimulationStarts++;
        MetricEstimateTask=CalculateMetricEstimatesAsync(source,measured,initialCapital,options,key,revision,cancellation);
    }

    private bool IsCurrentMetric(string key,int revision)=>metricKey==key && metricGeneration==revision && !Dispatcher.HasShutdownStarted;

    private async Task CalculateMetricEstimatesAsync(Snapshot source,AnalysisResult measured,long capital,
        MetricBootstrapOptions options,string key,int revision,CancellationTokenSource cancellation)
    {
        var token=cancellation.Token;
        var progress=new Progress<WorkProgress>(p=>{
            if(IsCurrentMetric(key,revision) && !token.IsCancellationRequested)
            {
                MetricEstimateProgress.Value=Math.Clamp(p.Fraction*100,0,100);
                MetricEstimateStatus.Text=p.Message;
                if(!busy&&!autoRunning&&LastError==null&&!lastJobCanceled)StatusText.Text=p.Message;
            }
        });
        try
        {
            var result=await Task.Run(()=>MetricEstimates.Run(source.Points,measured,capital,options,token,progress),token);
            token.ThrowIfCancellationRequested();
            if(!IsCurrentMetric(key,revision))return;
            cachedMetricKey=key;cachedMetricResult=result;
            ApplyMetricEstimates(result,false);
        }
        catch(OperationCanceledException) { }
        catch(Exception error)
        {
            if(IsCurrentMetric(key,revision))
            {
                LastMetricError=error.Message;CurrentMetricEstimate=null;
                PfEstimateValue.Text=InitialDdEstimateValue.Text=DdEstimateValue.Text="算出不可";
                PfEstimateHint.Text=InitialDdEstimateHint.Text=DdEstimateHint.Text=error.Message;
                MetricEstimateStatus.Text="PF・DD推定を完了できませんでした: "+error.Message;
                MetricEstimateStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,"Loss");
            }
        }
        finally
        {
            if(ReferenceEquals(metricCancellation,cancellation))metricCancellation=null;
            cancellation.Dispose();
            UpdateBackgroundStatus();
            if(IsCurrentMetric(key,revision))
            {
                MetricEstimateProgress.Visibility=Visibility.Collapsed;
                MetricCancel.IsEnabled=false;MetricRetry.IsEnabled=true;
            }
        }
    }

    private void ApplyMetricEstimates(MetricEstimateResult result,bool cached)
    {
        MetricEstimateStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,"Muted");
        CurrentMetricEstimate=result;LastMetricError=result.Trials==0?result.ProfitFactor.Reason:null;
        PfEstimateValue.Text=result.ProfitFactor is {Lower:double pLow,Upper:double pHigh}
            ?NumberDisplay.TwoDecimal(pLow)+"〜"+NumberDisplay.TwoDecimal(pHigh):"算出不可";
        DdEstimateValue.Text=result.DrawdownPercent is {Lower:double dLow,Upper:double dHigh}
            ?NumberDisplay.TwoDecimal(dLow)+"〜"+NumberDisplay.TwoDecimal(dHigh)+"%":"算出不可";
        InitialDdEstimateValue.Text=initialCapital>0 && result.DrawdownAmount is {Lower:double iLow,Upper:double iHigh}
            ?NumberDisplay.TwoDecimal(iLow/initialCapital*100)+"〜"+NumberDisplay.TwoDecimal(iHigh/initialCapital*100)+"%":"算出不可";
        string method=$"ARMADA独自の加重円環ブロック近似。{result.Trials:N0}試行、Seed42、ブロック長{result.BlockLength}、丸め前セット数{result.EffectiveSets:N6}。中央値より下側・上側の条件付き平均であり、厳密な信頼区間や将来の保証範囲ではありません。実取引の数量・損益・グラフは変更しません。";
        PfEstimateHint.Text=method+"\nPFは各試行の総利益/総損失から共同再計算。係数0.79788456や総損益収束率をPFに直接加減算しません。"+(result.ProfitFactor.Reason is string pReason?"\n"+pReason:"");
        InitialDdEstimateHint.Text=method+"\n初資比DD（初期証拠金比最大DD） = 最大DD金額 ÷ 初期証拠金 × 100。推定は同じ各試行の最大DD金額を初期証拠金で割って算出。残高ピークを分母にせず、追加のシミュレーションは行いません。TradeStationの Max. Drawdown as % of Initial Capital に相当し、元本割れを表す絶対DDとは異なります。"+(result.DrawdownAmount.Reason is string iReason?"\n"+iReason:"");
        DdEstimateHint.Text=method+"\nDDは生成した決済損益系列の順序と現在の初期資本から残高ピーク比を再計算。含み損は含みません。"+(result.DrawdownPercent.Reason is string dReason?"\n"+dReason:"");
        MetricMethodHint.Text=method+"\n独立ブロックの重みを補正セット数に合わせ、元の取引回数に相当する総エクスポージャーへ正規化しています。実取引数と同数の無加重ブートストラップではありません。";
        MetricEstimateStatus.Text=cached?"同一条件のPF・DD推定結果を再利用":result.Trials>0
            ?$"PF・DD推定: {result.Trials:N0}試行 · {result.Milliseconds:N0} ms · 条件付き平均（独自近似）":"PF・DD推定: 算出不可（理由は説明を参照）";
        MetricEstimateProgress.Visibility=Visibility.Collapsed;MetricCancel.IsEnabled=false;MetricRetry.IsEnabled=true;
    }

    private bool CancelMetricCore()
    {
        metricGeneration++;
        var cancellation=metricCancellation;metricCancellation=null;
        cancellation?.Cancel();return cancellation!=null;
    }
    internal void CancelMetricEstimates()
    {
        if(!CancelMetricCore())return;
        PfEstimateValue.Text=InitialDdEstimateValue.Text=DdEstimateValue.Text="中止";
        MetricEstimateStatus.Text="PF・DD推定を中止しました。実測値と総損益の解析範囲は維持しています。";
        MetricEstimateProgress.Visibility=Visibility.Collapsed;MetricCancel.IsEnabled=false;MetricRetry.IsEnabled=true;
    }
    private void ClearMetricEstimates()
    {
        CancelSlippage();slippageKey=null;SlippageValue.Text="算出不可";
        CancelMetricCore();metricKey=cachedMetricKey=null;cachedMetricResult=null;CurrentMetricEstimate=null;LastMetricError=null;
        NetEstimateValue.Text=PfEstimateValue.Text=InitialDdEstimateValue.Text=InitialDdPercentValue.Text=DdEstimateValue.Text=DdPercentValue.Text="算出不可";
        InitialDdBasisValue.Text="初期資本の割合";
        MetricEstimatePanel.Visibility=Visibility.Collapsed;
        MetricEstimateProgress.Visibility=Visibility.Collapsed;MetricCancel.IsEnabled=false;MetricRetry.IsEnabled=false;
    }
    private void MetricCancel_Click(object sender,RoutedEventArgs e)=>CancelMetricEstimates();
    private void MetricRetry_Click(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        cachedMetricKey=null;cachedMetricResult=null;
        ScheduleMetricEstimates();
    }
}
