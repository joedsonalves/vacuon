using System.Globalization;

namespace Vacuon.App.Infra;

/// <summary>Formatação de números para a interface.</summary>
public static class Format
{
    // U+2212 MINUS SIGN: the interface has a real font behind it, unlike the console.
    public static string Bytes(long value) => Core.ByteSize.Format(value, "−");

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
