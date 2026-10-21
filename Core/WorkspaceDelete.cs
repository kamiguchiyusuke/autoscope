namespace ArmadaAnalyzer;
public sealed partial class Workspace
{
    public void DeleteDatasets(long[] ids,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        using var db=Open();db.Exec("BEGIN IMMEDIATE;");bool committed=false;
        bool changed=false;
        var sources=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using(var source=db.Prepare("SELECT source FROM datasets"))
                while(source.Step())sources.Add(source.Text(0));
            using var trades=db.Prepare("DELETE FROM trades WHERE dataset=?");
            using var datasets=db.Prepare("DELETE FROM datasets WHERE id=?");
            foreach(long id in ids.Distinct())
            {
                token.ThrowIfCancellationRequested();
                trades.Bind(1,id).Step();trades.Reset();
                datasets.Bind(1,id).Step();datasets.Reset();
                using var count=db.Prepare("SELECT changes()");count.Step();changed|=count.Long(0)>0;
            }
            token.ThrowIfCancellationRequested();db.Exec("COMMIT;");committed=true;
        }
        finally {if(!committed)db.Exec("ROLLBACK;");}
        if(changed)ReclaimDeletedData(db,sources);
    }
}
