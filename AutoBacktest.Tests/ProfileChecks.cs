using MT4BatchReporter;
using System.Text;
internal static class ProfileChecks
{
    public static void Run(string root)
    {
        string data=Path.Combine(root,"profile-session"),profiles=Path.Combine(data,"profiles");
        Directory.CreateDirectory(profiles);string last=Path.Combine(profiles,"lastprofile.ini");
        byte[] original=Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("日本語プロファイル\r\n")).ToArray();
        File.WriteAllBytes(last,original);
        var state=Mt4Profile.Capture(data);state.Begin(data);
        string folder=Path.Combine(profiles,state.Name);
        if(Directory.GetFiles(folder,"*.chr").Length!=0)throw new Exception("profile has charts");
        File.WriteAllText(last,state.Name+"\r\n");state.Restore(data);
        if(!File.ReadAllBytes(last).SequenceEqual(original)||Directory.Exists(folder))throw new Exception("original profile not restored");
        state=Mt4Profile.Capture(data);state.Begin(data);File.WriteAllText(last,"UserChanged");
        state.Restore(data);if(File.ReadAllText(last)!="UserChanged")throw new Exception("user profile overwritten");
        File.Delete(last);state=Mt4Profile.Capture(data);state.Begin(data);File.WriteAllText(last,state.Name);
        state.Restore(data);if(File.Exists(last))throw new Exception("absent profile not restored");
        state=Mt4Profile.Capture(data);state.Name="../escape";
        try{state.Restore(data);throw new Exception("unsafe profile accepted");}catch(InvalidDataException){}
        Console.WriteLine("PASS MT4 empty profile restores exact bytes, preserves external changes and rejects unsafe paths");
    }
}
