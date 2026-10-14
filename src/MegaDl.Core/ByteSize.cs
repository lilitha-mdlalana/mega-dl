namespace MegaDl.Core;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>1536 → "1.5 KB". Java port note: MiscTools.formatBytes.</summary>
    public static string Format(double bytes)
    {
        int unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1) { bytes /= 1024; unit++; }
        return unit == 0 ? $"{bytes:0} B" : $"{bytes:0.##} {Units[unit]}";
    }
}
