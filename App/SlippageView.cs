namespace ArmadaAnalyzer.Desktop;
public partial class MainWindow
{
    private CancellationTokenSource? slippageCancellation;
    private string? slippageKey;
    private int slippageGeneration;
    internal Task SlippageTask {get;private set;}=Task.CompletedTask;
    private void CancelSlippage()
    {
        slippageGeneration++;slippageKey=null;
        slippageCancellation?.Cancel();slippageCancellation=null;
    }
    private void ScheduleSlippage()
    {
        if(snapshot==null || analysis==null || appliedFilter==null)return;
        var source=workspace;var series=snapshot;var measured=analysis;var filter=appliedFilter;
        string key=source.Root+":"+series.Key;
        if(slippageKey==key)return;
        CancelSlippage();slippageKey=key;
        var cancellation=new CancellationTokenSource();slippageCancellation=cancellation;
        UpdateBackgroundStatus();
        int generation=slippageGeneration;
        SlippageValue.Text="計算中";
        SlippageTask=Calculate();
        async Task Calculate()
        {
            try
            {
                var result=await Task.Run(()=>source.EstimateSlippage(filter,series.Key,measured.Net,measured.Count,cancellation.Token),cancellation.Token);
                if(cancellation.IsCancellationRequested || generation!=slippageGeneration)return;
                SlippageValue.Text=result.Pips is decimal p?"約"+NumberDisplay.GroupedTwoDecimal(p):"算出不可";
                SlippageCard.ToolTip=result.Detail;
            }
            catch(OperationCanceledException) { }
            catch(Exception error)
            {
                if(generation==slippageGeneration){SlippageValue.Text="算出不可";SlippageCard.ToolTip=error.Message;}
            }
            finally
            {
                if(ReferenceEquals(slippageCancellation,cancellation))slippageCancellation=null;
                cancellation.Dispose();
                UpdateBackgroundStatus();
            }
        }
    }
}
