using ArmadaAnalyzer;
static class MCTests
{
    public static void Run()
    {
        Program.Test("portfolio_correlation_uses_union_and_zero_fills_missing_days", () => { long day = new DateTime(2020, 1, 1).Ticks; var disjoint = Portfolio.Correlations([new(day, 100, 1), new(day + 2 * TimeSpan.TicksPerDay, 200, 2)]); Program.Eq(3, disjoint[0].Days); Program.Assert(Math.Abs(disjoint[0].Value!.Value + 0.5) < 1e-12, "union zero filling is wrong"); var constant = Portfolio.Correlations([new(day, 1, 1), new(day, 2, 2), new(day + TimeSpan.TicksPerDay, 1, 1), new(day + TimeSpan.TicksPerDay, 2, 2)]); Program.Assert(constant[0].Value == null, "constant correlation must be undefined"); });
        Program.Test("mc_known_peak_and_native_parity", () => { foreach (int method in new[] { 0, 1, 2 }) { long[] p = [100, 100, -50]; var o = new MCOptions(300, 3, method, 15, 42, 1); var a = MonteCarlo.Run(p, o, native: false); var b = MonteCarlo.Run(p, o with { Workers = 3 }, native: true); Program.Assert(a.Drawdowns.SequenceEqual(b.Drawdowns), "DD mismatch"); Program.Assert(a.Finals.SequenceEqual(b.Finals), "final PNL mismatch"); if (method == 1) { Program.Assert(a.Finals.All(x => x == 150), "shuffle altered total"); Program.Assert(a.Drawdowns.All(x => x == 50), "bad cumulative peak"); } } });
        Program.Test("mc_budget_accounts_for_expanded_price_snapshot",()=>Program.Throws<InvalidOperationException>(()=>MonteCarlo.Run(new long[1000000],new(Trials:1,Steps:1,MemoryMiB:64))));
        Program.Test("mc_cancel_and_overflow", () => { using var c = new CancellationTokenSource(); c.Cancel(); Program.Throws<OperationCanceledException>(() => MonteCarlo.Run([-100], new(), c.Token, native: true)); Program.Throws<OverflowException>(() => MonteCarlo.Run([long.MaxValue], new(2, 2), native: true)); Program.Throws<ArgumentException>(() => MonteCarlo.Run([], new(), native: true)); });
    }
}
