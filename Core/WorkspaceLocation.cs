namespace ArmadaAnalyzer;

public static class WorkspaceLocation
{
    public static string ResolveDefault(string applicationDirectory,string localApplicationData)
    {
        string legacy=Path.Combine(applicationDirectory,"Workspaces","Default");
        if(Directory.Exists(legacy))return legacy;
        if(!Path.IsPathFullyQualified(localApplicationData))throw new ArgumentException("ユーザーデータの保存先を確認できません。");
        return Path.Combine(localApplicationData,"ArmadaAnalyzer","Workspaces","Default");
    }
}
