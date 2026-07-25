using System.Diagnostics;
using System.Text;
using Vacuon.Cli;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Preview;
using Vacuon.Core.Scan;
using Vacuon.Core.Security;
using Vacuon.Native.Interop;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "/?")
{
    Help.Print();
    return 0;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "scan" => Commands.Scan(args[1..]),
        "volumes" => Commands.Volumes(),
        "security" => Commands.Security(args[1..]),
        "thumb" => Commands.Thumb(args[1..]),
        "reveal" => Commands.Reveal(args[1..]),
        "version" or "--version" => Commands.Version(),
        _ => Unknown(args[0]),
    };
}
catch (OperationCanceledException)
{
    Formatting.WriteWarning("\nCancelado.");
    return 5;
}
catch (VolumeAccessException ex)
{
    Formatting.WriteError($"\n{ex.Message}");
    return ex.Failure == VolumeAccessFailure.NeedsElevation ? 3 : 4;
}

static int Unknown(string command)
{
    Formatting.WriteError($"Comando desconhecido: {command}");
    Help.Print();
    return 2;
}

// ---------------------------------------------------------------------------

static class Help
{
    public static void Print()
    {
        Console.WriteLine();
        Console.WriteLine("  VACUON — analisador e liberador de espaço em disco");
        Console.WriteLine("  v0.1.0 · marco M1 (motor de varredura)");
        Console.WriteLine();
        Console.WriteLine("  USO");
        Console.WriteLine("    vacuon <comando> [opções]");
        Console.WriteLine();
        Console.WriteLine("  COMANDOS");
        Console.WriteLine("    volumes                    Lista os volumes e o espaço de cada um");
        Console.WriteLine("    scan <unidade|pasta>       Mapeia o espaço e mostra o que ocupa");
        Console.WriteLine("    security                   Inspeciona as chaves do registro onde malware se aloja");
        Console.WriteLine("    thumb <arquivo>            Extrai a miniatura de um arquivo");
        Console.WriteLine("    reveal <arquivo>           Abre o Explorer com o arquivo selecionado");
        Console.WriteLine("    version                    Versão e ambiente");
        Console.WriteLine();
        Console.WriteLine("  OPÇÕES DE scan");
        Console.WriteLine("    --top=N                    Quantos itens listar (padrão 20)");
        Console.WriteLine("    --strategy=auto|mft|walk   Força a estratégia de varredura");
        Console.WriteLine("    --suspicious               Também procura arquivos suspeitos");
        Console.WriteLine("    --no-progress              Silencia a barra de progresso");
        Console.WriteLine();
        Console.WriteLine("  OPÇÕES DE security");
        Console.WriteLine("    --all                      Inclui também as entradas normais");
        Console.WriteLine("    --no-signatures            Pula a checagem de assinatura digital (mais rápido)");
        Console.WriteLine();
        Console.WriteLine("  OPÇÕES DE thumb");
        Console.WriteLine("    --size=16|32|64|128|256|512   Tamanho em pixels (padrão 256)");
        Console.WriteLine("    --out=arquivo.bmp             Onde gravar (padrão: ao lado do original)");
        Console.WriteLine("    --icon                        Força o ícone do tipo em vez do conteúdo");
        Console.WriteLine();
        Console.WriteLine("  A leitura da MFT exige executar como Administrador. Sem elevação, o Vacuon");
        Console.WriteLine("  cai automaticamente para a travessia pela API — mais lenta, mesmo resultado.");
        Console.WriteLine();
    }
}

static class Commands
{
    public static int Version()
    {
        Console.WriteLine($"Vacuon 0.1.0");
        Console.WriteLine($".NET        {Environment.Version}");
        Console.WriteLine($"SO          {Environment.OSVersion.VersionString}");
        Console.WriteLine($"Núcleos     {Environment.ProcessorCount}");
        Console.WriteLine($"Elevado     {(VolumeProbe.IsElevated() ? "sim" : "não")}");
        return 0;
    }

    public static int Volumes()
    {
        Formatting.WriteHeading("VOLUMES");

        foreach (VolumeInfo v in VolumeProbe.EnumerateFixedVolumes())
        {
            double usedPercent = v.TotalBytes == 0 ? 0 : v.UsedBytes * 100.0 / v.TotalBytes;
            string bar = Bar(usedPercent, 24);

            Console.WriteLine($"  {v.DriveLetter}:    {Formatting.Truncate(v.Label, 22),-24} {v.FileSystem,-8} " +
                              $"{Formatting.Bytes(v.TotalBytes),12}   {bar} {usedPercent,5:N1}% usado   " +
                              $"{Formatting.Bytes(v.FreeBytes)} livres");
        }

        Console.WriteLine();
        return 0;
    }

