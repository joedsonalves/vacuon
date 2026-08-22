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

    /// <summary>
    /// A count of days, rounded to what the estimate can support.
    /// <para>
    /// Two decimals on a projection built from a fitted line would dress a rough estimate as
    /// a measurement. Under a fortnight it is worth a whole number; past that, weeks and
    /// months carry the same information without implying precision nobody has.
    /// </para>
    /// </summary>
    public static string Days(double days) => days switch
    {
        < 14 => string.Format(CultureInfo.CurrentCulture, "{0:N0}", Math.Max(1, Math.Round(days))),
        < 60 => string.Format(CultureInfo.CurrentCulture, "~{0:N0}", Math.Round(days / 7) * 7),
        _ => string.Format(CultureInfo.CurrentCulture, "~{0:N0}", Math.Round(days / 30) * 30),
    };

    public static string DateOrDash(DateTime value) =>
        value == DateTime.MinValue
            ? "—"
            : value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
}
