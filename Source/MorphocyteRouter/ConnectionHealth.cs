namespace MorphocyteRouter;

internal sealed class ConnectionHealth
{
    private readonly Queue<int?> _samples = new();
    internal int Count => _samples.Count;
    internal int SuccessCount => _samples.Count(value => value.HasValue);
    internal int? LastDelay => _samples.LastOrDefault();
    internal double Availability => Count == 0 ? 0 : SuccessCount * 100d / Count;
    internal void Record(int? delay)
    {
        if (delay is < 0) throw new ArgumentOutOfRangeException(nameof(delay));
        _samples.Enqueue(delay);
        if (_samples.Count > 20) _samples.Dequeue();
    }
    internal void Clear() => _samples.Clear();
    internal static string FormatRate(long bytesPerSecond) => bytesPerSecond switch
    {
        < 0 => "—",
        < 1024 => $"{bytesPerSecond} Б/с",
        < 1_048_576 => $"{bytesPerSecond / 1024d:0.0} КБ/с",
        < 1_073_741_824 => $"{bytesPerSecond / 1_048_576d:0.0} МБ/с",
        _ => $"{bytesPerSecond / 1_073_741_824d:0.0} ГБ/с"
    };
}
