using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ArmadaAnalyzer.Contracts;
namespace ArmadaAnalyzer;

public sealed partial class Workspace
{
    public string Root
    {
        get;
    }
    public string DatabasePath => Path.Combine(Root, "workspace.db");
    public Workspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        using var db = Open();
        db.Exec("PRAGMA page_size=32768; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE IF NOT EXISTS datasets(id INTEGER PRIMARY KEY,name TEXT NOT NULL,symbol TEXT NOT NULL,currency TEXT NOT NULL,timebasis TEXT NOT NULL,initial INTEGER NOT NULL,count INTEGER NOT NULL,hash TEXT NOT NULL,version INTEGER NOT NULL DEFAULT 1,warning TEXT NOT NULL,source TEXT NOT NULL,identity TEXT NOT NULL UNIQUE); CREATE TABLE IF NOT EXISTS trades(dataset INTEGER NOT NULL REFERENCES datasets(id),seq INTEGER NOT NULL,ticket INTEGER NOT NULL,part INTEGER NOT NULL,opened INTEGER NOT NULL,closed INTEGER NOT NULL,volume INTEGER NOT NULL,entry INTEGER NOT NULL,exit INTEGER NOT NULL,pnl INTEGER NOT NULL,side INTEGER NOT NULL,source_row INTEGER NOT NULL,PRIMARY KEY(dataset,seq),UNIQUE(dataset,ticket,part)) WITHOUT ROWID; CREATE INDEX IF NOT EXISTS trades_time ON trades(dataset,closed,seq); CREATE TABLE IF NOT EXISTS dataset_periods(dataset INTEGER PRIMARY KEY REFERENCES datasets(id) ON DELETE CASCADE,started INTEGER NOT NULL,ended INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS trade_price_profit(dataset INTEGER NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,seq INTEGER NOT NULL,profit INTEGER NOT NULL,PRIMARY KEY(dataset,seq)) WITHOUT ROWID; PRAGMA user_version=1;");
    }
    Db Open() => new(DatabasePath);
    public ImportResult ImportPlugin(string path, IReportReaderPlugin plugin, ImportOptions options, CancellationToken token = default, IProgress<WorkProgress>? progress = null) => ImportCore(path, options, token, progress, plugin);
    static Dataset DatasetRow(Db.Statement s) => new(s.Long(0), s.Text(1), s.Text(2), s.Text(3), s.Text(4), s.Long(5), s.Long(6), s.Text(7), s.Long(8), s.Text(9));
    const string DatasetColumns = "id,name,symbol,currency,timebasis,initial,count,hash,version,warning";
    public Dataset[] ListDatasets()
    {
        using var db = Open();
        using var s = db.Prepare("SELECT " + DatasetColumns + " FROM datasets ORDER BY id");
        var list = new List<Dataset>();
        while (s.Step())
            list.Add(DatasetRow(s));
        return list.ToArray();
    }
    public ImportResult Import(string path, ImportOptions options, CancellationToken token = default, IProgress<WorkProgress>? progress = null) => ImportCore(path, options, token, progress, null);
    ImportResult ImportCore(string path, ImportOptions options, CancellationToken token, IProgress<WorkProgress>? progress, IReportReaderPlugin? plugin)
    {
        options = options with
        {
            Currency = string.IsNullOrWhiteSpace(options.Currency) ? "不明" : options.Currency.Trim().ToUpperInvariant()
        };
        token.ThrowIfCancellationRequested();
        progress?.Report(new(0, "ファイルを検証中"));
        token.ThrowIfCancellationRequested();
        string hash;
        using (var f = File.OpenRead(path))
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1048576];
            int n;
            while ((n = f.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                h.AppendData(buffer, 0, n);
            }
            hash = Convert.ToHexString(h.GetHashAndReset());
        }
        var reportPeriod=plugin is null?BacktestPeriod.ReadHeader(path):null;
        Dictionary<string,string>? reportMetadata=null;
        if(plugin is null)try{reportMetadata=ReadReportMetadata(path);}catch(Exception e)when(e is IOException or FormatException or DecoderFallbackException){ }
        string identity = hash + "|" + options.TimeBasis + "|" + options.InitialBalance?.ToString(CultureInfo.InvariantCulture) + "|" + (plugin == null ? "parser1" : plugin.Metadata.Id + ":" + plugin.Metadata.Version);
        using var db = Open();
        db.Exec("BEGIN IMMEDIATE;");
        bool committed = false;
        try
        {
            using (var existing = db.Prepare("SELECT " + DatasetColumns + " FROM datasets WHERE identity=?1"))
            {
                existing.Bind(1, identity);
                if (existing.Step())
                {
                    var row = DatasetRow(existing);
                    db.Exec("ROLLBACK;");
                    committed = true;
                    SavePeriod(db,row.Id,reportPeriod);
                    if(reportMetadata!=null)SaveReportMetadata(db,row.Id,reportMetadata);
                    return new(row, true);
                }
            }
            using (var add = db.Prepare("INSERT INTO datasets(name,symbol,currency,timebasis,initial,count,hash,warning,source,identity) VALUES(?1,'不明',?2,?3,0,0,?4,'',?5,?6)"))
            {
                add.Bind(1, Path.GetFileNameWithoutExtension(path)).Bind(2, options.Currency).Bind(3, options.TimeBasis).Bind(4, hash).Bind(5, Path.GetFullPath(path)).Bind(6, identity);
                add.Step();
            }
            long id;
            using (var row = db.Prepare("SELECT last_insert_rowid()"))
            {
                row.Step();
                id = row.Long(0);
            }
            using var insert = db.Prepare("INSERT INTO trades VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12)");
            using var priceInsert=db.Prepare("INSERT INTO trade_price_profit VALUES(?1,?2,?3)");
            long firstOpened=long.MaxValue,lastClosed=long.MinValue;
            void Emit(Trade t)
            {
                firstOpened=Math.Min(firstOpened,t.OpenTicks);lastClosed=Math.Max(lastClosed,t.CloseTicks);
                insert.Bind(1, id).Bind(2, t.Sequence).Bind(3, t.Ticket).Bind(4, t.Part).Bind(5, t.OpenTicks).Bind(6, t.CloseTicks).Bind(7, t.Volume).Bind(8, t.EntryPrice).Bind(9, t.ExitPrice).Bind(10, t.Profit).Bind(11, t.Side).Bind(12, t.SourceRow);
                insert.Step();
                insert.Reset();
                if(t.PriceProfit is long priceProfit){priceInsert.Bind(1,id).Bind(2,t.Sequence).Bind(3,priceProfit).Step();priceInsert.Reset();}
            }
            ImportMetadata meta;
            if (plugin == null)
            {
                db.Exec("SAVEPOINT parse_input;");
                try
                {
                    meta = HtmlImporter.Parse(path, Emit, options, ResourceLimits.DefaultMemoryMiB, token, progress);
                }
                catch (DecoderFallbackException) { db.Exec("ROLLBACK TO parse_input;"); meta = HtmlImporter.Parse(path, Emit, options, ResourceLimits.DefaultMemoryMiB, token, progress, true); }
                db.Exec("RELEASE parse_input;");
            }
            else
            {
                using var source = File.OpenRead(path);
                var report = plugin.ReadReport(source, new(Path.GetFileName(path), options.Currency, options.TimeBasis), token);
                long count = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                foreach (var t in report.Trades)
                {
                    token.ThrowIfCancellationRequested();
                    if (t.OpenTicks < DateTime.MinValue.Ticks || t.CloseTicks > DateTime.MaxValue.Ticks || t.OpenTicks > t.CloseTicks || t.Volume <= 0 || t.EntryPrice < 0 || t.ExitPrice < 0 || t.Side is < 1 or > 2 || t.Part < 0 || t.Ticket < 0)
                        throw new FormatException("拡張パーサーから不正な取引が返されました");
                    Emit(new(count++, t.Ticket, t.Part, t.OpenTicks, t.CloseTicks, t.Volume, t.EntryPrice, t.ExitPrice, t.Profit, t.Side, 0, t.SourceRow));
                    if (watch.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new(Math.Min(0.99, (double)source.Position / Math.Max(1, source.Length)), $"{count:N0}取引を拡張パーサーで取込中"));
                        watch.Restart();
                    }
                }
                if (count == 0)
                    throw new FormatException("拡張パーサーの決済取引が0件です");
                meta = new(report.Info.Symbol, options.InitialBalance ?? report.Info.InitialBalance, count, 0, "");
            }
            using (var update = db.Prepare("UPDATE datasets SET symbol=?1,initial=?2,count=?3,warning=?4 WHERE id=?5"))
            {
                update.Bind(1, meta.Symbol).Bind(2, meta.Initial).Bind(3, meta.Closed).Bind(4, meta.Warning).Bind(5, id);
                update.Step();
            }
            token.ThrowIfCancellationRequested();
            SavePeriod(db,id,reportPeriod ?? (firstOpened<=lastClosed?new(firstOpened,lastClosed):null));
            if(plugin==null){reportMetadata??=new();reportMetadata["形式"]=meta.Format;}
            if(reportMetadata!=null)SaveReportMetadata(db,id,reportMetadata);
            db.Exec("COMMIT;");
            committed = true;
            progress?.Report(new(1, $"{meta.Closed:N0}取引を保存しました"));
            return new(new(id, Path.GetFileNameWithoutExtension(path), meta.Symbol, options.Currency, options.TimeBasis, meta.Initial, meta.Closed, hash, 1, meta.Warning), false);
        }
        finally { if (!committed) db.Exec("ROLLBACK;"); }
    }
    static string Where(Filter f)
    {
        if (f.DatasetIds.Length == 0)
            return "0";
        if (f.DatasetIds.Length > 512 || f.DatasetIds.Any(x => x <= 0) || f.Side is < 0 or > 2)
            throw new ArgumentException("絞込条件が不正です");
        return "dataset IN (" + string.Join(',', f.DatasetIds.Distinct().Order()) + ")" + (f.From is long a ? " AND closed>=" + a.ToString(CultureInfo.InvariantCulture) : "") + (f.To is long b ? " AND closed<" + b.ToString(CultureInfo.InvariantCulture) : "") + (f.Side != 0 ? " AND side=" + f.Side : "") + (f.Calendar?.Sql() ?? "");
    }
    public Snapshot LoadSeries(Filter filter, int memoryMiB = 256, CancellationToken token = default, Snapshot? reusable = null)
    {
        token.ThrowIfCancellationRequested();
        memoryMiB = ResourceLimits.MemoryMiB(memoryMiB);
        using var db = Open();
        db.Exec("BEGIN;");
        var meta = new List<Dataset>();
        using (var ds = db.Prepare("SELECT " + DatasetColumns + " FROM datasets WHERE id IN (" + (filter.DatasetIds.Length == 0 ? "0" : string.Join(',', filter.DatasetIds.Where(i => i > 0).Distinct().Order())) + ")"))
        {
            while (ds.Step())
                meta.Add(DatasetRow(ds));
        }
        if (filter.DatasetIds.Distinct().Count() != meta.Count)
            throw new InvalidOperationException("選択したデータが存在しません");
        var pipFactors = meta.ToDictionary(d=>d.Id,d=>DukascopyPips.Size(d.Symbol) is decimal size ? (int)(1m/size) : 0);
        var keyText = "schema1/parser1/columns2/" + Where(filter) + "/" + string.Join(';', meta.Select(d => $"{d.Id}:{d.Hash}:{d.Version}")) + "/" + string.Join(';', (filter.Weights ?? []).OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value.ToString(CultureInfo.InvariantCulture)}"));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyText)));
        if (reusable != null && reusable.Key == key)
        {
            if ((long)reusable.Points.Length * 64 + 16777216 > memoryMiB * 1048576L)
                throw new InvalidOperationException("分析メモリ予算を超えます。期間または戦略を絞り込んでください。");
            token.ThrowIfCancellationRequested();
            return new(key, reusable.Points, true) { PipFactors=pipFactors };
        }
        string cache = Path.Combine(Root, "cache");
        // The cache key includes immutable dataset hashes, metadata versions and all filters.
        // Verify the keyed payload and budget before rescanning the DB for a row count.
        var hit = ColumnCache.Read(cache, key, memoryMiB, token);
        if (hit != null)
            return new(key, hit, true) { PipFactors=pipFactors };
        using var ct = db.Prepare("SELECT count(*) FROM trades WHERE " + Where(filter));
        ct.Step();
        long count = ct.Long(0);
        if (count > int.MaxValue || count * 64 + 16777216 > memoryMiB * 1048576L)
            throw new InvalidOperationException("分析メモリ予算を超えます。期間または戦略を絞り込んでください。");
        var points = new SeriesPoint[(int)count];
        using (var s = db.Prepare("SELECT closed,pnl,dataset,entry,exit,side FROM trades WHERE " + Where(filter) + " ORDER BY closed,dataset,seq"))
        {
            int i = 0;
            while (s.Step())
            {
                if ((i & 4095) == 0)
                    token.ThrowIfCancellationRequested();
                long dataset = s.Long(2), profit = s.Long(1);
                decimal weight = filter.Weights?.GetValueOrDefault(dataset, 1m) ?? 1m;
                if (weight < 0 || weight > 1000)
                    throw new ArgumentException("ウェイトは0〜1000です");
                long weighted = checked((long)decimal.Round(profit * weight, 0, MidpointRounding.ToEven));
                long entry=s.Long(3),exit=s.Long(4),side=s.Long(5);
                long move=side==1 ? checked(exit-entry) : side==2 ? checked(entry-exit) : long.MinValue;
                points[i++] = new(s.Long(0), weighted, dataset,move);
            }
        }
        db.Exec("COMMIT;");
        token.ThrowIfCancellationRequested();
        try
        {
            ColumnCache.Write(cache, key, points, token);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new(key, points, false) { PipFactors=pipFactors };
    }
    public Trade[] ReadPage(Filter filter, int offset, int limit = 500)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        limit = Math.Clamp(limit, 1, 500);
        using var db = Open();
        using var s = db.Prepare("SELECT seq,ticket,part,opened,closed,volume,entry,exit,pnl,side,dataset,source_row FROM trades WHERE " + Where(filter) + " ORDER BY closed,dataset,seq LIMIT ?1 OFFSET ?2");
        s.Bind(1, limit).Bind(2, offset);
        var rows = new List<Trade>(limit);
        while (s.Step())
            rows.Add(new(s.Long(0), s.Long(1), (int)s.Long(2), s.Long(3), s.Long(4), s.Long(5), s.Long(6), s.Long(7), s.Long(8), (int)s.Long(9), s.Long(10), s.Long(11)));
        return rows.ToArray();
    }
    public void SetCurrency(long id, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("通貨が空です");
        using var db = Open();
        using var s = db.Prepare("UPDATE datasets SET currency=?1,version=version+1 WHERE id=?2");
        s.Bind(1, currency.Trim()).Bind(2, id);
        s.Step();
    }
    public void ExportCsv(Filter filter, string file, CancellationToken token = default)
    {
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var meta = ListDatasets().ToDictionary(x => x.Id);
        string Q(string s) => "\"" + (s.Length > 0 && "=+-@".Contains(s[0]) ? "'" : "") + s.Replace("\"", "\"\"") + "\"";
        try
        {
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("Dataset,Symbol,Ticket,Part,OpenTime_Broker,CloseTime_Broker,Side,Volume,EntryPrice,ExitPrice,Profit,Weight,WeightedProfit");
                using var db = Open();
                using var s = db.Prepare("SELECT dataset,ticket,part,opened,closed,volume,entry,exit,pnl,side FROM trades WHERE " + Where(filter) + " ORDER BY closed,dataset,seq");
                int n = 0;
                while (s.Step())
                {
                    if ((n++ & 4095) == 0)
                        token.ThrowIfCancellationRequested();
                    long id = s.Long(0), pnl = s.Long(8);
                    decimal weight = filter.Weights?.GetValueOrDefault(id, 1m) ?? 1m;
                    var d = meta[id];
                    writer.WriteLine(string.Join(',', Q(d.Name), Q(d.Symbol), s.Long(1).ToString(CultureInfo.InvariantCulture), s.Long(2).ToString(CultureInfo.InvariantCulture), new DateTime(s.Long(3)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), new DateTime(s.Long(4)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), s.Long(9) == 1 ? "buy" : "sell", Amount.Exact(s.Long(5)), Amount.Exact(s.Long(6)), Amount.Exact(s.Long(7)), Amount.Exact(pnl), weight.ToString(CultureInfo.InvariantCulture), Amount.Exact(checked((long)decimal.Round(pnl * weight, 0, MidpointRounding.ToEven)))));
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
