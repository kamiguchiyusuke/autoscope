using System.Globalization;

namespace ArmadaAnalyzer.Desktop;

internal static class NumberDisplay
{
    // Keep the existing F2 rounding, omitting only insignificant fractional zeros.
    internal static string TwoDecimal(double value)=>TrimFraction(value.ToString("F2"));
    internal static string TwoDecimal(decimal value)=>TrimFraction(value.ToString("F2"));
    internal static string GroupedTwoDecimal(decimal value)=>TrimFraction(value.ToString("N2"));

    internal static string TrimFraction(string text)
    {
        string separator=CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        int point=text.LastIndexOf(separator,StringComparison.Ordinal);
        if(point<0) return text;
        int end=text.Length;
        while(end>point+separator.Length && text[end-1]=='0') end--;
        if(end==point+separator.Length) end=point;
        return end==text.Length?text:text[..end];
    }
}
