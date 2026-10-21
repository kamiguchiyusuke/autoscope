using System.Security.Cryptography;
using System.Text.Json;
namespace ArmadaAnalyzer;

public sealed record ReportMetadata(long DatasetId,string Name,Dictionary<string,string> Fields);
public sealed partial class Workspace
{
    internal static Dictionary<string,string> ReadReportMetadata(string path)
    {
        var fields=new Dictionary<string,string>();
        using var rows=new HtmlRows(path);Span<Range> cells=stackalloc Range[64];
        for(int rowNumber=0;rowNumber<512 && rows.Next(out var row);rowNumber++)
        {
            int n=HtmlImporter.Cells(row,cells);
            if(Mt5HtmlImporter.IsHeader(row))fields["形式"]="MT5";
            else if(n>=9 && HtmlImporter.Clean(row[cells[2]]).ToLowerInvariant() is "buy" or "sell")fields.TryAdd("形式","MT4");
            if(row.Contains("Tick Data Suite",StringComparison.OrdinalIgnoreCase))fields["TDS"]="使用";
            for(int i=0;i<n-1;i++)
            {
                string key=HtmlImporter.Clean(row[cells[i]]).Trim().TrimEnd(':','：').ToLowerInvariant();
                string? label=key switch{
                    "expert" or "expert advisor" or "エキスパート"=>"EA",
                    "symbol" or "通貨ペア" or "銘柄"=>"通貨",
                    "period" or "期間" or "テスト期間"=>"期間",
                    "spread" or "スプレッド"=>"スプレッド",
                    "modeling quality" or "モデリング品質"=>"モデリング品質",
                    "history quality" or "ヒストリー品質"=>"履歴品質",
                    "model" or "モデル"=>"モデル",
                    "initial deposit" or "初期証拠金" or "初期入金"=>"初期証拠金",
                    "leverage" or "レバレッジ"=>"レバレッジ",
                    "lot" or "lots" or "ロット"=>"ロット",
                    "money management" or "資金管理設定"=>"資金管理設定",
                    "不利ポジション設定"=>"不利ポジション設定",
                    _=>null};
                if(label==null)continue;
                string value=HtmlImporter.Clean(row[cells[i+1]]).Trim();
                if(value.Length>0)fields.TryAdd(label,value.Length>2000?value[..2000]:value);
            }
        }
        if(fields.ContainsKey("履歴品質"))fields.TryAdd("形式","MT5");
        else if(fields.ContainsKey("モデリング品質"))fields.TryAdd("形式","MT4");
        if(fields.GetValueOrDefault("通貨","").Split(' ')[0].EndsWith("_TDS_UTC",StringComparison.OrdinalIgnoreCase))fields["TDS"]="使用";
        return fields;
    }
    static void SaveReportMetadata(Db db,long id,Dictionary<string,string> fields)
    {
        db.Exec("CREATE TABLE IF NOT EXISTS report_metadata(dataset INTEGER PRIMARY KEY REFERENCES datasets(id) ON DELETE CASCADE,json TEXT NOT NULL)");
        using var insert=db.Prepare("INSERT OR REPLACE INTO report_metadata(dataset,json) VALUES(?1,?2)");
        insert.Bind(1,id).Bind(2,JsonSerializer.Serialize(fields)).Step();
    }
    public void SetReportOrigin(long id,string format,bool tds)
    {
        if(format is not ("MT4" or "MT5"))throw new ArgumentException("レポート形式が不明です");
        var metadata=LoadReportMetadata(new([id])).Single();
        metadata.Fields["形式"]=format;
        if(tds)metadata.Fields["TDS"]="使用";else metadata.Fields.Remove("TDS");
        using var db=Open();SaveReportMetadata(db,id,metadata.Fields);
    }
    public ReportMetadata[] LoadReportMetadata(Filter filter,CancellationToken token=default)
    {
        using var db=Open();db.Exec("CREATE TABLE IF NOT EXISTS report_metadata(dataset INTEGER PRIMARY KEY REFERENCES datasets(id) ON DELETE CASCADE,json TEXT NOT NULL)");
        var result=new List<ReportMetadata>();
        foreach(long id in filter.DatasetIds.Distinct())
        {
            token.ThrowIfCancellationRequested();
            using var query=db.Prepare("SELECT d.name,d.source,d.hash,m.json FROM datasets d LEFT JOIN report_metadata m ON m.dataset=d.id WHERE d.id=?1");
            query.Bind(1,id);if(!query.Step())continue;
            string name=query.Text(0),path=query.Text(1),hash=query.Text(2),json=query.Text(3);
            Dictionary<string,string>? fields=null;
            if(!string.IsNullOrWhiteSpace(json))fields=JsonSerializer.Deserialize<Dictionary<string,string>>(json);
            if(fields==null || !fields.ContainsKey("形式"))try{
                using var file=File.OpenRead(path);using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer=new byte[65536];int count;while((count=file.Read(buffer))>0){token.ThrowIfCancellationRequested();sha.AppendData(buffer,0,count);}
                if(Convert.ToHexString(sha.GetHashAndReset())==hash){var fresh=ReadReportMetadata(path);fields??=new();foreach(var pair in fresh)fields.TryAdd(pair.Key,pair.Value);SaveReportMetadata(db,id,fields);}
            }catch(Exception e)when(e is IOException or UnauthorizedAccessException or FormatException or System.Text.DecoderFallbackException){ }
            result.Add(new(id,name,fields??new()));
        }
        return result.ToArray();
    }
}
