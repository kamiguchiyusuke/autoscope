using System.Globalization;
namespace ArmadaAnalyzer;

/// <summary>Bit zero denotes January/day 1/Sunday/hour 0/minute 0 respectively. Broker open time.</summary>
public sealed record EntryCalendar(int Months = 4095, int Days = int.MaxValue, int Weekdays = 127, int Hours = 16777215, long Minutes = 0x0FFFFFFFFFFFFFFF)
{
    internal string Sql()
    {
        if (Months < 0 || Months > 4095 || Days < 0 || Weekdays < 0 || Weekdays > 127 || Hours < 0 || Hours > 16777215 || Minutes < 0 || Minutes > 0x0FFFFFFFFFFFFFFF)
            throw new ArgumentException("新規時刻の選択範囲が不正です");
        if (Months == 0 || Days == 0 || Weekdays == 0 || Hours == 0 || Minutes == 0)
            return " AND 0";
        string result = "";
        void Mask(long value, long all, string part)
        {
            if (value != all)
                result += " AND ((1 << (" + part + ")) & " + value.ToString(CultureInfo.InvariantCulture) + ")<>0";
        }
        // Only calendar-month/day filters need a date conversion. Other dimensions use integer ticks.
        Mask(Months, 4095, "CAST(strftime('%m', opened/10000000-62135596800, 'unixepoch') AS INTEGER)-1");
        Mask(Days, int.MaxValue, "CAST(strftime('%d', opened/10000000-62135596800, 'unixepoch') AS INTEGER)-1");
        Mask(Weekdays, 127, "(opened/864000000000+1)%7");
        Mask(Hours, 16777215, "(opened/36000000000)%24");
        Mask(Minutes, 0x0FFFFFFFFFFFFFFF, "(opened/600000000)%60");
        return result;
    }
}
