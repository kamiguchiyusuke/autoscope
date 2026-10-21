using ArmadaAnalyzer;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
static class Performance
{
    public static int Profile(string directory)
    {
        string input = Path.Combine(directory, "data-1000000", "サンプル_EURUSD_42.htm");
        var workspace = new Workspace(Path.Combine(directory, "profile-" + Guid.NewGuid().ToString("N")));
        var imported = workspace.Import(input, new("USD"));
        var snapshot = workspace.LoadSeries(new([imported.Dataset.Id]));
        var result = Analytics.Analyze(snapshot.Points, Amount.Parse("1000000"));
        ChartSampling.Sample(result.Equity, 0, result.Count, 1200);
        Console.WriteLine("Profile pipeline complete: " + result.Count);
        return 0;
    }
    public static int Calibrate()
    {
        foreach (int count in new[] { 2400, 10000, 100000, 1000000 })
        {
            var random = new Random(42);
            long[] profits = Enumerable.Range(0, count).Select(_ => (long)random.Next(-12000, 15001) * 1000000).ToArray();
            var options = new MCOptions(10000, 1000, Workers: ResourceLimits.Workers);
            foreach (bool native in new[] { false, true })
            {
                MonteCarlo.Run(profits, options, native: native);
                double[] times = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    var watch = Stopwatch.StartNew();
                    MonteCarlo.Run(profits, options, native: native);
                    times[i] = watch.Elapsed.TotalMilliseconds;
                }
                Array.Sort(times);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    population = count,
                    engine = native ? "C++20" : "C#",
                    trials = 10000,
                    steps = 1000,
                    workers = options.Workers,
                    median_ms = times[1]
                }));
            }
        }
        return 0;
    }
    public static int Run(string output,bool dataOnly=false)
    {
        Directory.CreateDirectory(output);
        var measurements = new List<object>();
        void Record(object value)
        {
            measurements.Add(value);
            Console.WriteLine(JsonSerializer.Serialize(value));
        }
        double Time(Action action)
        {
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds;
        }
        double Median(Action action)
        {
            action();
            var values = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                values.Add(Time(action));
            }
            values.Sort();
            return values[2];
        }
        Snapshot? largest = null;
        foreach (int count in new[] { 10000, 100000, 1000000 })
        {
            string directory = Path.Combine(output, "data-" + count);
            string file = DemoReports.Create(directory, count, minutesPerTrade: 5);
            var workspace = new Workspace(Path.Combine(directory, "workspace-" + Guid.NewGuid().ToString("N")));
            ImportResult? imported = null;
            double import = Time(() => imported = workspace.Import(file, new("USD")));
            if (imported!.Dataset.Count != count)
                throw new Exception("performance import count mismatch");
            Filter filter = new([imported.Dataset.Id]);
            Snapshot? data = null;
            double firstRead = Time(() => data = workspace.LoadSeries(filter));
            if (data!.Points.Length != count)
                throw new Exception("performance series count mismatch");
            double cachedRead = Median(() => data = workspace.LoadSeries(filter));
            AnalysisResult? analysis = null;
            double stats = Median(() => analysis = Analytics.Analyze(data!.Points, Amount.Parse("1000000")));
            decimal reference = data!.Points.Sum(point => (decimal)point.Profit);
            if (analysis!.Net != reference)
                throw new Exception("performance net mismatch");
            ChartPoint[]? chart = null;
            double chartTime = Median(() => chart = ChartSampling.Sample(analysis.Equity, 0, count, 1200));
            if (chart!.Length > 2402)
                throw new Exception("unbounded chart");
            double page = Median(() => workspace.ReadPage(filter, count / 2));
            Record(new
            {
                stage = "dataset",
                count,
                html_bytes = new FileInfo(file).Length,
                import_including_sqlite_ms = import,
                normalized_first_read_ms = firstRead,
                cached_read_ms = cachedRead,
                statistics_including_curve_arrays_ms = stats,
                graph_preparation_1200px_ms = chartTime,
                middle_page_500_ms = page,
                chart_points = chart.Length,
                process_peak_working_set = Process.GetCurrentProcess().PeakWorkingSet64,
                workspace = workspace.Root
            });
            largest = data;
        }
        long[] profits = largest!.Points.Select(point => point.Profit).ToArray();
        foreach (int trials in dataOnly ? Array.Empty<int>() : new[] { 1000, 10000, 100000 })
        {
            var options = new MCOptions(trials, 1000, Workers: ResourceLimits.Workers);
            MCResult? managed = null, native = null;
            double managedTime = Median(() => managed = MonteCarlo.Run(profits, options, native: false));
            double nativeTime = Median(() => native = MonteCarlo.Run(profits, options, native: true));
            if (!managed!.Drawdowns.SequenceEqual(native!.Drawdowns) || !managed.Finals.SequenceEqual(native.Finals))
                throw new Exception("performance MC parity failure");
            Record(new
            {
                stage = "monte_carlo",
                population = profits.Length,
                trials,
                steps = 1000,
                workers = options.Workers,
                managed_ms = managedTime,
                native_ms = nativeTime,
                exact_match = true
            });
        }
        File.WriteAllText(Path.Combine(output, "performance.json"), JsonSerializer.Serialize(new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            logical_cpus = Environment.ProcessorCount,
            condition = "Warm OS cache; dataset import includes hash, full parser, all columns and SQLite commit. Other actions median of five after warmup. Graph preparation excludes WPF paint, measured separately by UI smoke.",
            measurements
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
