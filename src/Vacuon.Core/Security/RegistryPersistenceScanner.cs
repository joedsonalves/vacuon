using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using Vacuon.Core.Localization;

namespace Vacuon.Core.Security;

public sealed class SecurityScanOptions
{
    /// <summary>Inclui entradas normais no relatório. Padrão: só o que chamou atenção.</summary>
    public bool IncludeNormal { get; init; }

    /// <summary>Consulta a assinatura Authenticode dos alvos. Custa I/O; vale a pena.</summary>
    public bool CheckSignatures { get; init; } = true;

    /// <summary>Inclui as pastas de Inicialização e as Tarefas Agendadas.</summary>
    public bool IncludeStartupFolders { get; init; } = true;
    public bool IncludeScheduledTasks { get; init; } = true;
}

/// <summary>
/// Inspeciona os pontos do registro onde malware costuma se alojar (PRD F9.x).
/// <para>
/// <b>Somente leitura.</b> Este scanner nunca altera nem apaga uma chave — ele lista,
/// explica e deixa a decisão com o usuário. Também não é um antivírus: não há
/// base de assinaturas, e sim heurística de comportamento.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryPersistenceScanner(SecurityScanOptions? options = null)
{
    private readonly SecurityScanOptions _options = options ?? new SecurityScanOptions();

    public SecurityReport Scan(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var findings = new List<SecurityFinding>();
        int locations = 0;
        int entries = 0;

        foreach (AutorunLocation location in AutorunLocations.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            locations++;

            try
            {
                entries += Inspect(location, findings);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // Chave protegida sem elevação: registrar em vez de sumir com o dado.
                findings.Add(new SecurityFinding
                {
                    Kind = FindingKind.RegistryAutorun,
                    Level = Suspicion.Notable,
                    Location = location.DisplayPath,
                    Name = L.T("autorun.accessDenied"),
                    Reason = L.T("autorun.accessDeniedReason"),
                });
            }
            catch (IOException)
            {
            }
        }

        if (_options.IncludeStartupFolders) entries += InspectStartupFolders(findings);
        if (_options.IncludeScheduledTasks) entries += InspectScheduledTasks(findings);

        findings.Sort(static (a, b) => b.Level.CompareTo(a.Level));

        return new SecurityReport
        {
            Findings = findings,
            LocationsInspected = locations,
            EntriesInspected = entries,
            Elapsed = sw.Elapsed,
            WasElevated = Vacuon.Core.Scan.VolumeProbe.IsElevated(),
        };
    }

    private int Inspect(AutorunLocation location, List<SecurityFinding> findings)
    {
        RegistryView view = location.IsWow64View ? RegistryView.Registry32 : RegistryView.Registry64;
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(location.Hive, view);
        using RegistryKey? key = baseKey.OpenSubKey(location.SubKey, writable: false);

        if (key is null) return 0;

        return location.Shape switch
        {
            AutorunShape.ValuesAreCommands => InspectValues(location, key, findings),
            AutorunShape.NamedValue => InspectNamedValue(location, key, findings),
            AutorunShape.SubkeysWithValue => InspectSubkeys(location, key, findings),
            _ => 0,
        };
    }

    private int InspectValues(AutorunLocation location, RegistryKey key, List<SecurityFinding> findings)
    {
        int count = 0;

        foreach (string name in key.GetValueNames())
        {
            count++;
            string value = ReadAsString(key, name);
            if (string.IsNullOrWhiteSpace(value)) continue;

            Consider(location, name, value, findings);
        }

        return count;
    }

    private int InspectNamedValue(AutorunLocation location, RegistryKey key, List<SecurityFinding> findings)
    {
        string name = location.ValueName ?? string.Empty;
        object? raw = key.GetValue(name, null);

        if (raw is null)
        {
            // Ausência é o padrão em várias destas chaves — nada a relatar.
            return 0;
        }

        string value = Stringify(raw);

        // Comparação com o valor esperado é o sinal mais forte que existe aqui:
        // Winlogon\Shell diferente de explorer.exe não tem explicação inocente.
        if (location.ExpectedValue is not null)
        {
            bool matches = string.Equals(
                value.Trim().TrimEnd(','),
                location.ExpectedValue.Trim().TrimEnd(','),
                StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                if (_options.IncludeNormal) AddNormal(location, name, value, findings);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(location.ExpectedValue))
                return 1;

            string targetPath = CommandHeuristics.ExtractTargetPath(value);
            (Suspicion cmdLevel, List<string> reasons) = CommandHeuristics.Evaluate(value, targetPath);

            Suspicion level = (Suspicion)Math.Max(
                (int)Suspicion.Suspicious,
                Math.Max((int)cmdLevel, (int)location.BaseLevel));

            reasons.Insert(0, L.T("autorun.expectedValue", location.ExpectedValue));

            findings.Add(Build(location, FindingKind.RegistryHijack, level, name, value, targetPath, reasons));
            return 1;
        }

        Consider(location, name, value, findings);
        return 1;
    }

    private int InspectSubkeys(AutorunLocation location, RegistryKey key, List<SecurityFinding> findings)
    {
        int count = 0;

        foreach (string subName in key.GetSubKeyNames())
        {
            using RegistryKey? sub = key.OpenSubKey(subName, writable: false);
            if (sub is null) continue;

            count++;
            object? raw = sub.GetValue(location.ValueName ?? string.Empty, null);
            if (raw is null) continue;

            string value = Stringify(raw);
            if (string.IsNullOrWhiteSpace(value)) continue;

            var scoped = location with { SubKey = $@"{location.SubKey}\{subName}" };
            Consider(scoped, location.ValueName ?? L.T("autorun.defaultValue"), value, findings, subName);
        }

        return count;
    }

    private void Consider(AutorunLocation location, string name, string value,
                          List<SecurityFinding> findings, string? context = null)
    {
        string targetPath = CommandHeuristics.ExtractTargetPath(value);
        string resolved = CommandHeuristics.Normalize(targetPath);

        (Suspicion level, List<string> reasons) = CommandHeuristics.Evaluate(value, resolved);

        if (location.BaseLevel > level) level = location.BaseLevel;

        bool? exists = null;
        long size = 0;
        DateTime? modified = null;
        string? signer = null;

        // Só faz sentido investigar o alvo quando o valor realmente é um caminho.
        // "msv1_0", "scecli" e "{CLSID}" são nomes, não arquivos.
        if (!location.ValueIsFolder && CommandHeuristics.LooksLikePath(resolved))
        {
            bool found = false;
            try
            {
                var fi = new FileInfo(resolved);
                found = fi.Exists;
                if (found)
                {
                    size = fi.Length;
                    modified = fi.LastWriteTimeUtc;
                    if (_options.CheckSignatures) signer = TryGetSigner(resolved);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }

            exists = found;

            if (!found)
            {
                // Autorun apontando para o nada: ou é resto de desinstalação (lixo a limpar),
                // ou é malware que já foi removido pela metade.
                reasons.Add(L.T("autorun.orphan"));
                if (level < Suspicion.Notable) level = Suspicion.Notable;
            }
            else if (_options.CheckSignatures && signer is null && IsExecutable(resolved)
                     && !CommandHeuristics.IsUnderSystemDirectory(resolved.ToLowerInvariant()))
            {
                // Fora do diretório do sistema, a ausência de assinatura embutida vale
                // como sinal. Dentro dele não vale: o Windows assina por catálogo.
                reasons.Add(L.T("autorun.unsigned"));
                if (level < Suspicion.Notable) level = Suspicion.Notable;
            }
        }

        if (context is not null) reasons.Add(L.T("autorun.entry", context));

        if (level == Suspicion.Normal && !_options.IncludeNormal) return;

        // Nível base só sobrevive se algo concreto foi encontrado, ou se a simples
        // existência da entrada já é o sinal (AppInit_DLLs, IFEO Debugger, RunOnceEx).
        if (reasons.Count == 0 && !location.PresenceIsTheSignal && !_options.IncludeNormal) return;

        findings.Add(Build(location, FindingKind.RegistryAutorun, level, name, value,
                           CommandHeuristics.LooksLikePath(resolved) ? resolved : null,
                           reasons, exists, size, modified, signer));
    }

    private static SecurityFinding Build(AutorunLocation location, FindingKind kind, Suspicion level,
                                         string name, string value, string? target, List<string> reasons,
                                         bool? exists = null, long size = 0,
                                         DateTime? modified = null, string? signer = null) =>
        new()
        {
            Kind = kind,
            Level = level,
            Location = location.DisplayPath,
            Name = string.IsNullOrEmpty(name) ? L.T("autorun.defaultValue") : name,
            Value = value,
            Reason = reasons.Count > 0
                ? string.Join(" · ", reasons)
                : location.Description,
            TargetPath = string.IsNullOrEmpty(target) ? null : target,
            TargetExists = exists,
            TargetSizeBytes = size,
            TargetModifiedUtc = modified,
            Signer = signer,
        };

    private static void AddNormal(AutorunLocation location, string name, string value, List<SecurityFinding> findings) =>
        findings.Add(new SecurityFinding
        {
            Kind = FindingKind.RegistryAutorun,
            Level = Suspicion.Normal,
            Location = location.DisplayPath,
            Name = string.IsNullOrEmpty(name) ? L.T("autorun.defaultValue") : name,
            Value = value,
            Reason = L.T("autorun.asExpected"),
        });

    private int InspectStartupFolders(List<SecurityFinding> findings)
    {
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        ];

        int count = 0;

        foreach (string folder in folders)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;

            foreach (string file in Directory.EnumerateFiles(folder))
            {
                count++;
                string name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                var reasons = new List<string>();
                Suspicion level = Suspicion.Normal;

                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".vbs" or ".vbe" or ".js" or ".jse" or ".bat" or ".cmd" or ".ps1" or ".hta" or ".wsf")
                {
                    level = Suspicion.Suspicious;
                    reasons.Add(L.T("startup.script", ext));
                }
                else if (ext == ".lnk")
                {
                    reasons.Add(L.T("startup.shortcut"));
                }
                else if (ext is ".exe" or ".scr" or ".com" or ".pif")
                {
                    level = Suspicion.Notable;
                    reasons.Add(L.T("startup.bareExecutable"));
                }

                if (level == Suspicion.Normal && !_options.IncludeNormal) continue;

                findings.Add(new SecurityFinding
                {
                    Kind = FindingKind.StartupFolder,
                    Level = level,
                    Location = folder,
                    Name = name,
                    Value = file,
                    Reason = reasons.Count > 0 ? string.Join(" · ", reasons) : L.T("startup.item"),
                    TargetPath = file,
                    TargetExists = true,
                });
            }
        }

        return count;
    }

    private int InspectScheduledTasks(List<SecurityFinding> findings)
    {
        string tasksRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");

        if (!Directory.Exists(tasksRoot)) return 0;

        int count = 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(tasksRoot, "*", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException)
        {
            findings.Add(new SecurityFinding
            {
                Kind = FindingKind.ScheduledTask,
                Level = Suspicion.Notable,
                Location = tasksRoot,
                Name = L.T("autorun.accessDenied"),
                Reason = L.T("autorun.accessDeniedReason"),
            });
            return 0;
        }

        foreach (string file in files)
        {
            count++;

            string xml;
            try { xml = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            // As tarefas da própria Microsoft estão em subpastas \Microsoft\ e são milhares;
            // só interessam as que trazem um sinal forte.
            (Suspicion level, List<string> reasons) = CommandHeuristics.Evaluate(xml, null);
            if (level < Suspicion.Suspicious) continue;

            string relative = Path.GetRelativePath(tasksRoot, file);

            findings.Add(new SecurityFinding
            {
                Kind = FindingKind.ScheduledTask,
                Level = level,
                Location = @L.T("security.scheduledTasks"),
                Name = relative,
                Value = file,
                Reason = string.Join(" · ", reasons),
                TargetPath = file,
                TargetExists = true,
            });
        }

        return count;
    }

    private static bool IsExecutable(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".exe" or ".dll" or ".sys" or ".scr" or ".ocx" or ".com";
    }

    /// <summary>
    /// Assinante Authenticode do binário, ou <c>null</c> se não houver assinatura embutida.
    /// <para>
    /// Ausência de assinatura NÃO é prova de nada — muito software honesto não assina.
    /// Vale como um sinal a mais, nunca como veredito.
    /// </para>
    /// </summary>
    private static string? TryGetSigner(string path)
    {
        try
        {
            // CreateFromSignedFile lê a assinatura Authenticode embutida no PE.
            // Está marcada como obsoleta, mas continua sendo a única via gerenciada
            // que faz exatamente isto sem descer para WinVerifyTrust.
#pragma warning disable SYSLIB0057, SYSLIB0026
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057, SYSLIB0026
            string subject = cert.Subject;

            int cn = subject.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
            if (cn < 0) return subject;

            int start = cn + 3;
            int end = subject.IndexOf(',', start);
            return end < 0 ? subject[start..] : subject[start..end];
        }
        catch
        {
            // Sem assinatura embutida (ou assinatura em catálogo, que este caminho não vê).
            return null;
        }
    }

    private static string ReadAsString(RegistryKey key, string name)
    {
        try { return Stringify(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)); }
        catch { return string.Empty; }
    }

    private static string Stringify(object? raw) => raw switch
    {
        null => string.Empty,
        string s => s,
        string[] arr => string.Join(" ; ", arr),
        byte[] bytes => L.T("autorun.binary", bytes.Length),
        _ => raw.ToString() ?? string.Empty,
    };
}
