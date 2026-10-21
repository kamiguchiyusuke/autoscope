using System.Globalization;
using System.Text;
namespace ArmadaAnalyzer;

public static class DemoReports
{
    public static string Create(string folder, int count = 1200, int seed = 42, string symbol = "EURUSD", int minutesPerTrade = 480)
    {
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"サンプル_{symbol}_{seed}.htm");
        using var w = new StreamWriter(file, false, new UTF8Encoding(true));
        var random = new Random(seed);
        decimal balance = 1000000;
        w.WriteLine($"<html><meta charset=\"utf-8\"><body><table><tr><td>通貨ペア</td><td>{symbol}</td></tr><tr><td>初期証拠金</td><td>1000000</td></tr></table><table>");
        for (int i = 0; i < count; i++)
        {
            var opened = new DateTime(2020, 1, 1).AddMinutes((long)i * minutesPerTrade);
            var closed = opened.AddHours(3);
            decimal profit = random.Next(-12000, 15001) / 100m;
            if (i % 37 == 0)
                profit = 0;
            balance += profit;
            decimal entry = 1.1m, exit = entry + profit / 10000m;
            string T(DateTime date) => date.ToString("yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture);
            string N(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
            w.WriteLine($"<tr><td>{i * 2 + 1}</td><td>{T(opened)}</td><td>buy</td><td>{i + 1}</td><td>0.1</td><td>{N(entry)}</td><td>0</td><td>0</td><td colspan=2></td></tr>");
            w.WriteLine($"<tr><td>{i * 2 + 2}</td><td>{T(closed)}</td><td>close</td><td>{i + 1}</td><td>0.1</td><td>{N(exit)}</td><td>0</td><td>0</td><td>{N(profit)}</td><td>{N(balance)}</td></tr>");
        }
        w.WriteLine("</table></body></html>");
        return file;
    }
}
