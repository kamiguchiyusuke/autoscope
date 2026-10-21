namespace MT4BatchReporter;

public static class ReportFiles
{
    public static string ScopePdfPath(string directory,string testName)
    {
        string name=JobFiles.SafeName(testName);
        if(string.IsNullOrWhiteSpace(name))name="テスト";
        string device=name.Split('.')[0].ToUpperInvariant();
        if(device is "CON" or "PRN" or "AUX" or "NUL" ||
           device.Length==4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] is >= '1' and <= '9')name="_"+name;
        return Path.Combine(directory,name+".pdf");
    }
}
