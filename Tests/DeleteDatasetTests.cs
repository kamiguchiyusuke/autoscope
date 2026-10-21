using ArmadaAnalyzer;
static class DeleteDatasetTests
{
    static void InWorkspace(Action<Workspace,string> test)
    {
        string root=Path.Combine(Path.GetTempPath(),"scope-delete-"+Guid.NewGuid().ToString("N"));
        try{test(new Workspace(root),root);}
        finally
        {
            // This unique directory is owned exclusively by this test.
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
    public static void Run()
    {
        Program.Test("delete_datasets_atomic_preserves_source_and_survivors",()=>InWorkspace((w,root)=>{
            string a=DemoReports.Create(root),b=DemoReports.Create(root,seed:79,symbol:"GBPUSD");
            var first=w.Import(a,new()).Dataset;var second=w.Import(b,new()).Dataset;
            var original=w.LoadSeries(new([second.Id])).Points;
            string cache=Path.Combine(root,"cache");
            string generated=Path.Combine(cache,new string('A',64)+".col");
            string slippage=Path.Combine(cache,"slippage-v1-"+new string('B',64)+".json");
            string stale=generated+"."+Guid.NewGuid().ToString("N")+".tmp";
            string recent=generated+"."+Guid.NewGuid().ToString("N")+".tmp";
            string unknown=Path.Combine(cache,"user-notes.tmp"),unknownColumn=Path.Combine(cache,"saved.col");
            foreach(string path in new[]{generated,slippage,stale,recent,unknown,unknownColumn})File.WriteAllText(path,"keep or reclaim");
            File.SetLastWriteTimeUtc(stale,DateTime.UtcNow.AddHours(-2));
            // Even a source report with a generated-looking filename must survive.
            string disguisedSource=Path.Combine(cache,new string('C',64)+".col");
            w.DeleteDatasets([first.Id]);
            File.Move(a,disguisedSource);a=disguisedSource;
            first=w.Import(a,new()).Dataset;
            foreach(string path in new[]{generated,slippage,stale})File.WriteAllText(path,"reclaim");
            File.SetLastWriteTimeUtc(stale,DateTime.UtcNow.AddHours(-2));
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            Program.Throws<OperationCanceledException>(()=>w.DeleteDatasets([first.Id],cancel.Token));
            Program.Eq(2,w.ListDatasets().Length);
            Program.Assert(File.Exists(generated)&&File.Exists(slippage)&&File.Exists(stale),"cancellation preserves caches");
            w.DeleteDatasets([first.Id,first.Id]);Program.Eq(1,w.ListDatasets().Length);
            Program.Eq(0,w.ReadPage(new([first.Id]),0).Length);
            Program.Assert(original.SequenceEqual(w.LoadSeries(new([second.Id])).Points),"survivor calculations preserved");
            Program.Assert(!File.Exists(generated)&&!File.Exists(slippage)&&!File.Exists(stale),"generated caches reclaimed");
            Program.Assert(File.Exists(recent)&&File.Exists(unknown)&&File.Exists(unknownColumn),"active temporary and unknown files preserved");
            Program.Assert(File.Exists(a)&&File.Exists(b),"original reports preserved");
            Program.Assert(!w.Import(a,new()).AlreadyImported,"deleted report can be reimported");
            w.DeleteDatasets(w.ListDatasets().Select(d=>d.Id).ToArray());Program.Eq(0,w.ListDatasets().Length);
            Program.Assert(File.Exists(a)&&File.Exists(b),"reset preserves original reports");
        }));
        Program.Test("delete_reclaims_large_database_and_preserves_survivor",()=>InWorkspace((w,root)=>{
            string large=DemoReports.Create(root,count:160000,minutesPerTrade:5);
            var first=w.Import(large,new()).Dataset;
            var survivor=w.Import(DemoReports.Create(root,count:50,seed:79),new()).Dataset;
            var expected=w.ReadPage(new([survivor.Id]),0);
            long before=new FileInfo(w.DatabasePath).Length;
            w.DeleteDatasets([first.Id]);
            long after=new FileInfo(w.DatabasePath).Length;
            Program.Assert(after<before/2,$"database reclaimed: {before} -> {after}");
            Program.Assert(expected.SequenceEqual(w.ReadPage(new([survivor.Id]),0)),"surviving trades unchanged after vacuum");
            Program.Assert(File.Exists(large),"large source preserved");
            w.DeleteDatasets([survivor.Id]);
            Program.Assert(new FileInfo(w.DatabasePath).Length<=after,"reset retains compact database");
        }));
        Program.Test("delete_commits_when_cache_file_is_locked",()=>InWorkspace((w,root)=>{
            var dataset=w.Import(DemoReports.Create(root),new()).Dataset;
            w.LoadSeries(new([dataset.Id]));
            string file=Directory.GetFiles(Path.Combine(root,"cache"),"*.col").Single();
            using(var locked=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                w.DeleteDatasets([dataset.Id]);
                Program.Eq(0,w.ListDatasets().Length);
            }
        }));
    }
}
