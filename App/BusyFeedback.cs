using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media;
namespace ArmadaAnalyzer.Desktop;
public partial class MainWindow
{
    private bool backgroundStatusActive;
    private void ShowAutoLogProgress(string line)
    {
        if(!autoRunning||busy)return;
        string? phase=line.Contains("TDSキャッシュを照合")?"TDSデータの変更を確認中"
            :line.Contains("TDSBridgeImport:")||line.Contains("TDSBridgeSmoke:")?"TDSデータをMT5へ取り込み・全件照合中"
            :line.Contains("確認済み履歴を再利用:")?"確認済みTDSデータを再利用中"
            :line.Contains("検証済みの復元データを準備")?"テスト前の履歴保全を確認中"
            :line.Contains("起動PID")||line.Contains("本体PID")?"バックテスト実行中（端末の初期化・履歴同期を含みます）"
            :line.Contains("起動プロセス終了コード")?"テスト完了 · 標準レポートを保存・確認中"
            :null;
        if(phase!=null)ShowAutoProgress(phase);
    }
    private void UpdateBackgroundStatus()
    {
        if(busy||autoRunning)return;
        bool estimates=metricCancellation!=null,slippage=slippageCancellation!=null;
        if(LastError!=null||lastJobCanceled){SetBusyFeedback(estimates||slippage);return;}
        if(estimates||slippage)
        {
            backgroundStatusActive=true;
            StatusText.Text=estimates&&slippage?"PF・DD推定 / スリップ耐久pipsを計算中"
                :estimates?"PF・DD推定を計算中":"スリップ耐久pipsを計算中";
        }
        else if(backgroundStatusActive)
        {
            backgroundStatusActive=false;
            StatusText.Text="待機中 · 分析結果を表示しています";
        }
        SetBusyFeedback(estimates||slippage);
    }
    private void SetBusyFeedback(bool active)
    {
        active |= autoRunning || metricCancellation!=null || slippageCancellation!=null;
        BusySpinner.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        JobProgress.IsIndeterminate = active && MotionEnabled;
        BusyRotation.BeginAnimation(RotateTransform.AngleProperty, active && MotionEnabled
            ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }
    private void ShowAutoProgress(string message)
    {
        AutoProgressPanel.Visibility=autoRunning?Visibility.Visible:Visibility.Collapsed;
        AutoProgress.IsIndeterminate=autoRunning&&MotionEnabled;
        AutoProgressText.Text=message;
        if(!busy)
        {
            SetBusyFeedback(autoRunning);
            StatusText.Text=message;
        }
    }
}
