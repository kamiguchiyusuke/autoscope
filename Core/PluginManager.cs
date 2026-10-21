using ArmadaAnalyzer.Contracts;
using System.Reflection;
using System.Runtime.Loader;
namespace ArmadaAnalyzer;

public sealed class PluginManager
{
    readonly List<IPlugin> plugins = [];
    readonly List<AssemblyLoadContext> contexts = [];
    public IReadOnlyList<IPlugin> Plugins => plugins.AsReadOnly();
    public void Load(string file)
    {
        var context = new PluginLoadContext(Path.GetFullPath(file));
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(file));
        var additions = assembly.GetExportedTypes().Where(t => !t.IsAbstract && typeof(IPlugin).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null).Select(t => (IPlugin)Activator.CreateInstance(t)!).ToArray();
        if (additions.Length == 0)
            throw new InvalidOperationException("v1の公開プラグインがありません");
        if (plugins.Count + additions.Length > 64 || plugins.Concat(additions).GroupBy(p => p.Metadata.Id).Any(g => g.Count() > 1))
            throw new InvalidOperationException("プラグインIDが重複しているか、64個の上限を超えています");
        plugins.AddRange(additions);
        contexts.Add(context);
    }
    public ExtensionResult Run(IPlugin plugin, PluginContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var output = plugin switch
        {
            IAnalysisPlugin p => p.Analyze(context, token),
            IMonteCarloPlugin p => p.Simulate(context, token),
            IChartPlugin p => p.BuildChart(context, token),
            IEvaluationPlugin p => p.Evaluate(context, token),
            IPortfolioOptimizerPlugin p => p.Optimize(context, token),
            _ => throw new InvalidOperationException("この拡張は取込画面から使用してください")
        };
        token.ThrowIfCancellationRequested();
        if (output.Metrics.Count > 1000 || output.Charts?.Count > 8 || output.Charts?.Any(c => c.Points.Count > 16000) == true)
            throw new InvalidOperationException("拡張結果が表示上限を超えています（指標1000個、グラフ8本、各16000点）");
        return output;
    }
    sealed class PluginLoadContext(string file) : AssemblyLoadContext(isCollectible: false)
    {
        readonly AssemblyDependencyResolver resolver = new(file);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IPlugin).Assembly.GetName().Name)
                return typeof(IPlugin).Assembly;
            string? path = resolver.ResolveAssemblyToPath(name);
            return path == null ? null : LoadFromAssemblyPath(path);
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            string? path = resolver.ResolveUnmanagedDllToPath(name);
            return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
