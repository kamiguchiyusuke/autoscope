using System.Globalization;
using System.Text.RegularExpressions;
namespace MT4BatchReporter;

public static class JobNaming
{
    public static void Apply(TestJob job,string ea,string symbol,IEnumerable<TestJob> jobs,string? previousSymbol=null)
    {
        string oldEa=Path.GetFileNameWithoutExtension(job.EaFile);
        string oldSymbol=previousSymbol??job.Symbol;
        string unsuffixed=Regex.Replace(job.Name,@"（[2-9][0-9]*）$|（1[0-9]+）$","");
        bool legacy=oldEa.Length>0 && unsuffixed.StartsWith(oldEa+"_",StringComparison.Ordinal) &&
            DateTime.TryParseExact(unsuffixed[(oldEa.Length+1)..],"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _);
        bool generated=string.IsNullOrWhiteSpace(job.Name)||Regex.IsMatch(job.Name,@"^テスト(?:\s*[0-9０-９]+)?(?:\s*コピー)*$")||
            (oldEa.Length>0 && (unsuffixed==oldEa+"_"+oldSymbol || unsuffixed==oldEa+"_"+symbol))||legacy;
        job.EaFile=ea;job.Symbol=symbol;
        if(!generated||string.IsNullOrWhiteSpace(ea)||string.IsNullOrWhiteSpace(symbol))return;
        string basis=Path.GetFileNameWithoutExtension(ea)+"_"+symbol,name=basis;int suffix=2;
        while(jobs.Any(x=>x!=job&&string.Equals(x.Name,name,StringComparison.OrdinalIgnoreCase)))name=basis+"（"+(suffix++)+"）";
        job.Name=name;
    }
}
