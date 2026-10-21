using System.Windows;
using System.Windows.Media;
namespace ArmadaAnalyzer.Desktop;
internal static class UiTheme
{
    private static bool initialized, highContrast;
    public static bool IsDark { get; private set; }
    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
    public static bool Apply(bool dark)
    {
        if (initialized && IsDark == dark && highContrast == SystemParameters.HighContrast) return false;
        initialized = true;
        highContrast = SystemParameters.HighContrast;
        IsDark = dark;
        string[] keys = ["Canvas", "Surface", "Raised", "Ink", "Muted", "Subtle", "Outline", "Hover", "Selected", "Accent", "Gain", "Loss", "Warning", "ChartGrid", "PrimaryFill", "ScoreBlue", "ScoreYellow", "ScoreRed"];
        string[] light = ["#F1F2F4", "#FFFFFF", "#F7F7F8", "#202329", "#59616D", "#6B727D", "#D8DBDF", "#F7EBED", "#FCE8EA", "#DC1723", "#16716C", "#AB3A50", "#96620E", "#E5E7EB", "#DC1723", "#246DC1", "#B58A00", "#D12C38"];
        string[] night = ["#0B0D10", "#17191D", "#292D33", "#F2F2F2", "#BBC0C8", "#A4ABB5", "#3C424C", "#302226", "#3C2027", "#FF5964", "#70CFC2", "#F89BAD", "#EBC274", "#30353D", "#DC1723", "#79B4FF", "#F3CE45", "#FF727C"];
        if (SystemParameters.HighContrast)
        {
            foreach (string key in keys) Application.Current.Resources[key] = key is "Canvas" or "Surface" or "Raised" ? SystemColors.WindowBrush : key is "Selected" or "Hover" or "PrimaryFill" ? SystemColors.HighlightBrush : SystemColors.WindowTextBrush;
            return true;
        }
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString((dark ? night : light)[i]));
            brush.Freeze();
            Application.Current.Resources[keys[i]] = brush;
        }
        return true;
    }
}
