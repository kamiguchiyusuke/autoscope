using System.Text;
using System.Text.RegularExpressions;
namespace MT4BatchReporter;

// A chart-free startup profile avoids loading the user's indicators for a
// tester-only job. The exact original selection is carried by recovery.json.
public sealed class Mt4Profile
{
    public string Name { get; set; } = "";
    public byte[]? Original { get; set; }
    private const string Marker=".autoscope-profile";
    private string Folder(string data)
    {
        if(!Regex.IsMatch(Name,@"\AAutoScopeBatch-[0-9a-f]{32}\z"))throw new InvalidDataException("テスト用プロファイル名が不正です。");
        string root=Path.Combine(data,"profiles");
        for(var p=new DirectoryInfo(root);p!=null;p=p.Parent)
            if(p.Exists && p.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new InvalidDataException("リンクされたプロファイルは変更しません。");
        return Path.Combine(root,Name);
    }
    public static Mt4Profile Capture(string data)
    {
        var state=new Mt4Profile{Name="AutoScopeBatch-"+Guid.NewGuid().ToString("N")};
        state.Folder(data);
        string last=Path.Combine(data,"profiles","lastprofile.ini");
        if(File.Exists(last))
        {
            if(new FileInfo(last).Length>65536 || File.GetAttributes(last).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("元プロファイルの設定を確認できません。");
            state.Original=File.ReadAllBytes(last);
        }
        return state;
    }
    public void Begin(string data)
    {
        string folder=Folder(data);
        if(Directory.Exists(folder))throw new IOException("テスト用プロファイルが既に存在します。");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,Marker),Name,Encoding.ASCII);
    }
    public void Restore(string data)
    {
        string folder=Folder(data),last=Path.Combine(data,"profiles","lastprofile.ini");
        if(File.Exists(last))
        {
            if(File.GetAttributes(last).HasFlag(FileAttributes.ReparsePoint))throw new InvalidDataException("プロファイル設定がリンクに変更されています。");
            var bytes=File.ReadAllBytes(last);
            string current=(bytes.Length>=2&&bytes[0]==255&&bytes[1]==254?Encoding.Unicode:Encoding.UTF8).GetString(bytes).Trim('\uFEFF','\0','\r','\n',' ');
            if(current==Name)
            {
                if(Original is null)File.Delete(last);else File.WriteAllBytes(last,Original);
            }
        }
        // Never remove charts or other files added by a user.
        if(!Directory.Exists(folder)||File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))return;
        string marker=Path.Combine(folder,Marker);
        if(!File.Exists(marker)||File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint)||File.ReadAllText(marker)!=Name)return;
        var files=Directory.GetFileSystemEntries(folder);
        if(files.Any(p=>!File.Exists(p)||File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)||
            Path.GetFileName(p) is not (Marker or "order.wnd")))return;
        foreach(var file in files)File.Delete(file);
        Directory.Delete(folder);
    }
}
