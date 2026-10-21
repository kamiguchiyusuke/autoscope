using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
namespace MT4BatchReporter;
public static class ReportInspector
{
    private static readonly TimeSpan Limit=TimeSpan.FromSeconds(3);
    private static MatchCollection Matches(string text,string pattern)=>Regex.Matches(text,pattern,RegexOptions.IgnoreCase|RegexOptions.Singleline,Limit);
    public static ReportAssessment Inspect(string path,TestJob requested,string expectedServer,bool mt5=false)
    {
        try
        {
            var bytes=File.ReadAllBytes(path);
            if(bytes.Length>100*1024*1024) return new(){Outcome=ReportOutcome.Unknown,Warnings=["レポートが大きすぎます。"]};
            string html;
            using(var r=new StreamReader(new MemoryStream(bytes),Encoding.GetEncoding(932),true))html=r.ReadToEnd();
            var fields=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(Match row in Matches(html,@"<tr\b[^>]*>(.*?)</tr>"))
            {
                var cells=Matches(row.Groups[1].Value,@"<td\b[^>]*>(.*?)</td>").Select(m=>WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value,"<[^>]+>","",RegexOptions.None,Limit)).Trim()).ToArray();
                for(int i=0;i+1<cells.Length;i+=2)fields.TryAdd(mt5?cells[i].TrimEnd(':','：'):cells[i],cells[i+1]);
            }
            string Field(params string[] keys)=>keys.Select(k=>fields.GetValueOrDefault(k)).FirstOrDefault(x=>x!=null)??"";
            long? Number(params string[] keys)=>long.TryParse(Field(keys).Replace(" ","").Replace("\u00a0",""),NumberStyles.AllowThousands,CultureInfo.InvariantCulture,out long n)?n:null;
            var server=Matches(html,"<meta\\b[^>]*name=['\"]server['\"][^>]*content=['\"]([^'\"]*)['\"]").FirstOrDefault()?.Groups[1].Value??"";
            var bars=Number("テストバー数","Bars in test","バー","Bars");var ticks=Number("モデルティック数","Ticks modelled","Ticks modeled","ティック","Ticks");var trades=Number("総取引数","Total trades","取引数","Total Trades");
            decimal? profit=decimal.TryParse(Field("純益","Total net profit","総損益","Total Net Profit").Replace(" ","").Replace("\u00a0",""),NumberStyles.Number,CultureInfo.InvariantCulture,out var v)?v:null;
            var dates=Matches(Field("期間","Period"),@"\d{4}\.\d{2}\.\d{2}\s+\d{2}:\d{2}").Select(m=>DateTime.TryParseExact(m.Value,"yyyy.MM.dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var d)?(DateTime?)d:null).ToArray();
            if(mt5)dates=Matches(Field("期間","Period"),@"\d{4}\.\d{2}\.\d{2}").Select(m=>DateTime.TryParseExact(m.Value,"yyyy.MM.dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var d)?(DateTime?)d:null).ToArray();
            DateTime? from=dates.ElementAtOrDefault(0),to=dates.ElementAtOrDefault(1);
            var warnings=new List<string>();bool invalid=false;
            if(bars<=0||ticks<=0){invalid=true;warnings.Add("バー数またはティック数が0です。履歴データを確認してください。");}
            if(from.HasValue&&to.HasValue&&(from.Value.Year<2000||from>to||from<requested.From.Date||to>=requested.To.Date.AddDays(1))) {invalid=true;warnings.Add("実際のテスト期間が指定期間と一致しません。");}
            if(expectedServer.Length>0&&!server.Equals(expectedServer,StringComparison.OrdinalIgnoreCase)) warnings.Add($"サーバー不一致：指定 {expectedServer} / 実際 {server}");
            bool unknown=bars==null||ticks==null||trades==null||profit==null||from==null||to==null;
            if(unknown)warnings.Add("必要な結果項目を読み取れませんでした。");
            return new(){Server=server,Bars=bars,Ticks=ticks,Trades=trades,Profit=profit,ActualFrom=from,ActualTo=to,Warnings=warnings,Outcome=invalid?ReportOutcome.Invalid:unknown?ReportOutcome.Unknown:ReportOutcome.Valid};
        }
        catch(Exception e) when(e is IOException or DecoderFallbackException or RegexMatchTimeoutException or ArgumentException)
        { return new(){Outcome=ReportOutcome.Unknown,Warnings=["レポート内容を確認できません："+e.Message]}; }
    }
}
