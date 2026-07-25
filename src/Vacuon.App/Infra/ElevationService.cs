using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Vacuon.App.Infra;

/// <summary>
/// Relançamento elevado.
/// <para>
/// O manifesto do app é <c>asInvoker</c> (ADR-7): quem só quer olhar uma pasta não
/// deve encarar o UAC. A elevação é uma escolha do usuário, feita aqui.
/// </para>
/// </summary>
public static class ElevationService
{
    /// <summary>
    /// Argumento que marca "já tentei elevar". Sem ele, um relançamento que falha
    /// silenciosamente entra em laço infinito de UAC.
    /// </summary>
    public const string RelaunchGuardArgument = "--elevation-attempted";

    public static bool IsElevated => Vacuon.Core.Scan.VolumeProbe.IsElevated();

    public static bool WasRelaunchAttempted(string[] args) =>
        Array.Exists(args, a => string.Equals(a, RelaunchGuardArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reabre o app elevado e encerra esta instância.
    /// </summary>
    /// <returns>
    /// <c>false</c> se o usuário recusou o UAC (ou ele falhou) — nesse caso a instância
    /// atual continua viva e utilizável, apenas sem a leitura da MFT.
    /// </returns>
    public static bool RelaunchElevated(IEnumerable<string>? extraArguments = null)
    {
        string? executable = GetExecutablePath();
        if (executable is null) return false;

        var arguments = new List<string> { RelaunchGuardArgument };
        if (extraArguments is not null) arguments.AddRange(extraArguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(' ', arguments.Select(Quote)),
            // UseShellExecute é obrigatório para o verbo runas funcionar.
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };

        try
        {
            Process.Start(startInfo)?.Dispose();
        }
        catch (Win32Exception)
        {
            // ERROR_CANCELLED: o usuário clicou "Não" no UAC. Não é erro do app.
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            return false;
        }

        Application.Current?.Shutdown();
        return true;
    }

    /// <summary>
    /// Caminho do executável real.
    /// <para>
    /// <c>Assembly.Location</c> devolve string vazia em publicação single-file, e
    /// <c>ProcessPath</c> aponta para o host extraído. <c>MainModule</c> é o que
    /// funciona nos dois casos.
    /// </para>
    /// </summary>
    private static string? GetExecutablePath()
    {
        try
        {
            string? path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }

        string? fallback = Environment.ProcessPath;
        return !string.IsNullOrEmpty(fallback) && File.Exists(fallback) ? fallback : null;
    }

    private static string Quote(string argument) =>
        argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
}
