using System.Diagnostics;
using System.Text;
using Vacuon.Cli;
using Vacuon.Core;
using Vacuon.Core.Actions;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Cleanup;
using Vacuon.Core.Index;
using Vacuon.Core.Localization;
using Vacuon.Core.Monitoring;
using Vacuon.Core.Optimization;
using Vacuon.Core.Preview;
using Vacuon.Core.Scan;
using Vacuon.Core.Scheduling;
using Vacuon.Core.Security;
using Vacuon.Native.Interop;
using Vacuon.Native.Ntfs;

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
        "ai" => Commands.Ai(),
        "startup" => Commands.Startup(),
        "quarantine" => Commands.Quarantine(args[1..]),
        "duplicates" or "dupes" => Commands.Duplicates(args[1..]),
        "clean" => Commands.Clean(args[1..]),
        "similar" => Commands.Similar(args[1..]),
        "watch" => Commands.Watch(args[1..]),
        "schedule" => Commands.Schedule(args[1..]),
        "guard" => Commands.Guard(args[1..]),
        "residue" => Commands.Residue(args[1..]),
        "compress" => Commands.Compress(args[1..]),
        "diff" => Commands.Diff(args[1..]),
        "media" => Commands.Media(args[1..]),
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
        Console.WriteLine($"  v{AppInfo.Version} · {L.T("settings.versionTitle", AppInfo.Version)}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.usage")}");
        Console.WriteLine("    vacuon <command> [options]");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.commands")}");
        Console.WriteLine($"    volumes                    {L.T("cli.cmdVolumes")}");
        Console.WriteLine($"    scan <drive|folder>        {L.T("cli.cmdScan")}");
        Console.WriteLine($"    security                   {L.T("cli.cmdSecurity")}");
        Console.WriteLine($"    ai                         {L.T("cli.cmdAi")}");
        Console.WriteLine($"    startup                    {L.T("cli.cmdStartup")}");
        Console.WriteLine($"    quarantine [list|…]        {L.T("cli.cmdQuarantine")}");
        Console.WriteLine($"    duplicates <drive|folder>  {L.T("cli.cmdDuplicates")}");
        Console.WriteLine($"    clean                      {L.T("cli.cmdClean")}");
        Console.WriteLine($"    similar <drive|folder>     {L.T("cli.cmdSimilar")}");
        Console.WriteLine($"    similar <drive> --video    {L.T("cli.cmdSimilarVideo")}");
        Console.WriteLine($"    watch <drive>              {L.T("cli.cmdWatch")}");
        Console.WriteLine($"    schedule [list|create|…]   {L.T("cli.cmdSchedule")}");
        Console.WriteLine($"    guard --below=10GB         {L.T("cli.cmdGuard")}");
        Console.WriteLine($"    residue <drive>            {L.T("cli.cmdResidue")}");
        Console.WriteLine($"    compress <drive>           {L.T("cli.cmdCompress")}");
        Console.WriteLine($"    diff <drive>               {L.T("cli.cmdDiff")}");
        Console.WriteLine($"    media <file>               {L.T("cli.cmdMedia")}");
        Console.WriteLine($"    thumb <file>               {L.T("cli.cmdThumb")}");
        Console.WriteLine($"    reveal <file>              {L.T("cli.cmdReveal")}");
        Console.WriteLine($"    version                    {L.T("cli.cmdVersion")}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.scanOptions")}");
        Console.WriteLine($"    --top=N                    {L.T("cli.optTop")}");
        Console.WriteLine($"    --strategy=auto|mft|walk   {L.T("cli.optStrategy")}");
        Console.WriteLine($"    --suspicious               {L.T("cli.optSuspicious")}");
        Console.WriteLine($"    --no-progress              {L.T("cli.optNoProgress")}");
        Console.WriteLine($"    --fresh                    {L.T("cli.optFresh")}");
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
        Console.WriteLine($"  {L.T("cli.scheduleOptions")}");
        Console.WriteLine($"    {L.T("cli.scheduleUsage")}");
        Console.WriteLine($"    --at=HH:MM                 {L.T("cli.optAt")}");
        Console.WriteLine($"    --frequency=daily|weekly|monthly {L.T("cli.optFrequency")}");
        Console.WriteLine($"    --below=10GB               {L.T("cli.optBelow")}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.watchOptions")}");
        Console.WriteLine($"    --every=N                  {L.T("cli.optEvery")}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.similarOptions")}");
        Console.WriteLine($"    --threshold=0..20          {L.T("cli.optThreshold")}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.cleanOptions")}");
        Console.WriteLine($"    --profile=quick|deep|custom {L.T("cli.optProfile")}");
        Console.WriteLine($"    --apply                    {L.T("cli.optApply")}");
        Console.WriteLine($"    --to=quarantine|recycle|permanent {L.T("cli.optTo")}");
        Console.WriteLine($"    --rule=<id>                {L.T("cli.optRule")}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.duplicateOptions")}");
        Console.WriteLine($"    --min-size=4KB             {L.T("cli.optMinSize")}");
        Console.WriteLine($"    --keep=oldest|newest|shallow {L.T("cli.optKeep")}");
        Console.WriteLine($"    --verify                   {L.T("cli.optVerify")}");
        Console.WriteLine();
        Console.WriteLine($"  {L.T("cli.quarantineOptions")}");
        Console.WriteLine($"    {L.T("cli.quarantineUsage")}");
        Console.WriteLine($"    --older-than=N             {L.T("cli.optOlderThan")}");
        Console.WriteLine($"    --yes                      {L.T("cli.optYes")}");
        Console.WriteLine();
        Console.WriteLine("  " + L.T("cli.elevationNote").Replace("\n", "\n  "));
        Console.WriteLine();
    }
}

