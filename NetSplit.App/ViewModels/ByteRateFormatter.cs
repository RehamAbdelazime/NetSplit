namespace NetSplit.App.ViewModels;

public static class ByteRateFormatter
{
    private static readonly string[] Units = { "B/s", "KB/s", "MB/s", "GB/s" };

    public static string Format(double bytesPerSecond)
    {
        double value = bytesPerSecond;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        return $"{value:0.#} {Units[unitIndex]}";
    }
}
