using System.Globalization;

namespace Vacuon.App.Infra;

/// <summary>Formatação de números para a interface.</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    public static string Bytes(long value)
    {
        if (value < 0) return "−" + Bytes(-value);
        if (value < 1024) return $"{value} B";

        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return size >= 100
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", size, Units[unit])
            : string.Format(CultureInfo.CurrentCulture, "{0:N1} {1}", size, Units[unit]);
    }

    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Percent(double value) =>
        string.Format(CultureInfo.CurrentCulture, "{0:N1}%", value);

    public static string Duration(TimeSpan value) =>
        value.TotalSeconds < 1 ? $"{value.TotalMilliseconds:N0} ms"
        : value.TotalSeconds < 60 ? $"{value.TotalSeconds:N1} s"
        : $"{(int)value.TotalMinutes} min {value.Seconds} s";

    public static string DateOrDash(DateTime value) =>
        value == DateTime.MinValue
            ? "—"
            : value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
}
