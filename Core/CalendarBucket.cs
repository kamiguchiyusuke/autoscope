namespace ArmadaAnalyzer;

// Cache calendar boundaries, never expand or copy the trade sequence.
internal struct CalendarBucket
{
    private long start,end;
    private int key;
    public int Get(long ticks,bool monthly)
    {
        if(end!=0 && ticks>=start && ticks<end)return key;
        var date=new DateTime(ticks);
        int month=monthly?date.Month:1;
        start=new DateTime(date.Year,month,1).Ticks;
        end=date.Year==9999 && (!monthly || month==12)?DateTime.MaxValue.Ticks:new DateTime(date.Year,month,1).AddMonths(monthly?1:12).Ticks;
        key=monthly?date.Year*100+month:date.Year;
        return key;
    }
}
