namespace ArmadaAnalyzer;

public sealed record ReportHeaderSummary(string Currency,string Platform,string Spread,string Quality)
{
    public static ReportHeaderSummary Create(IEnumerable<string> symbols,IEnumerable<ReportMetadata> metadata)
    {
        var currencies=symbols.Select(s=>s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var reports=metadata.ToArray();
        string Shared(IEnumerable<string> values){var distinct=values.Distinct().ToArray();return distinct.Length==0?"記載なし":string.Join(" / ",distinct);}
        bool Tds(ReportMetadata m)=>m.Fields.GetValueOrDefault("TDS")=="使用" || m.Fields.GetValueOrDefault("通貨","").Split(' ')[0].EndsWith("_TDS_UTC",StringComparison.OrdinalIgnoreCase);
        var platforms=reports.Select(m=>m.Fields.GetValueOrDefault("形式","記載なし")).Distinct().ToArray();
        string platform=platforms.Contains("MT4")&&platforms.Contains("MT5")?"混同":Shared(platforms);
        return new(currencies.Length>1?"複数":currencies.FirstOrDefault()??"不明",platform,
            Shared(reports.Select(m=>Tds(m)?"変動":m.Fields.GetValueOrDefault("スプレッド","記載なし"))),
            Shared(reports.Select(m=>Tds(m)?"99.90%":m.Fields.GetValueOrDefault("モデリング品質",m.Fields.GetValueOrDefault("履歴品質","記載なし")))));
    }
}
