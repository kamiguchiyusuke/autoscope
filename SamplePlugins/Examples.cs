using ArmadaAnalyzer.Contracts;
using System.Globalization;

namespace ArmadaAnalyzer.SamplePlugins;

public sealed class ExpectancyPlugin : IAnalysisPlugin
{
    public PluginMetadata Metadata => new("samples.expectancy", "平均損益のサンプル", "1.0.0", "独立した分析指標の追加例です。");
    public ExtensionResult Analyze(PluginContext context, CancellationToken cancellationToken)
    {
        decimal total = 0;
        foreach (TradePoint point in context.Points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += (decimal)point.Profit / ContractVersion.AmountScale;
        }
        return new([new("取引数", context.Points.Count.ToString(CultureInfo.InvariantCulture)),
            new("平均損益", context.Points.Count == 0 ? "未定義" : (total / context.Points.Count).ToString("F8", CultureInfo.InvariantCulture))]);
    }
}

public sealed class CumulativeChartPlugin : IChartPlugin
{
    public PluginMetadata Metadata => new("samples.chart", "累積損益グラフのサンプル", "1.0.0", "512区間の極値を保つ独自グラフです。");
    public ExtensionResult BuildChart(PluginContext context, CancellationToken cancellationToken)
    {
        var output = new List<PlotPoint>();
        decimal balance = 0;
        int count = context.Points.Count;
        for (int bucket = 0; bucket < Math.Min(512, count); bucket++)
        {
            int first = (int)((long)count * bucket / Math.Min(512, count));
            int end = (int)((long)count * (bucket + 1) / Math.Min(512, count));
            decimal minimum = decimal.MaxValue, maximum = decimal.MinValue;
            int minimumIndex = first, maximumIndex = first;
            for (int index = first; index < end; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                balance += (decimal)context.Points[index].Profit / ContractVersion.AmountScale;
                if (balance < minimum)
                {
                    minimum = balance;
                    minimumIndex = index;
                }
                if (balance > maximum)
                {
                    maximum = balance;
                    maximumIndex = index;
                }
            }
            var low = new PlotPoint(minimumIndex, (double)minimum);
            var high = new PlotPoint(maximumIndex, (double)maximum);
            if (minimumIndex < maximumIndex)
            {
                output.Add(low);
                output.Add(high);
            }
            else
            {
                output.Add(high);
                output.Add(low);
            }
        }
        return new([], [new("サンプル累積損益", output)]);
    }
}

public sealed class EvaluationPlugin : IEvaluationPlugin
{
    public PluginMetadata Metadata => new("samples.evaluation", "純損益/DD比のサンプル", "1.0.0", "予測ではなく、決済履歴の記述的な評価指標です。");
    public ExtensionResult Evaluate(PluginContext context, CancellationToken cancellationToken)
    {
        decimal balance = 0, peak = 0, drawdown = 0;
        foreach (TradePoint point in context.Points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            balance += point.Profit;
            peak = Math.Max(peak, balance);
            drawdown = Math.Max(drawdown, peak - balance);
        }
        return new([new("純損益/最大DD", drawdown == 0 ? "未定義" : (balance / drawdown).ToString("F4", CultureInfo.InvariantCulture))]);
    }
}

public sealed class EqualWeightPlugin : IPortfolioOptimizerPlugin
{
    public PluginMetadata Metadata => new("samples.equalweight", "等ウェイト基準案のサンプル", "1.0.0", "最適性を主張しない、最適化プラグインの出力形式例です。");
    public ExtensionResult Optimize(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(context.Datasets.Select(dataset => new PluginMetric(dataset.Name,
            (1m / Math.Max(1, context.Datasets.Count)).ToString("F6", CultureInfo.InvariantCulture), "weight")).ToArray());
    }
}

public sealed class BootstrapPlugin : IMonteCarloPlugin
{
    public PluginMetadata Metadata => new("samples.bootstrap", "C#復元抽出のサンプル", "1.0.0", ".NET Randomを使う独立実装。組込エンジンの再現シードとは別方式です。");
    public ExtensionResult Simulate(PluginContext context, CancellationToken cancellationToken)
    {
        if (context.Points.Count == 0)
            return new([new("状態", "取引なし")]);
        int trials = int.Parse(context.Settings.GetValueOrDefault("trials", "1000"), CultureInfo.InvariantCulture);
        int steps = int.Parse(context.Settings.GetValueOrDefault("steps", "1000"), CultureInfo.InvariantCulture);
        int seed = int.Parse(context.Settings.GetValueOrDefault("seed", "42"), CultureInfo.InvariantCulture);
        if (trials is < 1 or > 100000 || steps is < 1 or > 1000000)
            throw new ArgumentOutOfRangeException(nameof(trials));
        var drawdowns = new decimal[trials];
        for (int trial = 0; trial < trials; trial++)
        {
            var random = new Random(unchecked(seed + trial));
            decimal balance = 0, peak = 0, drawdown = 0;
            for (int step = 0; step < steps; step++)
            {
                if ((step & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                balance += (decimal)context.Points[random.Next(context.Points.Count)].Profit / ContractVersion.AmountScale;
                peak = Math.Max(peak, balance);
                drawdown = Math.Max(drawdown, peak - balance);
            }
            drawdowns[trial] = drawdown;
        }
        Array.Sort(drawdowns);
        return new([new("最大DDの95%分位", drawdowns[(int)Math.Ceiling(trials * 0.95) - 1].ToString("F8", CultureInfo.InvariantCulture))]);
    }
}
