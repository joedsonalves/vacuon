using System.Runtime.Versioning;
using Vacuon.Core.Index;

namespace Vacuon.Core.Security;

public readonly record struct SuspiciousFile(
    int Index,
    string Path,
    Suspicion Level,
    string Reason,
    long SizeBytes,
    DateTime ModifiedUtc);

/// <summary>
/// Heurísticas de arquivo suspeito aplicadas sobre o índice já em memória (PRD F9.x).
/// <para>
/// Roda sem tocar no disco: tudo que precisa (nome, caminho, atributos, tamanho,
/// datas) já veio da MFT. Só a verificação de cabeçalho mágico abre o arquivo, e
/// mesmo assim apenas para os candidatos que já levantaram outra bandeira.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SuspiciousFileAnalyzer
{
    private static readonly string[] ExecutableExtensions =
    [
        ".exe", ".scr", ".com", ".pif", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".ps1", ".psm1", ".jar", ".msi", ".cpl", ".lnk", ".reg",
    ];

    /// <summary>
    /// Extensões que participam da regra de extensão dupla.
    /// <para>
    /// <c>.lnk</c> ficou DE FORA: <c>relatorio.pdf.lnk</c> é exatamente como o Windows
    /// nomeia um atalho para <c>relatorio.pdf</c>, e a pasta Recentes é cheia deles.
    /// Incluir <c>.lnk</c> marcava dezenas de atalhos normais em qualquer máquina usada.
    /// Um atalho malicioso se detecta pelo alvo, não pelo nome — e isso exige parsear
    /// o próprio <c>.lnk</c>, que é trabalho de outro marco.
    /// </para>
    /// </summary>
    private static readonly string[] DoubleExtensionTriggers =
    [
        ".exe", ".scr", ".com", ".pif", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".ps1", ".jar", ".msi", ".cpl",
    ];

    /// <summary>
    /// Pastas cujo conteúdo é gerado pelo próprio Windows. Os nomes ali não foram
    /// escolhidos por ninguém, então não dizem nada sobre intenção.
    /// </summary>
    private static readonly string[] SystemGeneratedFolders =
    [
        @"\appdata\roaming\microsoft\windows\recent\",
        @"\appdata\roaming\microsoft\office\recent\",
        @"\appdata\roaming\microsoft\windows\start menu\",
        @"\programdata\microsoft\windows\start menu\",
    ];

    /// <summary>Extensões que o usuário lê como "documento inofensivo".</summary>
    private static readonly string[] DecoyExtensions =
    [
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".docx", ".xls", ".xlsx",
        ".ppt", ".pptx", ".txt", ".mp3", ".mp4", ".avi", ".mkv", ".zip", ".rar", ".csv",
    ];

    private static readonly string[] VolatileFolders =
    [
        @"\appdata\local\temp\", @"\windows\temp\", @"\downloads\",
        @"\users\public\", @"\$recycle.bin\", @"\appdata\roaming\",
    ];

    /// <summary>
    /// Diretórios de dependência de terceiros. Nada aqui é anexo de e-mail — é código
    /// de biblioteca, e bibliotecas nomeiam arquivos de teste de formas estranhas.
    /// <para>
    /// O caso que motivou esta lista: o pacote npm <c>es-iterator-helpers</c> traz um
    /// <c>Iterator.zip.js</c> (o método <c>Iterator.zip</c>), que a regra de extensão
    /// dupla acusava como "parece .zip mas é executável". Em uma máquina de
    /// desenvolvedor isso rende dezenas de alarmes falsos.
    /// </para>
    /// </summary>
    private static readonly string[] DependencyFolders =
    [
        @"\node_modules\", @"\site-packages\", @"\.bun\", @"\.npm\", @"\.yarn\",
        @"\.pnpm-store\", @"\bower_components\", @"\vendor\", @"\.cargo\registry\",
        @"\.gradle\caches\", @"\.m2\repository\", @"\.nuget\packages\",
        @"\packages\", @"\.venv\", @"\lib\python", @"\dist-packages\",
    ];

    /// <summary>
    /// Texto do sinal "executável novo no System32". É comparado como marcador para
    /// que esse achado sobreviva às exclusões por pasta — daí ser uma constante e não
    /// uma string literal espalhada.
    /// </summary>
    private const string RecentSystem32Signal = "executável criado em System32 nos últimos 30 dias";

    /// <summary>Caracteres Unicode de override bidirecional — usados para inverter a extensão visível.</summary>
    private const char RightToLeftOverride = '‮';
    private const char LeftToRightOverride = '‭';
    private const char RightToLeftMark = '‏';

    public List<SuspiciousFile> Analyze(VolumeIndex index, int maxResults = 500,
                                        CancellationToken cancellationToken = default)
    {
        var results = new List<SuspiciousFile>();

        for (int i = 0; i < index.Entries.Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();

            ref FileEntry e = ref index.Entries[i];
            if (!e.IsInUse || e.IsDirectory) continue;

            ReadOnlySpan<char> name = index.GetName(i);
            if (name.IsEmpty) continue;

            (Suspicion level, string? reason) = Evaluate(name, e, index.GetAdsBytes(i));
            if (level == Suspicion.Normal) continue;

            string path = index.GetFullPath(i);

            // Refina com o caminho, que só existe depois de materializado.
            (level, reason) = RefineWithPath(path, name, e, level, reason!);
            if (level == Suspicion.Normal) continue;

            // O caractere RLO no nome não tem uso legítimo em lugar nenhum, nem dentro
            // de node_modules — esse sinal sobrevive às exclusões por pasta.
            // Dois sinais atravessam as exclusões por pasta, porque não têm explicação
            // inocente em lugar nenhum: o caractere RLO no nome, e um executável
            // recém-criado dentro do System32.
            bool survivesFolderExclusions =
                reason!.Contains("RLO", StringComparison.Ordinal) ||
                reason.Contains(RecentSystem32Signal, StringComparison.Ordinal);

            if (!survivesFolderExclusions && (IsInsideDependencyFolder(path)
                                           || IsSystemGenerated(path)
                                           || IsShippedWithWindows(path))) continue;

            e.Flags |= EntryFlags.Suspicious;

            results.Add(new SuspiciousFile(i, path, level, reason!, e.LogicalSize, e.LastWrite));
        }

        results.Sort(static (a, b) =>
        {
            int byLevel = b.Level.CompareTo(a.Level);
            return byLevel != 0 ? byLevel : b.SizeBytes.CompareTo(a.SizeBytes);
        });

        return results.Count > maxResults ? results.GetRange(0, maxResults) : results;
    }

    /// <summary>Sinais que dependem só do nome e dos atributos.</summary>
    private static (Suspicion, string?) Evaluate(ReadOnlySpan<char> name, in FileEntry entry, long adsBytes)
    {
        // --- Override bidirecional: "fatura‮gpj.exe" aparece como "fatura exe.jpg" ---
        foreach (char c in name)
        {
            if (c is RightToLeftOverride or LeftToRightOverride or RightToLeftMark)
            {
                return (Suspicion.HighlySuspicious,
                    "Nome contém caractere Unicode de inversão de texto (RLO) — truque clássico para esconder a extensão real");
            }
        }

        string lower = name.ToString().ToLowerInvariant();
        string ext = GetExtension(lower);

        // --- Extensão dupla: relatorio.pdf.exe ---
        if (Array.IndexOf(DoubleExtensionTriggers, ext) >= 0)
        {
            string withoutExt = lower[..^ext.Length];
            string inner = GetExtension(withoutExt);

            if (inner.Length > 0 && Array.IndexOf(DecoyExtensions, inner) >= 0)
            {
                return (Suspicion.HighlySuspicious,
                    $"Extensão dupla: parece um arquivo {inner} mas é executável ({ext})");
            }

            // --- Espaços para empurrar a extensão real para fora da vista ---
            if (lower.Contains("      ", StringComparison.Ordinal))
            {
                return (Suspicion.HighlySuspicious,
                    "Sequência longa de espaços no nome, escondendo a extensão executável");
            }
        }

        // --- Executável oculto ---
        if (IsExecutableExtension(ext) && (entry.Flags & EntryFlags.Hidden) != 0)
        {
            return (Suspicion.Suspicious, $"Executável ({ext}) com atributo oculto");
        }

        // --- Executável com ADS: o stream extra pode carregar um segundo binário ---
        if (IsExecutableExtension(ext) && (entry.Flags & EntryFlags.HasAds) != 0 && adsBytes > 4096)
        {
            return (Suspicion.Suspicious,
                $"Executável com Alternate Data Stream de {adsBytes / 1024} KB — conteúdo invisível no Explorer");
        }

        // --- Extensões que praticamente só existem em campanha de phishing ---
        // O refino por caminho descarta as que vivem dentro do Windows: Bubbles.scr e
        // Ribbons.scr são protetores de tela que vêm com o sistema.
        if (ext is ".scr" or ".pif" or ".hta" or ".jse" or ".vbe" or ".wsh")
        {
            return (Suspicion.Suspicious,
                $"Extensão {ext} é rara em software legítimo e comum em anexos maliciosos");
        }

        return (Suspicion.Normal, null);
    }

    /// <summary>Sinais que dependem de onde o arquivo está.</summary>
    private static (Suspicion, string) RefineWithPath(string path, ReadOnlySpan<char> name,
                                                      in FileEntry entry, Suspicion level, string reason)
    {
        string lowerPath = path.ToLowerInvariant();

        foreach (string folder in VolatileFolders)
        {
            if (!lowerPath.Contains(folder, StringComparison.Ordinal)) continue;

            // Executável em pasta volátil sozinho é ruído (instaladores vivem em Downloads).
            // Combinado com outro sinal, sobe o nível.
            if (level >= Suspicion.Suspicious)
            {
                return ((Suspicion)Math.Min((int)Suspicion.HighlySuspicious, (int)level + 1),
                        $"{reason} · localizado em pasta volátil ({folder.Trim('\\')})");
            }

            return (level, $"{reason} · em {folder.Trim('\\')}");
        }

        // Executável recém-criado dentro de System32 é um sinal forte — o diretório
        // muda basicamente só em atualização do Windows.
        if (lowerPath.Contains(@"\windows\system32\", StringComparison.Ordinal) &&
            entry.Created != DateTime.MinValue &&
            (DateTime.UtcNow - entry.Created).TotalDays < 30 &&
            IsExecutableExtension(GetExtension(name.ToString().ToLowerInvariant())))
        {
            return (Suspicion.HighlySuspicious, $"{reason} · {RecentSystem32Signal}");
        }

        return (level, reason);
    }

    /// <summary>
    /// O arquivo está dentro de uma árvore de dependências de terceiros?
    /// Se está, é código de biblioteca — não anexo suspeito.
    /// </summary>
    public static bool IsInsideDependencyFolder(string path)
    {
        string lower = path.ToLowerInvariant();
        foreach (string folder in DependencyFolders)
            if (lower.Contains(folder, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// O arquivo está numa pasta cujo conteúdo o Windows gera sozinho (Recentes,
    /// Menu Iniciar)? Ninguém escolheu esses nomes, então eles não indicam intenção.
    /// </summary>
    public static bool IsSystemGenerated(string path)
    {
        string lower = path.ToLowerInvariant();
        foreach (string folder in SystemGeneratedFolders)
            if (lower.Contains(folder, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// O arquivo vem com o Windows?
    /// <para>
    /// Só a extensão não basta para julgar o que está dentro de <c>%WINDIR%</c>:
    /// <c>Bubbles.scr</c> e <c>Ribbons.scr</c> em System32 são os protetores de tela
    /// do sistema, e marcá-los como phishing é ruído garantido em toda máquina.
    /// Um executável <b>recém-criado</b> ali continua sendo sinalizado pelo refino
    /// de caminho, que é o caso que realmente importa.
    /// </para>
    /// </summary>
    public static bool IsShippedWithWindows(string path)
    {
        string lower = path.ToLowerInvariant();
        return lower.Contains(@"\windows\system32\", StringComparison.Ordinal)
            || lower.Contains(@"\windows\syswow64\", StringComparison.Ordinal)
            || lower.Contains(@"\windows\winsxs\", StringComparison.Ordinal);
    }

    private static bool IsExecutableExtension(string ext) =>
        ext.Length > 0 && Array.IndexOf(ExecutableExtensions, ext) >= 0;

    private static string GetExtension(string lowerName)
    {
        int dot = lowerName.LastIndexOf('.');
        if (dot <= 0 || dot == lowerName.Length - 1) return string.Empty;
        string ext = lowerName[dot..];
        return ext.Length > 10 ? string.Empty : ext;
    }

    /// <summary>
    /// Confirma disfarce lendo os primeiros bytes: extensão diz "imagem", cabeçalho diz "MZ".
    /// Só vale a pena para candidatos já marcados — daí ser um passo separado.
    /// </summary>
    public static bool LooksLikeExecutableContent(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[2];
            if (fs.Read(header) < 2) return false;
            return header[0] == 0x4D && header[1] == 0x5A; // "MZ"
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
