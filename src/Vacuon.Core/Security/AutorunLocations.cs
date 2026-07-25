using Microsoft.Win32;
using Vacuon.Core.Localization;

namespace Vacuon.Core.Security;

/// <summary>Como o conteúdo de uma chave deve ser interpretado.</summary>
public enum AutorunShape
{
    /// <summary>Cada valor da chave é um comando (ex.: ...\CurrentVersion\Run).</summary>
    ValuesAreCommands,
    /// <summary>Um valor nomeado específico importa (ex.: Winlogon\Shell).</summary>
    NamedValue,
    /// <summary>Cada subchave é uma entrada; o comando está num valor dela.</summary>
    SubkeysWithValue,
}

/// <summary>
/// Catálogo das chaves do registro que malware usa para persistir.
/// <para>
/// A lista segue os pontos de autorun consagrados (Sysinternals Autoruns, MITRE ATT&amp;CK
/// T1547/T1546). Cada local diz o que é <b>esperado</b>, para que o scanner consiga
/// apontar o que fugiu do padrão em vez de despejar tudo na tela.
/// </para>
/// </summary>
public sealed record AutorunLocation
{
    public required RegistryHive Hive { get; init; }
    public required string SubKey { get; init; }

    /// <summary>Chave de tradução da descrição, não o texto em si.</summary>
    public required string DescriptionKey { get; init; }

    /// <summary>Argumentos de formatação da descrição, quando ela tem {0}.</summary>
    public object?[] DescriptionArgs { get; init; } = [];

    /// <summary>Descrição no idioma ativo.</summary>
    public string Description => DescriptionArgs.Length == 0
        ? L.T(DescriptionKey)
        : L.T(DescriptionKey, DescriptionArgs);
    public AutorunShape Shape { get; init; } = AutorunShape.ValuesAreCommands;

    /// <summary>Para <see cref="AutorunShape.NamedValue"/> / <see cref="AutorunShape.SubkeysWithValue"/>.</summary>
    public string? ValueName { get; init; }

    /// <summary>Valor legítimo esperado. Divergir disso é o próprio sinal.</summary>
    public string? ExpectedValue { get; init; }

    /// <summary>Suspeita mínima atribuída a qualquer entrada encontrada aqui.</summary>
    public Suspicion BaseLevel { get; init; } = Suspicion.Normal;

    /// <summary>
    /// A simples existência de uma entrada aqui já é o sinal, mesmo sem nenhuma
    /// heurística disparar (AppInit_DLLs, IFEO Debugger, RunOnceEx...).
    /// Onde isto é <c>false</c>, entradas sem sinal nenhum não entram no relatório.
    /// </summary>
    public bool PresenceIsTheSignal { get; init; }

    /// <summary>O valor é um caminho de PASTA, não de executável (ex.: Startup).</summary>
    public bool ValueIsFolder { get; init; }

    /// <summary>Registro de 32 bits em SO de 64 bits (WOW6432Node).</summary>
    public bool IsWow64View { get; init; }

    public string DisplayPath => $"{HiveShort}\\{SubKey}";

    private string HiveShort => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => Hive.ToString(),
    };
}

public static class AutorunLocations
{
    /// <summary>
    /// Os locais inspecionados. Ordem = ordem de exibição no relatório.
    /// </summary>
    public static IReadOnlyList<AutorunLocation> All { get; } = Build();

