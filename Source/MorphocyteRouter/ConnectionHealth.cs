namespace MorphocyteRouter;

internal sealed class ConnectionHealth
{
    internal int Count { get; private set; }
    internal int? LastDelay { get; private set; }
    internal void Record(int? delay)
    {
        if (delay is < 0) throw new ArgumentOutOfRangeException(nameof(delay));
        LastDelay = delay;
        Count = Math.Min(20, Count + 1);
    }
    internal void Clear() { Count = 0; LastDelay = null; }
    internal static string FormatRate(long bytesPerSecond) => bytesPerSecond switch
    {
        < 0 => "—",
        < 1024 => $"{bytesPerSecond} Б/с",
        < 1_048_576 => $"{bytesPerSecond / 1024d:0.0} КБ/с",
        < 1_073_741_824 => $"{bytesPerSecond / 1_048_576d:0.0} МБ/с",
        _ => $"{bytesPerSecond / 1_073_741_824d:0.0} ГБ/с"
    };
}
