using System.Diagnostics;
using System.Runtime.InteropServices;
namespace ArmadaAnalyzer;

public sealed record MCOptions(int Trials = 1000, int Steps = 1000, int Method = 0, int SkipPercent = 10, uint Seed = 42, int Workers = 1, int MemoryMiB = 256);
public sealed record MCResult(long[] Drawdowns, long[] Finals, string Engine, double Milliseconds);
public static unsafe class MonteCarlo
{
    [DllImport("ArmadaCompute", CallingConvention = CallingConvention.Cdecl)] static extern int run_mc(long* pnl, int count, int trials, int steps, int method, int skip, uint seed, int workers, long* dd, long* finals, int* cancel, int* done);
    public static MCResult Run(long[] pnl, MCOptions options, CancellationToken token = default, IProgress<WorkProgress>? progress = null, bool? native = null)
    {
        token.ThrowIfCancellationRequested();
        var o = options;
        if (pnl.Length == 0 || o.Trials is < 1 or > 100000 || o.Steps is < 1 or > 1000000 || o.Method is < 0 or > 2 || o.SkipPercent is < 0 or > 100 || o.Workers is < 1 or > 8 || (o.Method != 0 && o.Steps > pnl.Length))
            throw new ArgumentException("MCの入力/試行数/試行長/並列数が範囲外です");
        long required = pnl.LongLength * 56 + o.Trials * 24L + (o.Method == 1 ? pnl.LongLength * 4 * o.Workers : 0) + 16777216;
        if (required > ResourceLimits.MemoryMiB(o.MemoryMiB) * 1048576L)
            throw new InvalidOperationException("MCがメモリ予算を超えます。試行長または並列数を減らしてください。");
        // Use the managed engine by default; native execution remains explicitly selectable.
        // Keep native explicitly selectable for reproducibility and future comparative optimization.
        var dd = new long[o.Trials];
        var finals = new long[o.Trials];
        bool useNative = native ?? false;
        var watch = Stopwatch.StartNew();
        if (useNative)
        {
            int* flags = (int*)NativeMemory.AllocZeroed(8);
            CancellationTokenRegistration registration = default;
            Timer? timer = null;
            try
            {
                nint address = (nint)flags;
                registration = token.Register(() => Interlocked.Exchange(ref *(int*)address, 1));
                timer = new Timer(_ => progress?.Report(new((double)Volatile.Read(ref *((int*)address + 1)) / o.Trials, "Monte Carlo計算中")), null, 100, 100);
                int rc;
                fixed (long* p = pnl, d = dd, f = finals)
                    rc = run_mc(p, pnl.Length, o.Trials, o.Steps, o.Method, o.SkipPercent, o.Seed, o.Workers, d, f, flags, flags + 1);
                token.ThrowIfCancellationRequested();
                if (rc == 1)
                    throw new OperationCanceledException(token);
                if (rc == 3)
                    throw new OverflowException("MC累積損益が整数の表現範囲を超えました");
                if (rc != 0)
                    throw new InvalidOperationException("ネイティブ計算エラー: " + rc);
            }
            finally { registration.Dispose(); timer?.DisposeAsync().AsTask().GetAwaiter().GetResult(); NativeMemory.Free(flags); }
        }
        else
        {
            int done = 0;
            long last = 0;
            try
            {
                Parallel.For(0, o.Workers, new ParallelOptions { MaxDegreeOfParallelism = o.Workers, CancellationToken = token }, w =>
                {
                    int[] order = o.Method == 1 ? new int[pnl.Length] : [];
                    for (int t = w; t < o.Trials; t += o.Workers)
                    {
                        token.ThrowIfCancellationRequested();
                        var random = new RandomState(o.Seed, t);
                        if (o.Method == 1)
                        {
                            for (int j = 0; j < order.Length; j++)
                                order[j] = j;
                            for (int j = order.Length - 1; j > 0; j--)
                            {
                                if ((j & 4095) == 0)
                                    token.ThrowIfCancellationRequested();
                                int k = (int)random.Uniform((uint)(j + 1));
                                (order[j], order[k]) = (order[k], order[j]);
                            }
                        }
                        long balance = 0, peak = 0, drawdown = 0;
                        for (int j = 0; j < o.Steps; j++)
                        {
                            if ((j & 4095) == 0)
                                token.ThrowIfCancellationRequested();
                            if (o.Method == 2 && random.Uniform(100) < o.SkipPercent)
                                continue;
                            int index = o.Method == 0 ? (int)random.Uniform((uint)pnl.Length) : o.Method == 1 ? order[j] : j;
                            balance = checked(balance + pnl[index]);
                            peak = Math.Max(peak, balance);
                            drawdown = Math.Max(drawdown, checked(peak - balance));
                        }
                        dd[t] = drawdown;
                        finals[t] = balance;
                        int completed = Interlocked.Increment(ref done);
                        long now = watch.ElapsedMilliseconds, prior = Interlocked.Read(ref last);
                        if (now - prior >= 100 && Interlocked.CompareExchange(ref last, now, prior) == prior)
                            progress?.Report(new((double)completed / o.Trials, "Monte Carlo計算中"));
                    }
                });
            }
            catch (AggregateException e) { var root = e.Flatten().InnerExceptions[0]; if (root is OverflowException) throw new OverflowException("MC累積損益が整数の表現範囲を超えました", root); if (root is OperationCanceledException) throw new OperationCanceledException(token); throw; }
        }
        token.ThrowIfCancellationRequested();
        watch.Stop();
        progress?.Report(new(1, "Monte Carlo完了"));
        return new(dd, finals, useNative ? "C++20" : "C#", watch.Elapsed.TotalMilliseconds);
    }
    struct RandomState
    {
        uint state;
        internal RandomState(uint seed, int trial)
        {
            state = unchecked(seed + (uint)trial * 747796405u);
            if (state == 0)
                state = 0xA341316Cu;
        }
        uint Next()
        {
            unchecked
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                return state;
            }
        }
        internal uint Uniform(uint bound)
        {
            uint threshold = unchecked(0u - bound) % bound, x;
            do
            {
                x = Next();
            } while (x < threshold);
            return x % bound;
        }
    }
}
