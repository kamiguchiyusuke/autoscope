namespace ArmadaAnalyzer.Contracts;

/// <summary>Public v1 plugin contract. All monetary values use 10^8 units per account currency.</summary>
public static class ContractVersion
{
    public const int Current = 1; public const long AmountScale = 100000000;
}
public sealed record PluginMetadata(string Id, string Name, string Version, string Description);
public interface IPlugin
{
    PluginMetadata Metadata
    {
        get;
    }
}
public readonly record struct TradePoint(long CloseTicks, long Profit, long DatasetId);
public sealed record DatasetInfo(long Id, string Name, string Symbol, string Currency, long InitialBalance);
public sealed record PluginContext(IReadOnlyList<TradePoint> Points, IReadOnlyList<DatasetInfo> Datasets, IReadOnlyDictionary<string, string> Settings);
public sealed record PluginMetric(string Name, string Value, string Unit = "");
public readonly record struct PlotPoint(double X, double Y);
public sealed record PlotSeries(string Name, IReadOnlyList<PlotPoint> Points, string Unit = "");
public sealed record ExtensionResult(IReadOnlyList<PluginMetric> Metrics, IReadOnlyList<PlotSeries>? Charts = null);
public interface IAnalysisPlugin : IPlugin
{
    ExtensionResult Analyze(PluginContext context, CancellationToken cancellationToken);
}
public interface IMonteCarloPlugin : IPlugin
{
    ExtensionResult Simulate(PluginContext context, CancellationToken cancellationToken);
}
public interface IChartPlugin : IPlugin
{
    ExtensionResult BuildChart(PluginContext context, CancellationToken cancellationToken);
}
public interface IEvaluationPlugin : IPlugin
{
    ExtensionResult Evaluate(PluginContext context, CancellationToken cancellationToken);
}
public interface IPortfolioOptimizerPlugin : IPlugin
{
    ExtensionResult Optimize(PluginContext context, CancellationToken cancellationToken);
}
public sealed record ReaderContext(string FileName, string Currency, string TimeBasis);
public sealed record ReportInfo(string Symbol, long InitialBalance);
public readonly record struct ImportedTrade(long Ticket, int Part, long OpenTicks, long CloseTicks, long Volume, long EntryPrice, long ExitPrice, long Profit, int Side, long SourceRow);
public sealed record ReportData(ReportInfo Info, IEnumerable<ImportedTrade> Trades);
public interface IReportReaderPlugin : IPlugin
{
    bool CanRead(string extension, ReadOnlySpan<byte> prefix);
    /// <summary>Do not dispose input. Enumeration must cooperate with cancellation and remain bounded.</summary>
    ReportData ReadReport(Stream input, ReaderContext context, CancellationToken cancellationToken);
}