static class Commands
{
    /// <summary>
    /// Lists the Microsoft AI components and their state. Reads only — switching anything off
    /// is a deliberate act and lives in the app, behind a confirmation and a change journal.
    /// </summary>
    public static int Ai()
    {
        AiScanReport report = new AiComponentScanner().Scan();

        Formatting.WriteHeading(L.T("cli.headAi"));

        foreach (AiComponentStatus s in report.Items)
        {
            string state = L.T(s.State switch
            {
                ComponentState.On => "ai.stateOn",
                ComponentState.Off => "ai.stateOff",
                ComponentState.Absent => "ai.stateAbsent",
                _ => "ai.stateUnknown",
            });

            Console.WriteLine();
            Console.WriteLine($"  {s.Component.Name}  [{state}]");
            Console.WriteLine($"    {s.Component.Description}");

            if (s.Component.DisplayPath.Length > 0)
                Console.WriteLine($"    {s.Component.DisplayPath}");

            // Measured, or plainly nothing. Never an estimate of what it "would" cost.
            Console.WriteLine("    " + (s.RunningProcesses > 0
                ? L.T("ai.measured", ByteSize.Format(s.MeasuredBytes), s.RunningProcesses)
                : L.T("ai.measuredNone")));

            if (!s.Component.IsActionable) Console.WriteLine($"    {L.T("ai.reportedOnly")}");
            if (s.Component.ReturnsAfterUpdate) Console.WriteLine($"    {L.T("ai.returnsAfterUpdate")}");
        }

        Console.WriteLine();
        Console.WriteLine("  " + (report.MeasuredBytes > 0
            ? L.T("ai.summary", report.Items.Count, report.OnCount, ByteSize.Format(report.MeasuredBytes))
            : L.T("ai.summaryNothingRunning", report.Items.Count, report.OnCount)));

        if (!report.WasElevated) Formatting.WriteWarning("  " + L.T("cli.notElevatedWarning"));

        Console.WriteLine();
        Formatting.WriteMuted("  " + L.T("cli.aiReadOnly"));
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Lists what Windows launches at sign-in. Reads only — switching one off lives in the app.
    /// </summary>
    public static int Startup()
    {
        StartupReport report = new StartupScanner().Scan();

        Formatting.WriteHeading(L.T("cli.headStartup"));

        foreach (StartupEntry e in report.Entries
                     .OrderByDescending(e => e.IsEnabled)
                     .ThenByDescending(e => e.MeasuredBytes))
        {
            string state = L.T(e.IsEnabled ? "startup.enabled" : "startup.disabled");

            Console.WriteLine();
            Console.WriteLine($"  {e.Name}  [{state}]  {e.SourceLabel}");
            Console.WriteLine($"    {e.Command}");
            Console.WriteLine("    " + (e.RunningProcesses > 0
                ? L.T("startup.measured", ByteSize.Format(e.MeasuredBytes), e.RunningProcesses)
                : L.T("startup.measuredNone")));

            if (e.TargetPath is not null && !e.TargetExists)
                Console.WriteLine($"    {L.T("startup.missingTarget")}");
        }

        Console.WriteLine();
        Console.WriteLine("  " + (report.MeasuredBytes > 0
            ? L.T("startup.summary", report.Entries.Count, report.EnabledCount, ByteSize.Format(report.MeasuredBytes))
            : L.T("startup.summaryNothingRunning", report.Entries.Count, report.EnabledCount)));

        if (!report.WasElevated) Formatting.WriteWarning("  " + L.T("cli.notElevatedWarning"));
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Lists, restores or purges quarantine batches.
    /// <para>
    /// Unlike <c>security</c> and <c>ai</c>, this one is not read-only — restore and purge
    /// both change the disk. Purge is the destructive half and the only step in the whole
    /// quarantine that cannot be undone, so it refuses to run without <c>--yes</c> rather
    /// than asking a question a scheduled task would never see.
    /// </para>
    /// </summary>
    public static int Quarantine(string[] args)
    {
        string sub = args.Length == 0 ? "list" : args[0].ToLowerInvariant();

        return sub switch
        {
            "list" => QuarantineList(),
            "restore" => QuarantineRestore(args[1..]),
            "purge" => QuarantinePurge(args[1..]),
            _ => QuarantineUnknown(sub),
        };
    }

    private static int QuarantineUnknown(string sub)
    {
        Formatting.WriteError(L.T("cli.quarantineUnknown", sub));
        Console.WriteLine("  " + L.T("cli.quarantineUsage"));
        return 2;
    }

    /// <summary>Every batch on every fixed volume, because quarantine is per volume.</summary>
    private static List<(string Volume, QuarantineBatch Batch)> AllBatches()
    {
        var service = new QuarantineService();
        var found = new List<(string, QuarantineBatch)>();

        foreach (VolumeInfo v in VolumeProbe.EnumerateFixedVolumes())
        {
            string root = v.DriveLetter + ":\\";
            foreach (QuarantineBatch batch in service.ListBatches(root))
                found.Add((root, batch));
        }

        found.Sort(static (a, b) => b.Item2.CreatedUtc.CompareTo(a.Item2.CreatedUtc));
        return found;
    }

    private static int QuarantineList()
    {
        Formatting.WriteHeading(L.T("cli.headQuarantine"));

        List<(string Volume, QuarantineBatch Batch)> batches = AllBatches();

        if (batches.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  " + L.T("quarantine.emptyAll"));
            Console.WriteLine();
            return 0;
        }

        long held = 0;
        var service = new QuarantineService();

        foreach ((string volume, QuarantineBatch batch) in batches)
        {
            // What the batch holds NOW, not what its manifest set out to hold. Items that
            // were restored are back under their original names and this batch is holding
            // nothing on their behalf.
            (long bytes, int present) = service.Held(batch);
            if (present == 0) continue;

            int days = (int)(DateTime.UtcNow - batch.CreatedUtc).TotalDays;
            string age = days <= 0 ? L.T("quarantine.ageToday") : L.T("quarantine.ageDays", days);
            string count = present == 1
                ? L.T("quarantine.itemCountOne")
                : L.T("quarantine.itemCount", present);

            held += bytes;

            Console.WriteLine();
            Console.WriteLine($"  {batch.BatchId}   {volume}");
            Console.WriteLine($"    {count} · {L.T("quarantine.held", Formatting.Bytes(bytes))} · {age}");
        }

        if (held == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  " + L.T("quarantine.emptyAll"));
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine();
        // "Held", never "freed": these bytes are still allocated on the volume.
        Console.WriteLine("  " + L.T("quarantine.held", Formatting.Bytes(held)));
        Formatting.WriteMuted("  " + L.T("quarantine.heldExplain"));
        Console.WriteLine();
        return 0;
    }

    private static int QuarantineRestore(string[] args)
    {
        if (args.Length == 0) return QuarantineUnknown("restore");

        string wanted = args[0];
        var match = AllBatches().FirstOrDefault(b =>
            b.Batch.BatchId.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        if (match.Batch is null)
        {
            Formatting.WriteError(L.T("cli.quarantineNoBatch", wanted));
            return 2;
        }

        IReadOnlyList<RestoreResult> results = new QuarantineService().Restore(match.Batch);

        int restored = results.Count(r => r.Succeeded);
        int failed = results.Count - restored;

        Console.WriteLine();
        foreach (RestoreResult r in results.Where(r => !r.Succeeded))
            Console.WriteLine($"  {DescribeRestore(r.Outcome)}  {r.OriginalPath}");

        Console.WriteLine();
        Console.WriteLine("  " + (failed > 0
            ? L.T("quarantine.restorePartial", restored, failed)
            : restored == 1
                ? L.T("quarantine.restoreDoneOne")
                : L.T("quarantine.restoreDone", restored)));
        Console.WriteLine();

        return failed > 0 ? 1 : 0;
    }

    private static int QuarantinePurge(string[] args)
    {
        if (args.Length == 0) return QuarantineUnknown("purge");

        bool confirmed = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
        string wanted = args[0];

        var match = AllBatches().FirstOrDefault(b =>
            b.Batch.BatchId.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        if (match.Batch is null)
        {
            Formatting.WriteError(L.T("cli.quarantineNoBatch", wanted));
            return 2;
        }

        if (!confirmed)
        {
            Formatting.WriteWarning("  " + L.T("quarantine.purgeTitle"));
            Console.WriteLine("  " + L.T("quarantine.purgeBody",
                Formatting.Bytes(new QuarantineService().Held(match.Batch).Bytes)));
            Console.WriteLine("  --yes");
            return 2;
        }

        long freed = new QuarantineService().Purge(match.Batch);

        Console.WriteLine();
        // Here, and only here, "freed" is the honest word — and it counts what actually
        // went away, so a batch already restored reports zero rather than its old size.
        Console.WriteLine("  " + (freed > 0
            ? L.T("quarantine.purgeDone", Formatting.Bytes(freed))
            : L.T("quarantine.purgeNothing")));
        Console.WriteLine();
        return 0;
    }

    private static string DescribeRestore(RestoreOutcome outcome) => L.T(outcome switch
    {
        RestoreOutcome.MissingFromQuarantine => "quarantine.outcomeMissing",
        RestoreOutcome.OriginalPathTaken => "quarantine.outcomeTaken",
        RestoreOutcome.InUse => "quarantine.outcomeInUse",
        RestoreOutcome.AccessDenied => "quarantine.outcomeAccessDenied",
        _ => "quarantine.outcomeFailed",
    });

    /// <summary>
    /// Finds files with identical content. Read-only: it never removes a copy, it only says
    /// which ones are the same and what removing the redundant ones would actually free.
    /// </summary>
    public static int Duplicates(string[] args)
    {
        string target = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "C:";
        int top = ArgInt(args, "--top", 20);

        var options = new DuplicateOptions
        {
            MinimumBytes = ArgSize(args, "--min-size", 4096),
            VerifyByteForByte = args.Contains("--verify"),
            Keep = ArgString(args, "--keep", "oldest").ToLowerInvariant() switch
            {
                "newest" => KeepPreference.Newest,
                "shallow" or "shallowest" => KeepPreference.ShallowestPath,
                _ => KeepPreference.Oldest,
            },
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        bool showProgress = !args.Contains("--no-progress") && !Console.IsOutputRedirected;
        var orchestrator = new ScanOrchestrator(new MftScanOptions
        {
            Progress = showProgress ? new ConsoleProgress() : null,
        });

        ScanResult scan = IsWholeVolume(target)
            ? orchestrator.Refresh(char.ToUpperInvariant(target[0]), StrategyPreference.Auto,
                                   allowSnapshot: !args.Contains("--fresh"), cts.Token)
            : orchestrator.ScanFolder(target, cts.Token);

        if (showProgress) ConsoleProgress.Clear();

        Formatting.WriteHeading(L.T("cli.headDuplicates"));

        var watch = Stopwatch.StartNew();
        DuplicateReport report = new DuplicateFinder().Find(scan.Index, options, null, cts.Token);
        watch.Stop();

        if (report.GroupCount == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  " + L.T("dup.none"));
            Console.WriteLine();
            return 0;
        }

        foreach (DuplicateGroup group in report.Groups.Take(top))
        {
            Console.WriteLine();
            Console.WriteLine("  " + L.T("dup.groupHeader",
                                         Formatting.Count(group.CopyCount),
                                         Formatting.Bytes(group.Bytes),
                                         Formatting.Bytes(group.RecoverableBytes)));

            // The keeper is printed first and labelled, so the list never reads as though
            // every path on it were up for deletion.
            Console.WriteLine($"    [{L.T("dup.keeping")}]  {group.Keeper.Path}");

            foreach (DuplicateFile copy in group.Redundant)
            {
                Console.WriteLine($"    [{L.T("dup.redundant")}]  {copy.Path}");
                if (copy.IsHardLinked) Formatting.WriteMuted($"             {L.T("dup.hardlinked")}");
            }

            if (group.RecoverableBytes == 0)
                Formatting.WriteMuted("    " + L.T("dup.nothingRecoverable"));
        }

        Console.WriteLine();
        // Singular matters: "1 groups" is the kind of seam that makes a tool look unfinished,
        // and this project has already had to fix it once for items and folders.
        Console.WriteLine("  " + (report.GroupCount == 1
            ? L.T("dup.summaryOne", Formatting.Bytes(report.RecoverableBytes))
            : L.T("dup.summary", Formatting.Count(report.GroupCount),
                  Formatting.Bytes(report.RecoverableBytes))));
        Formatting.WriteMuted("  " + L.T("dup.read", Formatting.Bytes(report.BytesRead),
                                         Formatting.Count(report.FilesHashed)) +
                              $"  ·  {Formatting.Duration(watch.Elapsed)}");

        if (report.HardLinkedCopies > 0)
            Formatting.WriteMuted("  " + L.T("dup.hardlinkNote",
                                             Formatting.Count(report.HardLinkedCopies)));

        if (report.UnreadableFiles > 0)
            Formatting.WriteMuted("  " + L.T("dup.unreadable",
                                             Formatting.Count(report.UnreadableFiles)));

        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Rule-based cleanup.
    /// <para>
    /// Dry-run unless <c>--apply</c> is passed, and that is not a courtesy: this command can
    /// be pointed at a whole machine by someone who has not read the rules, so the default
    /// has to be the one that cannot cost anything.
    /// </para>
    /// </summary>
    public static int Clean(string[] args)
    {
        bool apply = args.Contains("--apply");
        string? onlyRule = args.FirstOrDefault(a => a.StartsWith("--rule=", StringComparison.OrdinalIgnoreCase))?[7..];

        CleanupProfile profile = ArgString(args, "--profile", "quick").ToLowerInvariant() switch
        {
            "deep" => CleanupProfile.Deep,
            "custom" => CleanupProfile.Custom,
            _ => CleanupProfile.Quick,
        };

        CleanupDisposal disposal = ArgString(args, "--to", "quarantine").ToLowerInvariant() switch
        {
            "recycle" or "recyclebin" => CleanupDisposal.RecycleBin,
            "permanent" => CleanupDisposal.Permanent,
            _ => CleanupDisposal.Quarantine,
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        RuleCatalog.CatalogLoad catalog = RuleCatalog.LoadWithProblems();
        IReadOnlyList<CleanupRule> rules = catalog.Rules;

        // Said out loud, never swallowed: a rules.json with one bad escape is unreadable,
        // and the only other symptom is that editing it changes nothing.
        foreach (string problem in catalog.Problems)
            Formatting.WriteWarning("  " + L.T("cleanup.catalogProblem", problem));

        if (onlyRule is not null)
            rules = [.. rules.Where(r => r.Id.Equals(onlyRule, StringComparison.OrdinalIgnoreCase))];

        bool elevated = VolumeProbe.IsElevated();
        var engine = new RuleEngine();

        Formatting.WriteHeading(L.T("cli.headClean"));

        CleanupPlan plan = engine.Plan(rules, profile, elevated, cts.Token);

        int needElevation = 0;

        foreach (RulePlan rule in plan.Rules)
        {
            if (rule.Skipped == RuleSkipReason.NeedsElevation) needElevation++;

            // Rules that matched nothing and rules the profile excluded are not worth a
            // line each; the ones that could have run but did not are.
            if (rule.Skipped is RuleSkipReason.NothingMatched or RuleSkipReason.RiskAboveProfile)
                continue;

            Console.WriteLine();
            Console.WriteLine($"  {rule.Rule.Name}  [{RiskLabel(rule.Rule.Risk)}]");
            Formatting.WriteMuted($"    {rule.Rule.Description}");

            if (rule.Skipped != RuleSkipReason.None)
            {
                Formatting.WriteWarning("    " + SkipLabel(rule));
                continue;
            }

            if (rule.Rule.IsSystemTool)
            {
                // No byte figure here on purpose: the tool reports what it freed only after
                // it has run, and quoting the catalog's range would be someone else's disk.
                Formatting.WriteMuted("    " + L.T("cleanup.toolWillRun", rule.Rule.Tool ?? "?"));
                continue;
            }

            Console.WriteLine($"    {Formatting.Count(rule.Matches.Count)} · {Formatting.Bytes(rule.Bytes)}");
        }

        Console.WriteLine();

        if (plan.FileCount == 0 && plan.SystemTools.Count == 0)
        {
            Console.WriteLine("  " + L.T("cleanup.planNothing"));
            Console.WriteLine();
            return 0;
        }

        int active = plan.Rules.Count(r => r.WillDoSomething);
        Console.WriteLine("  " + (active == 1
            ? L.T("cleanup.planSummaryOne",
                  Formatting.Count(plan.FileCount), Formatting.Bytes(plan.MatchedBytes))
            : L.T("cleanup.planSummary",
                  Formatting.Count(plan.FileCount), Formatting.Bytes(plan.MatchedBytes),
                  Formatting.Count(active))));

        if (needElevation > 0)
            Formatting.WriteMuted("  " + L.T("cleanup.needsElevationNote", Formatting.Count(needElevation)));

        if (!apply)
        {
            Console.WriteLine();
            Formatting.WriteMuted("  " + L.T("cleanup.dryRun"));
            Console.WriteLine();
            return 0;
        }

        CleanupReport report = engine.Execute(plan, disposal, cts.Token);

        Console.WriteLine();
        Console.WriteLine("  " + (report.Failed > 0
            ? L.T("cleanup.donePartial", Formatting.Count(report.Handled), Formatting.Count(report.Failed))
            : disposal switch
            {
                CleanupDisposal.Permanent => L.T("cleanup.donePermanent",
                    Formatting.Count(report.Handled), Formatting.Bytes(report.Bytes)),
                CleanupDisposal.RecycleBin => L.T("cleanup.doneRecycle",
                    Formatting.Count(report.Handled), Formatting.Bytes(report.Bytes)),
                _ => L.T("cleanup.doneQuarantine",
                    Formatting.Count(report.Handled), Formatting.Bytes(report.Bytes)),
            }));

        // System tools run after the files, and each one reports its own measured gain.
        var tools = new SystemTools();

        foreach (RulePlan rule in plan.SystemTools)
        {
            Console.WriteLine();
            Console.WriteLine($"  {rule.Rule.Name}");

            ToolResult result = tools.Run(rule.Rule.Tool!, "C:\\", cts.Token);

            if (!result.Succeeded)
            {
                Formatting.WriteWarning("    " + L.T("cleanup.toolFailed",
                                                     result.Error ?? $"exit {result.ExitCode}"));
                continue;
            }

            Console.WriteLine("    " + (result.FreedBytesMeasured
                ? L.T("cleanup.toolFreed", Formatting.Bytes(result.FreedBytes))
                : L.T("cleanup.toolNoMeasure")));
        }

        Console.WriteLine();
        return report.Failed > 0 ? 1 : 0;
    }

    private static string RiskLabel(CleanupRisk risk) => L.T(risk switch
    {
        CleanupRisk.Caution => "cleanup.riskCaution",
        CleanupRisk.Dangerous => "cleanup.riskDangerous",
        _ => "cleanup.riskSafe",
    });

    private static string SkipLabel(RulePlan plan) => plan.Skipped switch
    {
        RuleSkipReason.ProcessRunning => L.T("cleanup.skipProcess", plan.SkipDetail ?? "?"),
        RuleSkipReason.NeedsElevation => L.T("cleanup.skipElevation"),
        RuleSkipReason.RiskAboveProfile => L.T("cleanup.skipRisk"),
        _ => L.T("cleanup.skipNothing"),
    };

    /// <summary>
    /// Prints what Windows knows about a media file. Read-only, and it decodes nothing —
    /// the answers come from the same property handlers Explorer's details pane uses.
    /// </summary>
    public static int Media(string[] args)
    {
        string? target = args.FirstOrDefault(a => !a.StartsWith('-'));

        if (target is null || !File.Exists(target))
        {
            Formatting.WriteError(L.T("cli.needFile"));
            return 2;
        }

        MediaInfo info = MediaProbe.Read(target);

        Formatting.WriteHeading(L.T("cli.headMedia"));
        Console.WriteLine();
        Console.WriteLine($"  {Path.GetFileName(target)}");
        Console.WriteLine();

        if (info.IsEmpty)
        {
            // Said plainly rather than printed as a row of dashes: nothing known is a
            // different statement from nothing there.
            Formatting.WriteMuted("  " + L.T("media.nothing"));
            Console.WriteLine();
            return 0;
        }

        void Row(string labelKey, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            Console.WriteLine($"  {L.T(labelKey),-22} {value}");
        }

        Row("media.duration", info.Duration?.ToString(@"hh\:mm\:ss"));
        Row("media.resolution", info.Dimensions is null
            ? null
            : $"{info.Dimensions}  ({info.ResolutionLabel})");
        Row("media.frameRate", info.FrameRate is null ? null : L.T("media.fps", info.FrameRate.Value.ToString("N3").TrimEnd('0').TrimEnd('.', ',')));
        Row("media.videoCodec", info.VideoCodec);
        Row("media.videoBitrate", info.VideoBitrate is null ? null : L.T("media.perSecond", Formatting.Bytes((long)(info.VideoBitrate.Value / 8))));
        Row("media.audioCodec", info.AudioCodec);
        Row("media.audioBitrate", info.AudioBitrate is null ? null : L.T("media.perSecond", Formatting.Bytes((long)(info.AudioBitrate.Value / 8))));
        Row("media.sampleRate", info.SampleRate is null ? null : $"{info.SampleRate} Hz");
        Row("media.channels", info.Channels?.ToString());
        Row("media.camera", info.CameraModel);
        Row("media.dateTaken", info.DateTaken?.ToString("yyyy-MM-dd HH:mm"));

        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Finds pictures that look alike. Read-only: it names versions, it removes nothing.
    /// </summary>
    public static int Similar(string[] args)
    {
        string target = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "C:";
        int top = ArgInt(args, "--top", 20);

        var options = new NearDuplicateOptions
        {
            Threshold = Math.Clamp(ArgInt(args, "--threshold", PerceptualHash.DefaultThreshold), 0, 20),
            MinimumBytes = ArgSize(args, "--min-size", 16 * 1024),
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        bool showProgress = !args.Contains("--no-progress") && !Console.IsOutputRedirected;
        var orchestrator = new ScanOrchestrator(new MftScanOptions
        {
            Progress = showProgress ? new ConsoleProgress() : null,
        });

        ScanResult scan = IsWholeVolume(target)
            ? orchestrator.Refresh(char.ToUpperInvariant(target[0]), StrategyPreference.Auto,
                                   allowSnapshot: !args.Contains("--fresh"), cts.Token)
            : orchestrator.ScanFolder(target, cts.Token);

        if (showProgress) ConsoleProgress.Clear();

        // Videos are a separate finder, not a mode of this one: a picture is one fingerprint
        // and a video is several, and two videos are only comparable when their running times
        // agree. The flag chooses which question is being asked.
        if (args.Contains("--video")) return SimilarVideos(scan, args, top, cts.Token);

        Formatting.WriteHeading(L.T("cli.headSimilar"));

        SimilarReport report = new NearDuplicateFinder().Find(scan.Index, options, null, cts.Token);

        foreach (SimilarGroup group in report.Groups.Take(top))
        {
            int spread = group.Spread;

            Console.WriteLine();
            Console.WriteLine("  " + (spread == 0
                ? L.T("similar.identical", Formatting.Count(group.Images.Count),
                      Formatting.Bytes(group.RecoverableBytes))
                : L.T("similar.groupHeader", Formatting.Count(group.Images.Count),
                      Formatting.Bytes(group.RecoverableBytes), Formatting.Count(spread))));

            // The keeper first and labelled: these files are genuinely different, so the
            // list must never read as though every path on it were interchangeable.
            Console.WriteLine($"    [{L.T("similar.keeping")}]     {Describe(group.Keeper)}");

            foreach (SimilarImage other in group.Others)
                Console.WriteLine($"    [{L.T("similar.other")}]  {Describe(other)}");
        }

        Console.WriteLine();

        if (report.Groups.Count == 0)
        {
            Console.WriteLine("  " + L.T("similar.none"));
        }
        else
        {
            Console.WriteLine("  " + (report.Groups.Count == 1
                ? L.T("similar.summaryOne", Formatting.Bytes(report.RecoverableBytes))
                : L.T("similar.summary", Formatting.Count(report.Groups.Count),
                      Formatting.Bytes(report.RecoverableBytes))));
        }

        Formatting.WriteMuted("  " + L.T("similar.fingerprinted",
                                         Formatting.Count(report.ImagesFingerprinted)));

        // Said out loud: a file the shell answered with an icon was never compared, and
        // silently dropping it would look like the app decided it was unique.
        if (report.ImagesSkipped > 0)
            Formatting.WriteMuted("  " + L.T("similar.skipped", Formatting.Count(report.ImagesSkipped)));

        if (report.ImagesBelowMinimum > 0)
            Formatting.WriteMuted("  " + L.T("similar.belowMinimum",
                                             Formatting.Count(report.ImagesBelowMinimum)));

        if (report.FromCache > 0)
            Formatting.WriteMuted("  " + L.T("similar.fromCache", Formatting.Count(report.FromCache)));

        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// The same question asked of videos, which needs different evidence to answer.
    /// </summary>
    private static int SimilarVideos(ScanResult scan, string[] args, int top, CancellationToken token)
    {
        var options = new VideoDuplicateOptions
        {
            Threshold = Math.Clamp(ArgInt(args, "--threshold", VideoSimilarity.FrameThreshold), 0, 32),
            MinimumBytes = ArgSize(args, "--min-size", 4L * 1024 * 1024),
            UseCache = !args.Contains("--no-cache"),
        };

        var finder = new VideoDuplicateFinder();

        Formatting.WriteHeading(L.T("cli.headSimilarVideo"));

        // Said before the reading starts, not after: decoding frames out of every video on a
        // volume is minutes, and a run that begins with no number beside it is one nobody
        // agreed to.
        VideoScope scope = finder.Scope(scan.Index, options);

        Console.WriteLine();
        Console.WriteLine("  " + L.T("similarVideo.scope", Formatting.Count(scope.Candidates),
                                     Formatting.Bytes(scope.CandidateBytes)));

        VideoSimilarReport report = finder.Find(scan.Index, options, null, token);

        foreach (VideoGroup group in report.Groups.Take(top))
        {
            int spread = group.Spread;

            Console.WriteLine();
            Console.WriteLine("  " + (spread == 0
                ? L.T("similar.identical", Formatting.Count(group.Videos.Count),
                      Formatting.Bytes(group.RecoverableBytes))
                : L.T("similar.groupHeader", Formatting.Count(group.Videos.Count),
                      Formatting.Bytes(group.RecoverableBytes), Formatting.Count(spread))));

            Console.WriteLine($"    [{L.T("similar.keeping")}]     {Describe(group.Keeper)}");

            foreach (SimilarVideo other in group.Others)
                Console.WriteLine($"    [{L.T("similar.other")}]  {Describe(other)}");
        }

        Console.WriteLine();

        if (report.Groups.Count == 0)
        {
            Console.WriteLine("  " + L.T("similarVideo.none"));
        }
        else
        {
            Console.WriteLine("  " + (report.Groups.Count == 1
                ? L.T("similar.summaryOne", Formatting.Bytes(report.RecoverableBytes))
                : L.T("similar.summary", Formatting.Count(report.Groups.Count),
                      Formatting.Bytes(report.RecoverableBytes))));
        }

        Formatting.WriteMuted("  " + L.T("similarVideo.fingerprinted",
                                         Formatting.Count(report.Fingerprinted)));

        // A video this machine has no decoder for was never compared. Dropping it in silence
        // would read as the app having looked at it and found it unique.
        if (report.Unreadable > 0)
            Formatting.WriteMuted("  " + L.T("similarVideo.unreadable", Formatting.Count(report.Unreadable)));

        if (report.TooShort > 0)
            Formatting.WriteMuted("  " + L.T("similarVideo.tooShort", Formatting.Count(report.TooShort),
                                             (int)VideoSimilarity.MinimumDuration.TotalSeconds));

        if (report.FromCache > 0)
            Formatting.WriteMuted("  " + L.T("similar.fromCache", Formatting.Count(report.FromCache)));

        Formatting.WriteMuted("  " + L.T("similarVideo.noAudio"));

        Console.WriteLine();
        return 0;
    }

    private static string Describe(SimilarVideo video)
    {
        string label = video.ResolutionLabel is null
            ? Formatting.Bytes(video.SizeBytes)
            : $"{video.ResolutionLabel,-7} {Formatting.Bytes(video.SizeBytes)}";

        return $"{label,-20} {video.Duration.ToString(@"hh\:mm\:ss")}  {video.Path}";
    }

    private static string Describe(SimilarImage image)
    {
        string size = image.ResolutionLabel is null
            ? Formatting.Bytes(image.Bytes)
            : $"{image.ResolutionLabel,-7} {Formatting.Bytes(image.Bytes)}";

        return $"{size,-20} {image.Path}";
    }

    /// <summary>
    /// Watches the change journal and prints what is being written, folder by folder.
    /// <para>
    /// Read-only, and it answers <b>where</b>, never <b>who</b>: a USN record has no process
    /// in it. The footer says so on every run rather than letting the reader assume.
    /// </para>
    /// </summary>
    public static int Watch(string[] args)
    {
        string target = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "C:";
        char drive = char.ToUpperInvariant(target.TrimStart()[0]);
        int every = Math.Clamp(ArgInt(args, "--every", 5), 1, 3600);
        int top = ArgInt(args, "--top", 10);

        if (!VolumeProbe.IsElevated())
        {
            Formatting.WriteError(L.T("watch.needsElevation"));
            return 3;
        }

        using DiskMonitor? monitor = DiskMonitor.Start(drive);

        if (monitor is null)
        {
            Formatting.WriteError(L.T("watch.noJournal"));
            return 4;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Formatting.WriteHeading(L.T("cli.headWatch", drive + ":"));
        Console.WriteLine();
        Formatting.WriteMuted("  " + L.T("watch.noProcess"));

        try
        {
            while (!cts.IsCancellationRequested)
            {
                Task.Delay(TimeSpan.FromSeconds(every), cts.Token).GetAwaiter().GetResult();

                ActivitySnapshot snapshot = monitor.Poll(cts.Token);

                Console.WriteLine();
                Console.WriteLine($"  {DateTime.Now:HH:mm:ss}");

                // Before the quiet line, because a gap produces the same empty snapshot and
                // must not be reported as nothing having happened.
                if (snapshot.JournalGap)
                {
                    Formatting.WriteWarning("    " + L.T("watch.gap"));
                    continue;
                }

                if (snapshot.RecordsRead == 0)
                {
                    Formatting.WriteMuted("    " + L.T("watch.quiet"));
                    continue;
                }

                // Free space is signed on purpose: a volume that gained space is as
                // interesting as one losing it, and hiding the sign would flatten both.
                string delta = snapshot.FreeBytesDelta == 0
                    ? string.Empty
                    : (snapshot.FreeBytesDelta > 0 ? "+" : "-")
                      + Formatting.Bytes(Math.Abs(snapshot.FreeBytesDelta));

                Console.WriteLine("    " + L.T("watch.header",
                                               Formatting.Count(snapshot.RecordsRead),
                                               Formatting.Count(snapshot.Folders.Count),
                                               Formatting.Bytes(snapshot.FreeBytes),
                                               delta));

                foreach (FolderActivity folder in snapshot.Folders.Take(top))
                {
                    string bytes = folder.BytesAdded > 0
                        ? $"  {Formatting.Bytes(folder.BytesAdded)}"
                        : string.Empty;

                    Console.WriteLine($"      {Formatting.Truncate(folder.Folder, 64),-66}{bytes}");
                    Formatting.WriteMuted("        " + L.T("watch.counts",
                        Formatting.Count(folder.Created),
                        Formatting.Count(folder.Deleted),
                        Formatting.Count(folder.Modified)));
                }
            }
        }
        catch (OperationCanceledException) { }

        Console.WriteLine();
        Formatting.WriteMuted("  " + L.T("watch.stopping"));
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Creates, lists and removes the scheduled cleanups Vacuon owns.
    /// <para>
    /// Every task it creates runs with <c>--to=quarantine</c>, and that is not configurable
    /// here: an unattended run is the one case where nobody can stop a mistake, so it only
    /// gets the reversible route. The command prints that sentence before creating anything.
    /// </para>
    /// </summary>
    public static int Schedule(string[] args)
    {
        string action = args.Length == 0 ? "list" : args[0].ToLowerInvariant();
        var scheduler = new ScheduledCleanup();

        Formatting.WriteHeading(L.T("cli.headSchedule"));

        switch (action)
        {
            case "list":
            {
                ScheduleListing listing = scheduler.List();

                Console.WriteLine();

                if (!listing.Succeeded)
                {
                    // Not the same sentence as "nothing scheduled": the app did not find out.
                    Formatting.WriteError("  " + L.T("schedule.unreadable", listing.Error ?? string.Empty));
                    return 1;
                }

                if (listing.Tasks.Count == 0)
                {
                    Console.WriteLine("  " + L.T("schedule.none"));
                    Console.WriteLine();
                    return 0;
                }

                foreach (ScheduledTask task in listing.Tasks)
                {
                    Console.WriteLine($"  {task.Name}");
                    Formatting.WriteMuted($"    {task.Schedule}  ·  {L.T("schedule.nextRun", task.NextRun)}  ·  {task.Status}");
                    Formatting.WriteMuted($"    {task.Command}");
                }

                Console.WriteLine();
                return 0;
            }

            case "create":
            {
                if (!VolumeProbe.IsElevated())
                {
                    Formatting.WriteError(L.T("schedule.needsElevation"));
                    return 3;
                }

                CleanupProfile profile = ArgString(args, "--profile", "quick").ToLowerInvariant() switch
                {
                    "deep" => CleanupProfile.Deep,
                    "custom" => CleanupProfile.Custom,
                    _ => CleanupProfile.Quick,
                };

                ScheduleFrequency frequency = ArgString(args, "--frequency", "daily").ToLowerInvariant() switch
                {
                    "weekly" => ScheduleFrequency.Weekly,
                    "monthly" => ScheduleFrequency.Monthly,
                    _ => ScheduleFrequency.Daily,
                };

                TimeOnly at = TimeOnly.TryParse(ArgString(args, "--at", "03:00"), out TimeOnly parsed)
                    ? parsed
                    : new TimeOnly(3, 0);

                string exe = Environment.ProcessPath ?? "vacuon.exe";

                ScheduleResult result = scheduler.Create(exe, frequency, at, profile);

                Console.WriteLine();

                if (!result.Succeeded)
                {
                    // Before the preview, not after: Build falls back to the quick profile
                    // for anything it will not schedule, so printing the command line first
                    // would show a run that is never going to happen.
                    Formatting.WriteError("  " + L.T("schedule.failed", result.Error ?? result.Output));
                    return 1;
                }

                Formatting.WriteMuted("  " + L.T("schedule.alwaysQuarantine"));
                Console.WriteLine();
                Console.WriteLine("  " + L.T("schedule.willRun", ScheduledCleanup.Build(exe, profile)));
                Console.WriteLine();

                Console.WriteLine("  " + L.T("schedule.created", $"{frequency} {at:HH\\:mm}"));
                Console.WriteLine();
                return 0;
            }

            case "delete":
            {
                string? name = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

                if (name is null)
                {
                    Formatting.WriteError(L.T("cli.scheduleUsage"));
                    return 2;
                }

                ScheduleResult result = scheduler.Delete(name);

                Console.WriteLine();

                if (!result.Succeeded)
                {
                    Formatting.WriteError("  " + L.T("schedule.failed", result.Error ?? result.Output));
                    return 1;
                }

                Console.WriteLine("  " + L.T("schedule.deleted", name));
                Console.WriteLine();
                return 0;
            }

            default:
                Formatting.WriteError(L.T("cli.scheduleUsage"));
                return 2;
        }
    }

    /// <summary>
    /// Compares free space against a threshold and says so in the exit code.
    /// <para>
    /// It changes nothing, on purpose. A guard that also cleaned would be a timer that
    /// deletes when a number moves — the response belongs in a second, deliberate step, and
    /// the exit code is how a scheduler asks for one.
    /// </para>
    /// </summary>
    public static int Guard(string[] args)
    {
        long threshold = ArgSize(args, "--below", 10L * 1024 * 1024 * 1024);
        string? drive = args.FirstOrDefault(a => !a.StartsWith('-'));

        char? letter = drive is { Length: > 0 } ? char.ToUpperInvariant(drive[0]) : null;

        GuardReport report = SpaceGuard.Check(threshold, letter);

        Formatting.WriteHeading(L.T("cli.headGuard"));
        Console.WriteLine();

        foreach (GuardReading volume in report.Volumes)
        {
            string line = volume.BelowThreshold
                ? L.T("guard.below", volume.DriveLetter + ":",
                      Formatting.Bytes(volume.FreeBytes), Formatting.Bytes(volume.TotalBytes),
                      volume.FreePercent.ToString("N1"), Formatting.Bytes(threshold),
                      Formatting.Bytes(volume.Shortfall))
                : L.T("guard.above", volume.DriveLetter + ":",
                      Formatting.Bytes(volume.FreeBytes), Formatting.Bytes(volume.TotalBytes),
                      volume.FreePercent.ToString("N1"), Formatting.Bytes(threshold));

            if (volume.BelowThreshold) Formatting.WriteWarning("  " + line);
            else Console.WriteLine("  " + line);
        }

        Console.WriteLine();
        Formatting.WriteMuted("  " + L.T("guard.noAction"));
        Console.WriteLine();

        // 6 is not an error: it is the answer. A scheduler branches on it.
        return report.AnyBreached ? 6 : 0;
    }

    /// <summary>
    /// Folders under the user roots that nothing installed claims.
    /// <para>
    /// Read-only, and every row is a guess built on a name. It says so at the end rather than
    /// leaving somebody to read the list as a verdict.
    /// </para>
    /// </summary>
    public static int Residue(string[] args)
    {
        string target = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "C:";
        int top = ArgInt(args, "--top", 30);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        ScanResult scan = ScanFor(target, args, cts.Token);

        Formatting.WriteHeading(L.T("cli.headResidue"));

        ResidueReport report = UninstallResidue.Find(
            scan.Index,
            ArgSize(args, "--min-size", UninstallResidue.MinimumBytes),
            TimeSpan.FromDays(ArgInt(args, "--older-than", (int)UninstallResidue.MinimumAge.TotalDays)));

        Console.WriteLine();

        foreach (Residue residue in report.Residues.Take(top))
        {
            Console.WriteLine($"  {Formatting.Bytes(residue.Bytes),12}  {residue.Folder}");
            Formatting.WriteMuted("                " + L.T("residue.detail",
                Formatting.Count(residue.FileCount), (int)residue.Age.TotalDays));
        }

        Console.WriteLine();

        Console.WriteLine("  " + (report.Residues.Count == 0
            ? L.T("residue.none")
            : L.T("residue.summary", Formatting.Count(report.Residues.Count),
                  Formatting.Bytes(report.Bytes))));

        Formatting.WriteMuted("  " + L.T("residue.read", Formatting.Count(report.InstalledProgramsRead),
                                         Formatting.Count(report.FoldersExamined)));

        // The caveat is the point, not a footnote: this matches names, and a name is not an
        // identity. Nothing was deleted and nothing here is a recommendation to delete.
        Formatting.WriteMuted("  " + L.T("residue.guess"));

        Console.WriteLine();
        return 0;
    }

    /// <summary>Folders NTFS would compress well, that are not compressed.</summary>
    public static int Compress(string[] args)
    {
        string target = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "C:";
        int top = ArgInt(args, "--top", 20);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        ScanResult scan = ScanFor(target, args, cts.Token);

        Formatting.WriteHeading(L.T("cli.headCompress"));

        CompressionReport report = CompressionCandidates.Find(
            scan.Index, ArgSize(args, "--min-size", CompressionCandidates.MinimumFolderBytes));

        Console.WriteLine();

        foreach (CompressionCandidate candidate in report.Candidates.Take(top))
        {
            Console.WriteLine($"  {Formatting.Bytes(candidate.Bytes),12}  {candidate.Folder}");
            Formatting.WriteMuted("                " + L.T("compress.detail",
                Formatting.Count(candidate.FileCount), L.T(candidate.Category),
                Formatting.Bytes(candidate.EstimatedSaving)));
        }

        Console.WriteLine();

        Console.WriteLine("  " + (report.Candidates.Count == 0
            ? L.T("compress.none")
            : L.T("compress.summary", Formatting.Count(report.Candidates.Count),
                  Formatting.Bytes(report.EstimatedSaving))));

        if (report.AlreadyCompressed > 0)
            Formatting.WriteMuted("  " + L.T("compress.already", Formatting.Count(report.AlreadyCompressed),
                                             Formatting.Bytes(report.AlreadyCompressedBytes)));

        // The only figure in this application that was not measured, labelled where it is read.
        Formatting.WriteMuted("  " + L.T("compress.estimate"));
        Formatting.WriteMuted("  " + L.T("compress.howTo"));

        Console.WriteLine();
        return 0;
    }

    /// <summary>What changed between the stored snapshot and the volume as it is now.</summary>
    public static int Diff(string[] args)
    {
        string target = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "C:";
        int top = ArgInt(args, "--top", 25);

        if (!IsWholeVolume(target))
        {
            Formatting.WriteError(L.T("diff.needsVolume"));
            return 2;
        }

        char letter = char.ToUpperInvariant(target[0]);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Formatting.WriteHeading(L.T("cli.headDiff"));

        // The snapshot is keyed by the NTFS volume serial, which lives in the volume metadata
        // and needs the device open to read. GetVolumeInformation's serial is a different,
        // 32-bit number and would name a snapshot that does not exist — which it quietly did,
        // reporting "no earlier scan" on a machine that had one sitting on disk.
        long serial;

        try
        {
            using VolumeDevice device = VolumeDevice.Open(letter);
            serial = device.SerialNumber;
        }
        catch (Exception ex) when (ex is VolumeAccessException or UnauthorizedAccessException or IOException)
        {
            Formatting.WriteError(L.T("diff.needsElevation"));
            return 3;
        }

        LoadedSnapshot? before = IndexSnapshot.Load(IndexSnapshot.PathFor(serial), serial);

        if (before is null)
        {
            // Not an error: it is the first run. Saying so beats an empty table that reads
            // as "nothing changed".
            Console.WriteLine();
            Console.WriteLine("  " + L.T("diff.noBaseline"));
            Console.WriteLine();
            return 0;
        }

        // Deliberately a fresh read. Allowing the snapshot here would compare it against
        // itself and report that nothing ever changes.
        bool showProgress = !args.Contains("--no-progress") && !Console.IsOutputRedirected;

        var orchestrator = new ScanOrchestrator(new MftScanOptions
        {
            Progress = showProgress ? new ConsoleProgress() : null,
        });

        ScanResult now = orchestrator.Refresh(letter, StrategyPreference.Auto,
                                              allowSnapshot: false, cts.Token);

        if (showProgress) ConsoleProgress.Clear();

        var after = new LoadedSnapshot(now.Index, JournalMark.None, DateTime.UtcNow);

        SnapshotComparison diff = SnapshotDiff.Compare(
            before, after, ArgSize(args, "--min-delta", SnapshotDiff.MinimumDelta));

        Console.WriteLine();
        Console.WriteLine("  " + L.T("diff.window", Formatting.Duration(diff.Elapsed),
                                     Signed(diff.ByteDelta)));
        Console.WriteLine();

        foreach (FolderChange change in diff.Changes.Take(top))
        {
            string line = $"  {Signed(change.ByteDelta),14}  {change.Folder}";

            if (change.Grew) Console.WriteLine(line);
            else Formatting.WriteMuted(line);
        }

        Console.WriteLine();

        if (diff.Changes.Count == 0)
            Console.WriteLine("  " + L.T("diff.nothing", Formatting.Bytes(SnapshotDiff.MinimumDelta)));

        Formatting.WriteMuted("  " + L.T("diff.folders"));

        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// A byte count that keeps its sign, because on this screen the direction is the answer.
    /// </summary>
    private static string Signed(long bytes) => bytes switch
    {
        0 => "0",
        > 0 => "+" + Formatting.Bytes(bytes),
        _ => "-" + Formatting.Bytes(-bytes),
    };

    /// <summary>The scan every read-only report starts from.</summary>
    private static ScanResult ScanFor(string target, string[] args, CancellationToken token)
    {
        bool showProgress = !args.Contains("--no-progress") && !Console.IsOutputRedirected;

        var orchestrator = new ScanOrchestrator(new MftScanOptions
        {
            Progress = showProgress ? new ConsoleProgress() : null,
        });

        ScanResult scan = IsWholeVolume(target)
            ? orchestrator.Refresh(char.ToUpperInvariant(target[0]), StrategyPreference.Auto,
                                   allowSnapshot: !args.Contains("--fresh"), token)
            : orchestrator.ScanFolder(target, token);

        if (showProgress) ConsoleProgress.Clear();

        return scan;
    }

    public static int Version()
    {
        Console.WriteLine($"Vacuon {AppInfo.Version}");
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

        // --fresh skips the snapshot entirely. It exists because a cached index is a
        // convenience, and the moment anyone doubts the numbers they need a way to
        // insist on measuring the disk again.
        bool fresh = args.Contains("--fresh");

        ScanResult result = IsWholeVolume(target)
            ? orchestrator.Refresh(char.ToUpperInvariant(target[0]), preference, !fresh, cts.Token)
            : orchestrator.ScanFolder(target, cts.Token);

        sw.Stop();
        if (showProgress) ConsoleProgress.Clear();

        VolumeIndex index = result.Index;

        // ------------------------------------------------------------------
        Formatting.WriteHeading(L.T("cli.headScan", target));

        Console.WriteLine($"  {L.T("cli.labelSource"),-17} {L.T(result.CameFromSnapshot ? "cli.sourceSnapshot" : "cli.sourceScan")}");
        if (result.Incremental is not null)
            Formatting.WriteMuted($"                    {SnapshotDescription.Describe(result.Incremental)}");

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

        // Print the two totals against each other. They sat one line apart for a whole
        // release saying "758 GiB on disk" and "377 GiB used of 476 GiB", and nothing
        // objected, because nothing was comparing them.
        if (hasRealAllocation)
        {
            Reconciliation check = index.CheckAgainstFileSystem();
            string line = $"  {L.T("reconcile.label"),-17} {check.Describe()}";

            if (check.IsImpossible)
            {
                Formatting.WriteWarning(line);
                ExplainInflatedTotal(index);
            }
            else
            {
                Formatting.WriteMuted(line);
            }
        }

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

    /// <summary>
    /// Lists what is driving an impossible total, once the cross-check has said the total
    /// cannot be right.
    /// <para>
    /// Saying "do not trust this number" and stopping there leaves the person holding a
    /// broken tool and no way to report why. These ten lines are the evidence: the entries
    /// claiming the most space, with the logical size and the Alternate Data Stream bytes
    /// separated, because the two get inflated for entirely different reasons.
    /// </para>
    /// </summary>
    private static void ExplainInflatedTotal(VolumeIndex index)
    {
        var worst = new List<(int Index, long OnDisk)>();

        for (int i = 0; i < index.Entries.Length; i++)
        {
            ref FileEntry e = ref index.Entries[i];
            if (!e.IsInUse || e.IsDirectory) continue;
            if (e.HardLinkCount > 1) continue; // mesma regra do total, senão não explica o total

            long onDisk = index.GetSizeOnDisk(i);
            if (onDisk > 0) worst.Add((i, onDisk));
        }

        worst.Sort((a, b) => b.OnDisk.CompareTo(a.OnDisk));

        Console.WriteLine();
        Formatting.WriteWarning($"  {L.T("reconcile.explainTitle")}");

        foreach ((int i, long onDisk) in worst.Take(10))
        {
            long ads = index.GetAdsBytes(i);
            string adsNote = ads > 0 ? $"  ADS {Formatting.Bytes(ads)}" : string.Empty;

            Formatting.WriteMuted(
                $"    {Formatting.Bytes(onDisk),12}  " +
                $"({L.T("reconcile.explainLogical", Formatting.Bytes(index.Entries[i].LogicalSize))}){adsNote}  " +
                $"{Formatting.Truncate(index.GetFullPath(i), 74)}");
        }

        Console.WriteLine();
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

    /// <summary>Parses <c>--min-size=500MB</c> and friends. Plain digits mean bytes.</summary>
    private static long ArgSize(string[] args, string name, long fallback)
    {
        string raw = ArgString(args, name, string.Empty).Trim();
        if (raw.Length == 0) return fallback;

        long multiplier = 1;

        foreach ((string suffix, long factor) in new[]
                 {
                     ("KB", 1024L), ("KIB", 1024L),
                     ("MB", 1024L * 1024), ("MIB", 1024L * 1024),
                     ("GB", 1024L * 1024 * 1024), ("GIB", 1024L * 1024 * 1024),
                 })
        {
            if (!raw.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            multiplier = factor;
            raw = raw[..^suffix.Length].Trim();
            break;
        }

        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? (long)(value * multiplier)
            : fallback;
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