    private static List<AutorunLocation> Build()
    {
        var list = new List<AutorunLocation>();

        // ---------------------------------------------------------------
        // 1. Run / RunOnce — o ponto de partida clássico
        // ---------------------------------------------------------------
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\Run",
                DescriptionKey = "autorun.run",
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
                DescriptionKey = "autorun.runOnce",
                BaseLevel = Suspicion.Notable,
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnceEx",
                DescriptionKey = "autorun.runOnceEx",
                BaseLevel = Suspicion.Suspicious,
                PresenceIsTheSignal = true,
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\RunServices",
                DescriptionKey = "autorun.runServices",
                BaseLevel = Suspicion.Suspicious,
                PresenceIsTheSignal = true,
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\RunServicesOnce",
                DescriptionKey = "autorun.runServicesOnce",
                BaseLevel = Suspicion.Suspicious,
                PresenceIsTheSignal = true,
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run",
                DescriptionKey = "autorun.policiesRun",
                BaseLevel = Suspicion.Suspicious,
                PresenceIsTheSignal = true,
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                DescriptionKey = "autorun.run32",
                IsWow64View = true,
            });
        }

        // ---------------------------------------------------------------
        // 2. Winlogon — sequestro do processo de logon
        // ---------------------------------------------------------------
        const string winlogon = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";

        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = winlogon,
            ValueName = "Shell",
            ExpectedValue = "explorer.exe",
            Shape = AutorunShape.NamedValue,
            DescriptionKey = "autorun.winlogonShell",
            BaseLevel = Suspicion.Notable,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = winlogon,
            ValueName = "Userinit",
            ExpectedValue = @"C:\Windows\system32\userinit.exe,",
            Shape = AutorunShape.NamedValue,
            DescriptionKey = "autorun.winlogonUserinit",
            BaseLevel = Suspicion.Notable,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = winlogon,
            ValueName = "Taskman",
            Shape = AutorunShape.NamedValue,
            DescriptionKey = "autorun.winlogonTaskman",
            BaseLevel = Suspicion.Suspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = winlogon + @"\Notify",
            Shape = AutorunShape.SubkeysWithValue,
            ValueName = "DllName",
            DescriptionKey = "autorun.winlogonNotify",
            BaseLevel = Suspicion.Suspicious,
            PresenceIsTheSignal = true,
        });

        // ---------------------------------------------------------------
        // 3. Injeção de DLL em todo processo
        // ---------------------------------------------------------------
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Windows NT\CurrentVersion\Windows",
            ValueName = "AppInit_DLLs",
            ExpectedValue = "",
            Shape = AutorunShape.NamedValue,
            DescriptionKey = "autorun.appInitDlls",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows",
            ValueName = "AppInit_DLLs",
            ExpectedValue = "",
            Shape = AutorunShape.NamedValue,
            IsWow64View = true,
            DescriptionKey = "autorun.appInitDlls32",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"System\CurrentControlSet\Control\Session Manager",
            ValueName = "AppCertDlls",
            Shape = AutorunShape.NamedValue,
            DescriptionKey = "autorun.appCertDlls",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"System\CurrentControlSet\Control\Session Manager",
            ValueName = "BootExecute",
            ExpectedValue = "autocheck autochk *",
            Shape = AutorunShape.NamedValue,
            DescriptionKey = "autorun.bootExecute",
        });

        // ---------------------------------------------------------------
        // 4. Image File Execution Options — sequestro por "depurador"
        // ---------------------------------------------------------------
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
            Shape = AutorunShape.SubkeysWithValue,
            ValueName = "Debugger",
            DescriptionKey = "autorun.ifeoDebugger",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Windows NT\CurrentVersion\SilentProcessExit",
            Shape = AutorunShape.SubkeysWithValue,
            ValueName = "MonitorProcess",
            DescriptionKey = "autorun.silentProcessExit",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });

        // ---------------------------------------------------------------
        // 5. LSA — pacotes carregados dentro do processo de segurança
        // ---------------------------------------------------------------
        foreach (string v in new[] { "Security Packages", "Authentication Packages", "Notification Packages" })
        {
            list.Add(new AutorunLocation
            {
                Hive = RegistryHive.LocalMachine,
                SubKey = @"System\CurrentControlSet\Control\Lsa",
                ValueName = v,
                Shape = AutorunShape.NamedValue,
                DescriptionKey = "autorun.lsaPackage",
                DescriptionArgs = [v],
            });
        }

        // ---------------------------------------------------------------
        // 6. Sequestro de associação de arquivo e do shell
        // ---------------------------------------------------------------
        // txtfile e htmlfile ficam sem valor esperado: o comando deles depende do
        // editor e do navegador padrão do usuário, então comparar contra uma constante
        // acusaria toda máquina que não usa Bloco de Notas e Internet Explorer.
        foreach ((string cls, string? expected) in new (string, string?)[]
                 {
                     ("exefile", "\"%1\" %*"),
                     ("comfile", "\"%1\" %*"),
                     ("batfile", "\"%1\" %*"),
                     ("cmdfile", "\"%1\" %*"),
                     ("piffile", "\"%1\" %*"),
                     ("scrfile", "\"%1\" /S"),
                     ("txtfile", null),
                     ("htmlfile", null),
                 })
        {
            list.Add(new AutorunLocation
            {
                Hive = RegistryHive.ClassesRoot,
                SubKey = $@"{cls}\shell\open\command",
                ValueName = "",
                ExpectedValue = expected,
                Shape = AutorunShape.NamedValue,
                DescriptionKey = "autorun.fileAssociation",
                DescriptionArgs = [cls],
                BaseLevel = Suspicion.Notable,
            });
        }

        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Command Processor",
            ValueName = "AutoRun",
            Shape = AutorunShape.NamedValue,
            DescriptionKey = "autorun.commandProcessor",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.CurrentUser,
            SubKey = @"Software\Microsoft\Command Processor",
            ValueName = "AutoRun",
            Shape = AutorunShape.NamedValue,
            DescriptionKey = "autorun.commandProcessorUser",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });

        // ---------------------------------------------------------------
        // 7. Componentes carregados pelo Explorer
        // ---------------------------------------------------------------
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects",
            Shape = AutorunShape.SubkeysWithValue,
            ValueName = "",
            DescriptionKey = "autorun.bho",
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad",
            DescriptionKey = "autorun.shellServiceObject",
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\SharedTaskScheduler",
            DescriptionKey = "autorun.sharedTaskScheduler",
            BaseLevel = Suspicion.Suspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Active Setup\Installed Components",
            Shape = AutorunShape.SubkeysWithValue,
            ValueName = "StubPath",
            DescriptionKey = "autorun.activeSetup",
        });

        // ---------------------------------------------------------------
        // 8. Pastas de inicialização redirecionadas
        // ---------------------------------------------------------------
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                ValueName = "Startup",
                Shape = AutorunShape.NamedValue,
                ValueIsFolder = true,
                DescriptionKey = "autorun.startupFolder",
            });
        }

        // ---------------------------------------------------------------
        // 9. Scripts de logon
        // ---------------------------------------------------------------
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.CurrentUser,
            SubKey = @"Environment",
            ValueName = "UserInitMprLogonScript",
            Shape = AutorunShape.NamedValue,
            DescriptionKey = "autorun.logonScript",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });

        return list;
    }
}