    public static int Scan(string[] args)
    {
        string target = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "C:";
        int top = ArgInt(args, "--top", 20);
        bool showProgress = !args.Contains("--no-progress") && !Console.IsOutputRedirected;
        bool wantSuspicious = args.Contains("--suspicious");

        StrategyPreference preference = ArgString(args, "--strategy", "auto").ToLowerInvariant() switch
        {
            "mft" => StrategyPreference.ForceMft,
            "walk" => StrategyPreference.ForceWalk,
            _ => StrategyPreference.Auto,
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        IProgress<ScanProgress>? progress = showProgress ? new ConsoleProgress() : null;
        var options = new MftScanOptions { Progress = progress };
        var orchestrator = new ScanOrchestrator(options);

        var sw = Stopwatch.StartNew();

        ScanResult result = IsWholeVolume(target)
            ? orchestrator.ScanVolume(char.ToUpperInvariant(target[0]), preference, cts.Token)
            : orchestrator.ScanFolder(target, cts.Token);

        sw.Stop();
        if (showProgress) ConsoleProgress.Clear();

        VolumeIndex index = result.Index;

        // ------------------------------------------------------------------
        Formatting.WriteHeading($"VARREDURA — {target}");

        Console.WriteLine($"  Estratégia        {(result.StrategyUsed == ScanStrategy.Mft ? "leitura bruta da MFT" : "travessia pela API do Windows")}");
        if (result.FallbackReason is not null)
            Formatting.WriteMuted($"                    (caiu para o fallback: {result.FallbackReason})");

        int files = index.FileCount;
        int dirs = index.DirectoryCount;

        Console.WriteLine($"  Tempo             {Formatting.Duration(sw.Elapsed)}");
        Console.WriteLine($"  Arquivos          {Formatting.Count(files)}");
        Console.WriteLine($"  Pastas            {Formatting.Count(dirs)}");
        if (sw.Elapsed.TotalSeconds > 0)
            Console.WriteLine($"  Velocidade        {Formatting.Count((long)(files / sw.Elapsed.TotalSeconds))} arquivos/s");

        bool hasRealAllocation = result.StrategyUsed == ScanStrategy.Mft;

        Console.WriteLine();
        Console.WriteLine($"  Tamanho lógico    {Formatting.Bytes(index.TotalLogicalBytes)}");

        if (hasRealAllocation)
        {
            Console.WriteLine($"  Tamanho em disco  {Formatting.Bytes(index.TotalBytesOnDisk)}");
            Console.WriteLine($"  Desperdício       {Formatting.Bytes(index.TotalSlackBytes)}  (folga de cluster)");
        }
        else
        {
            // A API do Windows não expõe AllocatedSize: repetir o tamanho lógico aqui
            // faria o app afirmar "desperdício 0 B", que é falso e não medido.
            Formatting.WriteMuted("  Tamanho em disco  não medido (só a leitura da MFT expõe AllocatedSize)");
            Formatting.WriteMuted("  Desperdício       não medido pelo mesmo motivo");
        }

        Console.WriteLine($"  Volume            {Formatting.Bytes(index.Volume.UsedBytes)} usados de {Formatting.Bytes(index.Volume.TotalBytes)}");

        // ------------------------------------------------------------------
        Formatting.WriteHeading($"MAIORES ARQUIVOS (top {top})");
        foreach (SizedItem item in SizeAnalyzer.TopFiles(index, top))
        {
            string path = index.GetFullPath(item.Index);
            ref FileEntry e = ref index.Entries[item.Index];

            string badge = (e.Flags & EntryFlags.CloudPlaceholder) != 0 ? " [nuvem]"
                         : (e.Flags & EntryFlags.HardLinked) != 0 ? " [hardlink]"
                         : (e.Flags & EntryFlags.Compressed) != 0 ? " [comprimido]"
                         : string.Empty;

            Console.WriteLine($"  {Formatting.Bytes(item.LogicalSize),12}  {Formatting.Truncate(path, 92)}{badge}");
        }

        // ------------------------------------------------------------------
        Formatting.WriteHeading($"MAIORES PASTAS (top {top})");
        foreach (SizedItem item in SizeAnalyzer.TopFolders(index, top))
        {
            string path = index.GetFullPath(item.Index);
            Console.WriteLine($"  {Formatting.Bytes(item.LogicalSize),12}  {Formatting.Count(item.FileCount),9} arq.  {Formatting.Truncate(path, 80)}");
        }

        // ------------------------------------------------------------------
        Formatting.WriteHeading("POR TIPO DE ARQUIVO");
        foreach (ExtensionBucket bucket in SizeAnalyzer.ByExtension(index, 15))
        {
            Console.WriteLine($"  {Formatting.Bytes(bucket.TotalBytes),12}  {Formatting.Count(bucket.Count),9} arq.  " +
                              $"{bucket.Extension,-16} {bucket.Category}");
        }

        // ------------------------------------------------------------------
        Formatting.WriteHeading("DISTRIBUIÇÃO POR TAMANHO");
        foreach (SizeBucket bucket in SizeAnalyzer.BySizeRange(index))
        {
            if (bucket.Count == 0) continue;

            string slack = hasRealAllocation
                ? $"   desperdício {Formatting.Bytes(bucket.SlackBytes)}"
                : string.Empty;

            Console.WriteLine($"  {bucket.Label,-18} {Formatting.Count(bucket.Count),10} arq. " +
                              $"{Formatting.Bytes(bucket.TotalBytes),12}{slack}");
        }

        // ------------------------------------------------------------------
        Formatting.WriteHeading("IDADE DOS ARQUIVOS (última modificação)");
        foreach (AgeBucket bucket in SizeAnalyzer.ByAge(index, DateTime.UtcNow))
        {
            if (bucket.Count == 0) continue;
            Console.WriteLine($"  {bucket.Label,-18} {Formatting.Count(bucket.Count),10} arq. {Formatting.Bytes(bucket.TotalBytes),12}");
        }

        // ------------------------------------------------------------------
        if (wantSuspicious)
        {
            Formatting.WriteHeading("ARQUIVOS SUSPEITOS");
            var analyzer = new SuspiciousFileAnalyzer();
            List<SuspiciousFile> suspicious = analyzer.Analyze(index, 40, cts.Token);

            if (suspicious.Count == 0)
            {
                Formatting.WriteMuted("  Nenhum arquivo bateu nas heurísticas. Isso é uma boa notícia.");
            }
            else
            {
                foreach (SuspiciousFile s in suspicious)
                {
                    ConsoleColor previous = Console.ForegroundColor;
                    Console.ForegroundColor = Formatting.ColorFor(s.Level);
                    Console.WriteLine($"  [{Formatting.LabelFor(s.Level)}] {Formatting.Truncate(s.Path, 88)}");
                    Console.ForegroundColor = previous;
                    Formatting.WriteMuted($"      {s.Reason}");
                    Formatting.WriteMuted($"      {Formatting.Bytes(s.SizeBytes)} · modificado em {s.ModifiedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
                }

                Console.WriteLine();
                Formatting.WriteMuted("  O Vacuon não é antivírus: isto é heurística de comportamento, não veredito.");
                Formatting.WriteMuted("  Nada foi alterado. Verifique cada item antes de agir.");
            }
        }

        Console.WriteLine();
        return 0;
    }

    public static int Security(string[] args)
    {
        bool all = args.Contains("--all");
        bool signatures = !args.Contains("--no-signatures");

        Formatting.WriteHeading("CHAVES DE PERSISTÊNCIA DO REGISTRO");

        if (!VolumeProbe.IsElevated())
            Formatting.WriteWarning("  Sem elevação: chaves de HKLM protegidas podem não ser lidas. Execute como Administrador para a inspeção completa.\n");

        var scanner = new RegistryPersistenceScanner(new SecurityScanOptions
        {
            IncludeNormal = all,
            CheckSignatures = signatures,
        });

        SecurityReport report = scanner.Scan();

        Console.WriteLine($"  Locais inspecionados   {report.LocationsInspected}");
        Console.WriteLine($"  Entradas lidas         {Formatting.Count(report.EntriesInspected)}");
        Console.WriteLine($"  Tempo                  {Formatting.Duration(report.Elapsed)}");
        Console.WriteLine();

        int flagged = report.CountAtLeast(Suspicion.Notable);
        if (flagged == 0)
        {
            Formatting.WriteMuted("  Nenhuma entrada fugiu do padrão. Os pontos de autorun estão limpos.");
            Console.WriteLine();
            return 0;
        }

        foreach (SecurityFinding f in report.Findings)
        {
            if (!all && f.Level == Suspicion.Normal) continue;

            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = Formatting.ColorFor(f.Level);
            Console.WriteLine($"  [{Formatting.LabelFor(f.Level)}] {f.Location}");
            Console.ForegroundColor = previous;

            Console.WriteLine($"      {f.Name} = {Formatting.Truncate(f.Value, 100)}");
            Formatting.WriteMuted($"      → {f.Reason}");

            if (f.TargetPath is not null)
            {
                string exists = f.TargetExists == true ? "existe" : "NÃO EXISTE";
                string signer = f.Signer is null ? "sem assinatura embutida" : $"assinado por {f.Signer}";
                Formatting.WriteMuted($"      alvo: {Formatting.Truncate(f.TargetPath, 90)} ({exists}, {signer})");
            }

            Console.WriteLine();
        }

        Formatting.WriteMuted("  Somente leitura: o Vacuon não alterou nenhuma chave.");
        Formatting.WriteMuted("  Entradas legítimas aparecem aqui o tempo todo — leia o motivo antes de concluir.");
        Console.WriteLine();
        return 0;
    }

    public static int Thumb(string[] args)
    {
        string? file = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (file is null || !File.Exists(file))
        {
            Formatting.WriteError("Informe um arquivo existente: vacuon thumb <arquivo> [--size=256]");
            return 2;
        }

        int size = ArgInt(args, "--size", 256);
        bool iconOnly = args.Contains("--icon");
        string output = ArgString(args, "--out", Path.ChangeExtension(Path.GetFileName(file), ".thumb.bmp"));

        ThumbnailSize thumbSize = size switch
        {
            <= 16 => ThumbnailSize.Tiny,
            <= 32 => ThumbnailSize.Small,
            <= 64 => ThumbnailSize.Medium,
            <= 128 => ThumbnailSize.Large,
            <= 256 => ThumbnailSize.ExtraLarge,
            _ => ThumbnailSize.Huge,
        };

        using var provider = new ThumbnailProvider();
        var sw = Stopwatch.StartNew();
        ThumbnailBitmap? bitmap = provider.Get(file, thumbSize, preferContent: !iconOnly);
        sw.Stop();

        if (bitmap is null)
        {
            Formatting.WriteError("O Shell do Windows não conseguiu produzir uma imagem para este arquivo.");
            return 1;
        }

        BmpWriter.Write(bitmap, output);

        Formatting.WriteHeading("MINIATURA");
        Console.WriteLine($"  Arquivo    {file}");
        Console.WriteLine($"  Categoria  {FileCategories.Of(Path.GetFileName(file).AsSpan())}");
        Console.WriteLine($"  Origem     {(bitmap.IsContentThumbnail ? "conteúdo do arquivo" : "ícone do tipo")}");
        Console.WriteLine($"  Dimensões  {bitmap.Width} × {bitmap.Height} px");
        Console.WriteLine($"  Tempo      {Formatting.Duration(sw.Elapsed)}");
        Console.WriteLine($"  Gravado em {Path.GetFullPath(output)}");
        Console.WriteLine();
        return 0;
    }

    public static int Reveal(string[] args)
    {
        string? file = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (file is null)
        {
            Formatting.WriteError("Informe um caminho: vacuon reveal <arquivo>");
            return 2;
        }

        Shell32.RevealInExplorer(Path.GetFullPath(file));
        return 0;
    }

    /// <summary>"C:", "C:\" e "c" significam o volume inteiro; qualquer outra coisa é pasta.</summary>
    private static bool IsWholeVolume(string target) => target.Trim() switch
    {
        [char letter] => char.IsLetter(letter),
        [char letter, ':'] => char.IsLetter(letter),
        [char letter, ':', '\\'] => char.IsLetter(letter),
        _ => false,
    };

    private static string Bar(double percent, int width)
    {
        int filled = (int)Math.Round(percent / 100.0 * width);
        filled = Math.Clamp(filled, 0, width);
        return new string('█', filled) + new string('░', width - filled);
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        string? raw = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return raw is not null && int.TryParse(raw[(name.Length + 1)..], out int value) ? value : fallback;
    }

    private static string ArgString(string[] args, string name, string fallback)
    {
        string? raw = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return raw is not null ? raw[(name.Length + 1)..] : fallback;
    }
}

/// <summary>
/// Barra de progresso com throttle. Reportar por arquivo trava mais o terminal
/// do que a própria varredura (PRD §17, armadilha 14).
/// </summary>
sealed class ConsoleProgress : IProgress<ScanProgress>
{
    private static int _lastLength;

    public void Report(ScanProgress value)
    {
        string line = value.TotalBytes > 0
            ? $"  {value.Percent,5:N1}%  {Formatting.Count(value.RecordsParsed)} registros  " +
              $"{Formatting.Count(value.EntriesFound)} itens  {value.MegabytesPerSecond,6:N0} MB/s"
            : $"  {Formatting.Count(value.RecordsParsed)} itens  {Formatting.Duration(value.Elapsed)}";

        Console.Write('\r');
        Console.Write(line.PadRight(Math.Max(_lastLength, line.Length)));
        _lastLength = line.Length;
    }

    public static void Clear()
    {
        Console.Write('\r');
        Console.Write(new string(' ', _lastLength + 2));
        Console.Write('\r');
    }
}
