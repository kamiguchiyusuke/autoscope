using System.Text.RegularExpressions;
namespace ArmadaAnalyzer;

public sealed partial class Workspace
{
    // Cache keys are hashes, so invalidate generated caches after deletion. Survivors
    // rebuild them on demand. Only our exact file naming formats are eligible.
    static readonly Regex GeneratedCacheName=new(@"\A(?:[0-9A-Fa-f]{64}\.col|slippage-v1-[0-9A-Fa-f]{64}\.json)(?:\.[0-9A-Fa-f]{32}\.tmp)?\z",RegexOptions.CultureInvariant);

    static bool HasReparseAncestor(string path)
    {
        for(DirectoryInfo? directory=new(path);directory!=null;directory=directory.Parent)
            if((directory.Attributes&FileAttributes.ReparsePoint)!=0)return true;
        return false;
    }

    void ReclaimDeletedData(Db db,HashSet<string> sources)
    {
        try
        {
            string cache=Path.Combine(Root,"cache");
            if(Directory.Exists(cache)&&!HasReparseAncestor(cache))
                foreach(string file in Directory.EnumerateFiles(cache))
                {
                    if(!GeneratedCacheName.IsMatch(Path.GetFileName(file))||sources.Contains(file))continue;
                    try
                    {
                        if((File.GetAttributes(file)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)continue;
                        // A recent temporary file may belong to a running cache writer.
                        if(file.EndsWith(".tmp",StringComparison.Ordinal)&&File.GetLastWriteTimeUtc(file)>DateTime.UtcNow.AddHours(-1))continue;
                        File.Delete(file);
                    }
                    catch(Exception e)when(e is IOException or UnauthorizedAccessException){ }
                }
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){ }

        // Maintenance must never report a committed deletion as failed. Keep lock
        // waits short and leave reusable free pages in place when another reader is busy.
        try
        {
            if(HasReparseAncestor(Root)||(File.GetAttributes(DatabasePath)&FileAttributes.ReparsePoint)!=0)return;
            db.Exec("PRAGMA busy_timeout=100;");
            long Scalar(string sql){using var s=db.Prepare(sql);s.Step();return s.Long(0);}
            long free=Scalar("PRAGMA freelist_count"),pages=Scalar("PRAGMA page_count"),size=Scalar("PRAGMA page_size");
            if(free>0&&(Scalar("SELECT count(*) FROM datasets")==0||(free*size>=8L*1024*1024&&free>=pages/4)))
            {
                db.Exec("VACUUM;");
                db.Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            }
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){ }
    }
}
