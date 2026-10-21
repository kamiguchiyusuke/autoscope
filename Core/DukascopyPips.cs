namespace ArmadaAnalyzer;

public static class DukascopyPips
{
    // Explicit supported symbols only: no suffix stripping or arbitrary CFD inference.
    public static decimal? Size(string symbol)
    {
        string name=symbol.Trim().ToUpperInvariant().Replace("/", "");
        if(name.EndsWith("_TDS_UTC",StringComparison.Ordinal))name=name[..^8];
        return name switch
    {
        "USDJPY" or "EURJPY" or "GBPJPY" or "AUDJPY" or "NZDJPY" or "CADJPY" or "CHFJPY" => 0.01m,
        "XAUUSD" or "XAUUSDP" or "XAUUSD+" => 0.1m, // Report convention requested by user; independent of tick digits.
        "EURUSD" or "GBPUSD" or "AUDUSD" or "NZDUSD" or "USDCHF" or "USDCAD" or "EURGBP" => 0.0001m,
        _ => null
    };
    }
    public static decimal Move(long entry, long exit, int side, decimal size)
    {
        if (size <= 0 || side is not (1 or 2)) throw new ArgumentException("Invalid pip size or trade side");
        return (side == 1 ? (decimal)exit - entry : (decimal)entry - exit) / Amount.Scale / size;
    }
}
