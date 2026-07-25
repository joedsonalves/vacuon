using System.Globalization;
using Vacuon.Core.Localization;
using Vacuon.Core.Localization;
using Vacuon.Core.Security;

namespace Vacuon.Cli;

/// <summary>Formatação para o terminal. Números tabulares, cor só onde significa algo.</summary>
public static class Formatting
{
    private static readonly string[] BinaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    public static string Bytes(long value)
    {
        if (value < 0) return "-" + Bytes(-value);
        if (value < 1024) return $"{value} B";

        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < BinaryUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return size >= 100
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", size, BinaryUnits[unit])
            : string.Format(CultureInfo.CurrentCulture, "{0:N1} {1}", size, BinaryUnits[unit]);
    }

    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Duration(TimeSpan value) =>
        value.TotalSeconds < 1
            ? $"{value.TotalMilliseconds:N0} ms"
            : value.TotalSeconds < 60
                ? $"{value.TotalSeconds:N2} s"
                : $"{(int)value.TotalMinutes} min {value.Seconds} s";

    public static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        if (max <= 3) return value[..max];

        // Trunca no meio: o começo (unidade) e o fim (nome do arquivo) são o que importa.
        int head = (max - 3) / 2;
        int tail = max - 3 - head;
        return string.Concat(value.AsSpan(0, head), "...", value.AsSpan(value.Length - tail));
    }

    /// <summary>
    /// Largura utilizável do terminal. Sem console anexado (saída redirecionada,
    /// execução por script), Console.WindowWidth lança IOException em vez de
    /// devolver zero — por isso a consulta é sempre protegida.
    /// </summary>
    public static int ConsoleWidth
    {
        get
        {
            try
            {
                int width = Console.WindowWidth;
                return width > 20 ? width : 100;
            }
            catch (IOException)
            {
                return 100;
            }
        }
    }

    public static void WriteHeading(string text)
    {
        Console.WriteLine();
        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(text);
        Console.WriteLine(new string('─', Math.Min(text.Length, ConsoleWidth - 1)));
        Console.ForegroundColor = previous;
    }

    public static void WriteMuted(string text)
    {
        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    public static void WriteWarning(string text)
    {
        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    public static void WriteError(string text)
    {
        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    public static ConsoleColor ColorFor(Suspicion level) => level switch
    {
        Suspicion.HighlySuspicious => ConsoleColor.Red,
        Suspicion.Suspicious => ConsoleColor.DarkYellow,
        Suspicion.Notable => ConsoleColor.Yellow,
        _ => ConsoleColor.DarkGray,
    };

    public static string LabelFor(Suspicion level) => L.T(level switch
    {
        Suspicion.HighlySuspicious => "suspicion.highly",
        Suspicion.Suspicious => "suspicion.suspicious",
        Suspicion.Notable => "suspicion.notable",
        _ => "suspicion.normal",
    });
}
