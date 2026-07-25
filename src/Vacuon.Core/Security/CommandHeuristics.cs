using System.Text.RegularExpressions;

namespace Vacuon.Core.Security;

/// <summary>
/// Heurísticas sobre uma linha de comando de autorun.
/// <para>
/// Nenhuma delas prova nada sozinha. O scanner soma sinais e só sobe o nível quando
/// a combinação é rara em software legítimo — o objetivo é uma lista curta e útil,
/// não um alarme que o usuário aprende a ignorar.
/// </para>
/// </summary>
public static partial class CommandHeuristics
{
    /// <summary>
    /// Pastas onde um executável de autorun é anormal.
    /// <para>
    /// <c>AppData\Local</c> e <c>AppData\Roaming</c> ficaram DE FORA de propósito:
    /// Chrome, Discord, Opera e Roblox instalam ali por padrão. Sinalizar essas pastas
    /// gera meia dúzia de alarmes falsos em toda máquina, e uma lista que alarma sempre
    /// é uma lista que o usuário aprende a ignorar.
    /// </para>
    /// </summary>
    private static readonly (string Folder, Suspicion Level)[] VolatileFolders =
    [
        (@"\appdata\local\temp\", Suspicion.Suspicious),
        (@"\windows\temp\",       Suspicion.Suspicious),
        (@"\$recycle.bin\",       Suspicion.HighlySuspicious),
        (@"\users\public\",       Suspicion.Suspicious),
        (@"\temp\",               Suspicion.Suspicious),
        (@"\tmp\",                Suspicion.Suspicious),
        (@"\downloads\",          Suspicion.Notable),
    ];

    /// <summary>Binários do Windows usados como intermediário. Sempre suspeitos, mesmo em System32.</summary>
    private static readonly string[] AlwaysSuspiciousBinaries =
    [
        "mshta.exe", "certutil.exe", "bitsadmin.exe", "msbuild.exe",
        "installutil.exe", "regasm.exe", "regsvcs.exe", "forfiles.exe", "pcalua.exe",
    ];

    /// <summary>
    /// Intermediários que o próprio Windows usa em entradas legítimas (Active Setup
    /// chama <c>rundll32</c> o tempo todo). Só contam quando o comando sai do sistema.
    /// </summary>
    private static readonly string[] ContextualBinaries =
    [
        "regsvr32.exe", "rundll32.exe", "wmic.exe", "cscript.exe", "wscript.exe",
    ];

    /// <summary>Extrai o caminho do executável de uma linha de comando.</summary>
    public static string ExtractTargetPath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return string.Empty;

        string s = commandLine.Trim();

        // Caminho entre aspas: pega até a aspa de fechamento.
        if (s[0] == '"')
        {
            int close = s.IndexOf('"', 1);
            return close > 1 ? s[1..close] : s[1..];
        }

        // Sem aspas: o primeiro token que termina em executável é o alvo. Isso trata
        // "C:\Program Files\App\app.exe -silent", em que o espaço não separa argumento.
        int probe = 0;
        while (probe < s.Length)
        {
            int space = s.IndexOf(' ', probe);
            string candidate = space < 0 ? s : s[..space];

            if (HasExecutableExtension(candidate)) return candidate;
            if (space < 0) break;
            probe = space + 1;
        }

        int firstSpace = s.IndexOf(' ');
        string first = firstSpace < 0 ? s : s[..firstSpace];

        // Um valor que é só um switch ("/UserInstall", "-silent") não é caminho de nada.
        // Sem esta guarda o Active Setup do Windows vira "autorun órfão".
        return first.StartsWith('/') || first.StartsWith('-') ? string.Empty : first;
    }

    private static bool HasExecutableExtension(string path)
    {
        ReadOnlySpan<char> span = path.AsSpan();
        return span.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || span.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || span.EndsWith(".com", StringComparison.OrdinalIgnoreCase)
            || span.EndsWith(".scr", StringComparison.OrdinalIgnoreCase)
            || span.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            || span.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// O valor é um caminho de arquivo, ou apenas um nome/identificador?
    /// <para>
    /// Chaves como <c>Lsa\Authentication Packages</c> guardam nomes de DLL sem caminho
    /// (<c>msv1_0</c>), e BHOs guardam nomes de exibição. Tratar isso como caminho faz
    /// o scanner gritar "arquivo não existe" para entradas perfeitamente normais.
    /// </para>
    /// </summary>
    public static bool LooksLikePath(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains('\\', StringComparison.Ordinal) || value.Contains(":\\", StringComparison.Ordinal));

    /// <summary>
    /// O caminho está dentro do Windows ou de Arquivos de Programas?
    /// <para>
    /// Importa porque binários do sistema são assinados por <b>catálogo</b> (.cat), não
    /// com assinatura embutida no PE. Cobrar assinatura embutida de <c>rundll32.exe</c>
    /// marca metade do System32 como suspeito.
    /// </para>
    /// </summary>
    public static bool IsUnderSystemDirectory(string lowerPath)
    {
        if (string.IsNullOrEmpty(lowerPath)) return false;

        return lowerPath.Contains(@"\windows\", StringComparison.Ordinal)
            || lowerPath.Contains(@"\program files\", StringComparison.Ordinal)
            || lowerPath.Contains(@"\program files (x86)\", StringComparison.Ordinal)
            || lowerPath.Contains(@"\winsxs\", StringComparison.Ordinal);
    }

    /// <summary>Expande %VARS% e normaliza para comparação.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            // Sem trocar '/' por '\': em linha de comando do Windows a barra é
            // separador de switch, e converter transformaria "/S" em um "caminho".
            return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// Avalia a linha de comando e devolve os motivos encontrados, do mais forte ao mais fraco.
    /// </summary>
    public static (Suspicion Level, List<string> Reasons) Evaluate(string commandLine, string? targetPath)
    {
        var reasons = new List<string>();
        Suspicion level = Suspicion.Normal;

        if (string.IsNullOrWhiteSpace(commandLine)) return (level, reasons);

        string lower = commandLine.ToLowerInvariant();
        string normalizedTarget = Normalize(targetPath ?? string.Empty).ToLowerInvariant();

        void Raise(Suspicion l, string reason)
        {
            if (l > level) level = l;
            reasons.Add(reason);
        }

        // --- PowerShell com comando codificado: quase nunca é legítimo em autorun ---
        if (EncodedPowerShell().IsMatch(lower))
            Raise(Suspicion.HighlySuspicious, "PowerShell com comando codificado em Base64 (-EncodedCommand)");

        if (lower.Contains("powershell") &&
            (lower.Contains("-w hidden") || lower.Contains("-windowstyle hidden") || lower.Contains("-w h ")))
            Raise(Suspicion.HighlySuspicious, "PowerShell iniciado com janela oculta");

        if (lower.Contains("executionpolicy bypass") || lower.Contains("-ep bypass") || lower.Contains("-exec bypass"))
            Raise(Suspicion.HighlySuspicious, "Política de execução do PowerShell contornada (Bypass)");

        if (lower.Contains("downloadstring") || lower.Contains("downloadfile") ||
            lower.Contains("invoke-webrequest") || lower.Contains("iwr ") || lower.Contains("invoke-expression"))
            Raise(Suspicion.HighlySuspicious, "Baixa e executa conteúdo da internet");

        // --- Download direto na linha de comando ---
        if (UrlInCommand().IsMatch(lower))
            Raise(Suspicion.Suspicious, "URL embutida na linha de comando do autorun");

        // --- Binários do próprio Windows usados como intermediário ---
        foreach (string lolbin in AlwaysSuspiciousBinaries)
        {
            if (!lower.Contains(lolbin)) continue;
            Raise(Suspicion.HighlySuspicious, $"Usa {lolbin} como intermediário (técnica de living-off-the-land)");
            break;
        }

        // Os contextuais só valem fora do diretório do sistema: entradas nativas do
        // Windows (Active Setup, por exemplo) chamam rundll32 legitimamente.
        if (!IsUnderSystemDirectory(lower))
        {
            foreach (string lolbin in ContextualBinaries)
            {
                if (!lower.Contains(lolbin)) continue;
                Raise(Suspicion.Suspicious, $"Usa {lolbin} como intermediário fora do diretório do sistema");
                break;
            }
        }

        if (lower.Contains("certutil") && (lower.Contains("-decode") || lower.Contains("-urlcache")))
            Raise(Suspicion.HighlySuspicious, "certutil usado para decodificar ou baixar arquivo");

        if (lower.Contains("rundll32") && (lower.Contains("javascript:") || lower.Contains("vbscript:")))
            Raise(Suspicion.HighlySuspicious, "rundll32 executando script embutido");

        // --- Localização do alvo ---
        if (!string.IsNullOrEmpty(normalizedTarget))
        {
            foreach ((string folder, Suspicion folderLevel) in VolatileFolders)
            {
                if (!normalizedTarget.Contains(folder, StringComparison.Ordinal)) continue;
                Raise(folderLevel, $"Executável fica em pasta volátil ({folder.Trim('\\')})");
                break;
            }

            // Script interpretado direto do perfil do usuário
            if (ScriptInUserProfile().IsMatch(normalizedTarget))
                Raise(Suspicion.Suspicious, "Autorun aponta para um script (vbs/js/ps1/bat) no perfil do usuário");
        }

        // --- Nome do executável tentando parecer do sistema ---
        string fileName = Path.GetFileName(normalizedTarget);
        if (LooksLikeSystemBinaryTypo(fileName))
            Raise(Suspicion.HighlySuspicious, $"Nome imita um binário do sistema ({fileName})");

        return (level, reasons);
    }

    /// <summary>
    /// Nomes que imitam binários do Windows por uma letra ou por espaço extra.
    /// É o truque mais barato de todos e ainda funciona.
    /// </summary>
    private static bool LooksLikeSystemBinaryTypo(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;

        string[] impostors =
        [
            "svch0st.exe", "svchosts.exe", "scvhost.exe", "svchost .exe", "svchosl.exe",
            "explorer .exe", "expl0rer.exe", "explore.exe",
            "lsass .exe", "lsasss.exe", "isass.exe", "1sass.exe",
            "csrss .exe", "csrsss.exe", "crss.exe",
            "winlogon .exe", "winlogin.exe",
            "rundll32 .exe", "rundii32.exe",
            "services .exe", "servlces.exe",
            "taskhost .exe", "taskhostw .exe",
            "chrome .exe", "ch rome.exe",
        ];

        foreach (string impostor in impostors)
            if (string.Equals(fileName, impostor, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    [GeneratedRegex(@"-e(nc|ncoded|ncodedcommand)?\s+[a-z0-9+/=]{40,}", RegexOptions.IgnoreCase)]
    private static partial Regex EncodedPowerShell();

    [GeneratedRegex(@"https?://|ftp://|\\\\[a-z0-9._-]+\\", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInCommand();

    [GeneratedRegex(@"\\users\\[^\\]+\\.*\.(vbs|vbe|js|jse|ps1|bat|cmd|wsf|hta)$", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptInUserProfile();
}
