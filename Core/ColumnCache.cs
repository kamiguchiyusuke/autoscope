using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace ArmadaAnalyzer;

internal static class ColumnCache
{
    const long Header = 48;
    internal static SeriesPoint[]? Read(string root, string key, int budget, CancellationToken token)
    {
        string file = Path.Combine(root, key + ".col");
        if (!File.Exists(file))
            return null;
        try
        {
            using var s = File.OpenRead(file);
            using var r = new BinaryReader(s, Encoding.UTF8, true);
            if (Encoding.ASCII.GetString(r.ReadBytes(8)) != "ARMADA02")
                return null;
            int n = r.ReadInt32(), size = r.ReadInt32();
            if (n < 0 || size != 32 || s.Length != Header + 32L * n + 32 || 64L * n + 16777216 > (long)budget * 1048576 || !r.ReadBytes(32).AsSpan().SequenceEqual(Convert.FromHexString(key)))
                return null;
            token.ThrowIfCancellationRequested();
            var data = new SeriesPoint[n];
            var bytes = MemoryMarshal.AsBytes(data.AsSpan());
            s.ReadExactly(bytes);
            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(r.ReadBytes(32)))
                return null;
            token.ThrowIfCancellationRequested();
            return data;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    internal static void Write(string root, string key, SeriesPoint[] points, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, key + ".col"), temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            using (var s = File.Create(temp))
            {
                using var w = new BinaryWriter(s, Encoding.UTF8, true);
                w.Write(Encoding.ASCII.GetBytes("ARMADA02"));
                w.Write(points.Length);
                w.Write(32);
                w.Write(Convert.FromHexString(key));
                var bytes = MemoryMarshal.AsBytes(points.AsSpan());
                s.Write(bytes);
                w.Write(SHA256.HashData(bytes));
                s.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, file, true);
            long used = 0;
            foreach (var f in new DirectoryInfo(root).GetFiles("*.col").OrderByDescending(x => x.LastWriteTimeUtc))
            {
                used += f.Length;
                if (used > 536870912 && f.FullName != file)
                    try
                    {
                        f.Delete();
                    }
                    catch (IOException) { }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
