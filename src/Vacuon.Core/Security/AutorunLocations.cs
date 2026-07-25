using Microsoft.Win32;

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
    public required string Description { get; init; }
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
                Description = "Programas iniciados junto com o Windows",
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
                Description = "Executa uma vez no próximo logon e se apaga",
                BaseLevel = Suspicion.Notable,
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnceEx",
                Description = "Variante do RunOnce, raramente usada por software legítimo",
                BaseLevel = Suspicion.Suspicious,
                PresenceIsTheSignal = true,
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\RunServices",
                Description = "Autorun herdado do Windows 9x; software moderno não usa",
                BaseLevel = Suspicion.Suspicious,
                PresenceIsTheSignal = true,
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\RunServicesOnce",
                Description = "Autorun herdado do Windows 9x, execução única",
                BaseLevel = Suspicion.Suspicious,
                PresenceIsTheSignal = true,
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run",
                Description = "Autorun por política de grupo — fora de domínio, é bandeira vermelha",
                BaseLevel = Suspicion.Suspicious,
                PresenceIsTheSignal = true,
            });
            list.Add(new AutorunLocation
            {
                Hive = hive,
                SubKey = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                Description = "Autorun de aplicativos 32 bits",
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
            Description = "Shell do Windows. Qualquer coisa diferente de explorer.exe é sequestro",
            BaseLevel = Suspicion.Notable,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = winlogon,
            ValueName = "Userinit",
            ExpectedValue = @"C:\Windows\system32\userinit.exe,",
            Shape = AutorunShape.NamedValue,
            Description = "Processo de inicialização do usuário. Comandos extras aqui rodam a cada logon",
            BaseLevel = Suspicion.Notable,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = winlogon,
            ValueName = "Taskman",
            Shape = AutorunShape.NamedValue,
            Description = "Gerenciador de tarefas alternativo. Normalmente não existe",
            BaseLevel = Suspicion.Suspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = winlogon + @"\Notify",
            Shape = AutorunShape.SubkeysWithValue,
            ValueName = "DllName",
            Description = "DLLs notificadas em eventos de logon",
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
            Description = "DLLs carregadas em TODO processo que usa user32.dll. Deve estar vazio",
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
            Description = "AppInit_DLLs na visão 32 bits. Deve estar vazio",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"System\CurrentControlSet\Control\Session Manager",
            ValueName = "AppCertDlls",
            Shape = AutorunShape.NamedValue,
            Description = "DLLs carregadas em toda criação de processo. Normalmente não existe",
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
            Description = "Executado pelo gerenciador de sessão antes do logon",
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
            Description = "Depurador anexado a um executável: rodar o programa X roda o programa Y",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Windows NT\CurrentVersion\SilentProcessExit",
            Shape = AutorunShape.SubkeysWithValue,
            ValueName = "MonitorProcess",
            Description = "Processo disparado quando outro encerra — técnica de persistência",
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
                Description = $"{v}: DLLs carregadas pelo LSASS. Alvo clássico de roubo de credencial",
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
                Description = $"Comando de abertura de {cls}. Alterado, roda malware junto com qualquer arquivo do tipo",
                BaseLevel = Suspicion.Notable,
            });
        }

        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Command Processor",
            ValueName = "AutoRun",
            Shape = AutorunShape.NamedValue,
            Description = "Comando executado em toda abertura do cmd.exe",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.CurrentUser,
            SubKey = @"Software\Microsoft\Command Processor",
            ValueName = "AutoRun",
            Shape = AutorunShape.NamedValue,
            Description = "Comando executado em toda abertura do cmd.exe (usuário atual)",
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
            Description = "Browser Helper Objects (Internet Explorer). Vetor histórico de adware",
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad",
            Description = "Objetos COM carregados pelo Explorer na inicialização",
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\SharedTaskScheduler",
            Description = "Tarefas compartilhadas do Explorer",
            BaseLevel = Suspicion.Suspicious,
            PresenceIsTheSignal = true,
        });
        list.Add(new AutorunLocation
        {
            Hive = RegistryHive.LocalMachine,
            SubKey = @"Software\Microsoft\Active Setup\Installed Components",
            Shape = AutorunShape.SubkeysWithValue,
            ValueName = "StubPath",
            Description = "Active Setup: roda uma vez por usuário no primeiro logon",
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
                Description = "Pasta de inicialização. Redirecionar isto esconde os autoruns do usuário",
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
            Description = "Script executado no logon do usuário. Não é criado por software comum",
            BaseLevel = Suspicion.HighlySuspicious,
            PresenceIsTheSignal = true,
        });

        return list;
    }
}
