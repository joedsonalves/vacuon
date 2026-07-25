using System.Globalization;

namespace Vacuon.Core;

/// <summary>
/// Byte counts as text, in binary units. Lived duplicated in the CLI and the GUI until Core
/// itself needed to say "you are claiming 367 GiB more than the volume holds".
/// </summary>
public static class ByteSize
{
    private static readonly string[] BinaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <param name="negativeSign">
    /// The GUI uses a real minus sign (U+2212) and the terminal a hyphen, because a console
    /// font cannot be relied on to have the former.
    /// </param>
    public static string Format(long value, string negativeSign = "-")
    {
        if (value < 0) return negativeSign + Format(-value, negativeSign);
        if (value < 1024) return $"{value} B";

        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < BinaryUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Three significant figures is the readable point: "757 GiB", not "757.6 GiB".
        return size >= 100
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", size, BinaryUnits[unit])
            : string.Format(CultureInfo.CurrentCulture, "{0:N1} {1}", size, BinaryUnits[unit]);
    }
}
