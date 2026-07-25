using System.Diagnostics;
using System.Text;
using Vacuon.Cli;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;
using Vacuon.Core.Preview;
using Vacuon.Core.Scan;
using Vacuon.Core.Security;
using Vacuon.Native.Interop;

Console.OutputEncoding = Encoding.UTF8;

// Idioma antes de qualquer escrita: en-US é o padrão, --language=pt-BR troca.
L.Use(LanguageFromArgs(args));

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
    Formatting.WriteWarning("\n" + L.T("cli.cancelled"));
    return 5;
}
catch (VolumeAccessException ex)
{
    Formatting.WriteError($"\n{ex.Message}");
    return ex.Failure == VolumeAccessFailure.NeedsElevation ? 3 : 4;
}

static int Unknown(string command)
{
    Formatting.WriteError(L.T("cli.unknownCommand", command));
    Help.Print();
    return 2;
}

/// <summary>
/// Lê --language=xx dos argumentos. Fica solto aqui porque precisa rodar antes do
/// despacho de comandos — a mensagem de erro de um comando inválido já sai traduzida.
/// </summary>
static AppLanguage LanguageFromArgs(string[] args)
{
    foreach (string a in args)
    {
        if (!a.StartsWith("--language=", StringComparison.OrdinalIgnoreCase)) continue;

        string tag = a["--language=".Length..].Trim();
        return tag.ToLowerInvariant() switch
        {
            "pt" or "pt-br" or "portugues" or "português" => AppLanguage.Portuguese,
            "system" or "auto" => AppLanguage.System,
            _ => AppLanguage.English,
        };
    }

    return AppLanguage.English;
}

// ---------------------------------------------------------------------------

static class Help
{
    public static void Print()
    {
        Console.WriteLine();
        Console.WriteLine($"  {L.T("app.name")} — {L.T("app.tagline")}");
        Console.WriteLine($"  v0.2.0 · {L.T("settings.versionTitle", "0.2.0")}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.usage")}");
        Console.WriteLine("    vacuon <command> [options]");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.commands")}");
        Console.WriteLine($"    volumes                    {L.T("cli.cmdVolumes")}");
        Console.WriteLine($"    scan <drive|folder>        {L.T("cli.cmdScan")}");
        Console.WriteLine($"    security                   {L.T("cli.cmdSecurity")}");
        Console.WriteLine($"    thumb <file>               {L.T("cli.cmdThumb")}");
        Console.WriteLine($"    reveal <file>              {L.T("cli.cmdReveal")}");
        Console.WriteLine($"    version                    {L.T("cli.cmdVersion")}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.scanOptions")}");
        Console.WriteLine($"    --top=N                    {L.T("cli.optTop")}");
        Console.WriteLine($"    --strategy=auto|mft|walk   {L.T("cli.optStrategy")}");
        Console.WriteLine($"    --suspicious               {L.T("cli.optSuspicious")}");
        Console.WriteLine($"    --no-progress              {L.T("cli.optNoProgress")}");
        Console.WriteLine($"    --language=en-US|pt-BR     {L.T("cli.optLanguage")}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.securityOptions")}");
        Console.WriteLine($"    --all                      {L.T("cli.optAll")}");
        Console.WriteLine($"    --no-signatures            {L.T("cli.optNoSignatures")}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.thumbOptions")}");
        Console.WriteLine($"    --size=16..512             {L.T("cli.optSize")}");
        Console.WriteLine($"    --out=file.bmp             {L.T("cli.optOut")}");
        Console.WriteLine($"    --icon                     {L.T("cli.optIcon")}");
        Console.WriteLine();
        Console.WriteLine("  " + L.T("cli.elevationNote").Replace("\n", "\n  "));
        Console.WriteLine();
    }
}

static class Commands
{
    public static int Version()
    {
        Console.WriteLine("Vacuon 0.2.0");
        Console.WriteLine($".NET        {Environment.Version}");
        Console.WriteLine($"OS          {Environment.OSVersion.VersionString}");
        Console.WriteLine($"{L.T("cli.cores"),-11} {Environment.ProcessorCount}");
        Console.WriteLine($"{L.T("cli.elevated"),-11} {L.T(VolumeProbe.IsElevated() ? "cli.yes" : "cli.no")}");
        Console.WriteLine($"Language    {L.Culture.Name}");
        return 0;
    }

