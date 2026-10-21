using MT4BatchReporter;
static class JobNamingChecks
{
    public static void Run()
    {
        void Check(bool ok,string name){if(!ok)throw new Exception("Naming: "+name);Console.WriteLine("PASS Naming "+name);}
        var a=new TestJob{Name="テスト 1",Symbol="USDJPY"};var b=new TestJob{Name="",Symbol="USDJPY"};var jobs=new[]{a,b};
        JobNaming.Apply(a,"sample.ex4","USDJPY",jobs);JobNaming.Apply(b,"sample.ex4","USDJPY",jobs);
        Check(a.Name=="sample_USDJPY"&&b.Name=="sample_USDJPY（2）","pair and unique suffix");
        JobNaming.Apply(a,"sample.ex4","EURUSD.raw",jobs);Check(a.Name=="sample_EURUSD.raw","pair change");
        JobNaming.Apply(a,"next.ex5","EURUSD.raw",jobs);Check(a.Name=="next_EURUSD.raw","EA change");
        a.Name="自分の名前";JobNaming.Apply(a,"third.ex5","USDJPY",jobs);Check(a.Name=="自分の名前","manual name preserved");
        a.Name="third_20261019（2）";JobNaming.Apply(a,a.EaFile,a.Symbol,jobs);Check(a.Name=="third_USDJPY","legacy date name migrated");
        a.Name="third_20261399";JobNaming.Apply(a,a.EaFile,a.Symbol,jobs);Check(a.Name=="third_20261399","invalid date not treated as generated");
        var copy=b.Clone();JobNaming.Apply(copy,copy.EaFile,"GBPJPY",jobs);Check(copy.Name=="sample_GBPJPY","saved name follows pair");
    }
}