    public static int Volumes()
    {
        Formatting.WriteHeading(L.T("cli.headVolumes"));

        foreach (VolumeInfo v in VolumeProbe.EnumerateFixedVolumes())
        {
            double usedPercent = v.TotalBytes == 0 ? 0 : v.UsedBytes * 100.0 / v.TotalBytes;
            string bar = Bar(usedPercent, 24);

            Console.WriteLine($"  {v.DriveLetter}:    {Formatting.Truncate(v.Label, 22),-24} {v.FileSystem,-8} " +
                              $"{Formatting.Bytes(v.TotalBytes),12}   {bar} {usedPercent,5:N1}%    " +
                              $"{Formatting.Bytes(v.FreeBytes)} {L.T("volumes.freeOf").TrimEnd()}");
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
        Formatting.WriteHeading(L.T("cli.headScan", target));

        Console.WriteLine($"  {L.T("cli.labelStrategy"),-17} {L.T(result.StrategyUsed == ScanStrategy.Mft ? "scan.strategyMft" : "scan.strategyWalk")}");
        if (result.FallbackReason is not null)
            Formatting.WriteMuted($"                    {L.T("cli.labelFellBack", result.FallbackReason)}");

        int files = index.FileCount;
        int dirs = index.DirectoryCount;

        Console.WriteLine($"  {L.T("cli.labelTime"),-17} {Formatting.Duration(sw.Elapsed)}");
        Console.WriteLine($"  {L.T("cli.labelFiles"),-17} {Formatting.Count(files)}");
        Console.WriteLine($"  {L.T("cli.labelFolders"),-17} {Formatting.Count(dirs)}");
        if (sw.Elapsed.TotalSeconds > 0)
            Console.WriteLine($"  {L.T("cli.labelSpeed"),-17} {L.T("cli.labelFilesPerSecond", Formatting.Count((long)(files / sw.Elapsed.TotalSeconds)))}");

        bool hasRealAllocation = result.StrategyUsed == ScanStrategy.Mft;

        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.labelLogicalSize"),-17} {Formatting.Bytes(index.TotalLogicalBytes)}");

        if (hasRealAllocation)
        {
            Console.WriteLine($"  {L.T("cli.labelSizeOnDisk"),-17} {Formatting.Bytes(index.TotalBytesOnDisk)}");
            Console.WriteLine($"  {L.T("cli.labelSlack"),-17} {Formatting.Bytes(index.TotalSlackBytes)}  {L.T("cli.labelSlackNote")}");
        }
        else
        {
            // A API do Windows não expõe AllocatedSize: repetir o tamanho lógico aqui
            // faria o app afirmar "desperdício 0 B", que é falso e não medido.
            // Rótulo e mensagem separados: com o texto inteiro numa chave, a largura
            // do padding vinha de um idioma só e o outro saía desalinhado.
            Formatting.WriteMuted($"  {L.T("cli.labelSizeOnDisk"),-17} {L.T("cli.notMeasuredDisk")}");
            Formatting.WriteMuted($"  {L.T("cli.labelSlack"),-17} {L.T("cli.notMeasuredSame")}");
        }

        Console.WriteLine($"  {L.T("cli.labelVolume"),-17} {L.T("cli.labelUsedOf", Formatting.Bytes(index.Volume.UsedBytes), Formatting.Bytes(index.Volume.TotalBytes))}");

        // ------------------------------------------------------------------
        Formatting.WriteHeading(L.T("cli.headBiggestFiles", top));
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
        Formatting.WriteHeading(L.T("cli.headBiggestFolders", top));
        foreach (SizedItem item in SizeAnalyzer.TopFolders(index, top))
        {
            string path = index.GetFullPath(item.Index);
            Console.WriteLine($"  {Formatting.Bytes(item.LogicalSize),12}  {Formatting.Count(item.FileCount),9} {L.T("cli.labelFilesUnit")}  {Formatting.Truncate(path, 80)}");
        }

        // ------------------------------------------------------------------
        Formatting.WriteHeading(L.T("cli.headByType"));
        foreach (ExtensionBucket bucket in SizeAnalyzer.ByExtension(index, 15))
        {
            Console.WriteLine($"  {Formatting.Bytes(bucket.TotalBytes),12}  {Formatting.Count(bucket.Count),9} {L.T("cli.labelFilesUnit")}  " +
                              $"{bucket.DisplayExtension,-16} {bucket.Category}");
        }

        // ------------------------------------------------------------------
        Formatting.WriteHeading(L.T("cli.headBySize"));
        foreach (SizeBucket bucket in SizeAnalyzer.BySizeRange(index))
        {
            if (bucket.Count == 0) continue;

            string slack = hasRealAllocation
                ? $"   {L.T("cli.labelWaste")} {Formatting.Bytes(bucket.SlackBytes)}"
                : string.Empty;

            Console.WriteLine($"  {bucket.Label,-18} {Formatting.Count(bucket.Count),10} arq. " +
                              $"{Formatting.Bytes(bucket.TotalBytes),12}{slack}");
        }

        // ------------------------------------------------------------------
        Formatting.WriteHeading(L.T("cli.headByAge"));
        foreach (AgeBucket bucket in SizeAnalyzer.ByAge(index, DateTime.UtcNow))
        {
            if (bucket.Count == 0) continue;
            Console.WriteLine($"  {bucket.Label,-18} {Formatting.Count(bucket.Count),10} arq. {Formatting.Bytes(bucket.TotalBytes),12}");
        }

        // ------------------------------------------------------------------
        if (wantSuspicious)
        {
            Formatting.WriteHeading(L.T("cli.headSuspicious"));
            var analyzer = new SuspiciousFileAnalyzer();
            List<SuspiciousFile> suspicious = analyzer.Analyze(index, 40, cts.Token);

            if (suspicious.Count == 0)
            {
                Formatting.WriteMuted("  " + L.T("status.noSuspicious"));
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
                    Formatting.WriteMuted($"      {L.T("cli.labelModifiedOn", Formatting.Bytes(s.SizeBytes), s.ModifiedUtc.ToLocalTime().ToString("g", L.Culture))}");
                }

                Console.WriteLine();
                Formatting.WriteMuted("  " + L.T("cli.notAntivirusNote"));
                Formatting.WriteMuted("  " + L.T("cli.nothingChangedNote"));
            }
        }

        Console.WriteLine();
        return 0;
    }

    public static int Security(string[] args)
    {
        bool all = args.Contains("--all");
        bool signatures = !args.Contains("--no-signatures");

        Formatting.WriteHeading(L.T("cli.headSecurity"));

        if (!VolumeProbe.IsElevated())
            Formatting.WriteWarning("  " + L.T("cli.notElevatedWarning") + "\n");

        var scanner = new RegistryPersistenceScanner(new SecurityScanOptions
        {
            IncludeNormal = all,
            CheckSignatures = signatures,
        });

        SecurityReport report = scanner.Scan();

        Console.WriteLine($"  {L.T("cli.labelLocations"),-22} {report.LocationsInspected}");
        Console.WriteLine($"  {L.T("cli.labelEntries"),-22} {Formatting.Count(report.EntriesInspected)}");
        Console.WriteLine($"  {L.T("cli.labelTime"),-22} {Formatting.Duration(report.Elapsed)}");
        Console.WriteLine();

        int flagged = report.CountAtLeast(Suspicion.Notable);
        if (flagged == 0)
        {
            Formatting.WriteMuted("  " + L.T("cli.nothingFlagged"));
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
                string exists = L.T(f.TargetExists == true ? "security.exists" : "security.missing");
                string signer = f.Signer is null ? L.T("security.unsigned") : L.T("security.signedBy", f.Signer);
                _ = signer;
                Formatting.WriteMuted($"      {L.T("security.target")} {Formatting.Truncate(f.TargetPath, 90)} ({exists}, {signer})");
            }

            Console.WriteLine();
        }

        Formatting.WriteMuted("  " + L.T("cli.readOnlyFooter"));
        Formatting.WriteMuted("  " + L.T("cli.legitimateNote"));
        Console.WriteLine();
        return 0;
    }

    public static int Thumb(string[] args)
    {
        string? file = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (file is null || !File.Exists(file))
        {
            Formatting.WriteError(L.T("cli.needFile"));
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
            Formatting.WriteError(L.T("cli.noThumbnail"));
            return 1;
        }

        BmpWriter.Write(bitmap, output);

        Formatting.WriteHeading(L.T("cli.headThumbnail"));
        Console.WriteLine($"  {L.T("cli.labelFile"),-11} {file}");
        Console.WriteLine($"  {L.T("cli.labelCategory"),-11} {FileCategories.DisplayNameOf(Path.GetFileName(file).AsSpan())}");
        Console.WriteLine($"  {L.T("cli.labelSource"),-11} {L.T(bitmap.IsContentThumbnail ? "cli.labelSourceContent" : "cli.labelSourceIcon")}");
        Console.WriteLine($"  {L.T("cli.labelDimensions"),-11} {bitmap.Width} × {bitmap.Height} px");
        Console.WriteLine($"  {L.T("cli.labelTime"),-11} {Formatting.Duration(sw.Elapsed)}");
        Console.WriteLine($"  {L.T("cli.labelWrittenTo"),-11} {Path.GetFullPath(output)}");
        Console.WriteLine();
        return 0;
    }

    public static int Reveal(string[] args)
    {
        string? file = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (file is null)
        {
            Formatting.WriteError(L.T("cli.needPath"));
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
            ? $"  {value.Percent,5:N1}%  {Formatting.Count(value.EntriesFound)} · {value.MegabytesPerSecond,6:N0} MB/s"
            : $"  {Formatting.Count(value.RecordsParsed)} · {Formatting.Duration(value.Elapsed)}";

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
