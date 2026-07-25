namespace Vacuon.Core.Security;

/// <summary>
/// Nível de suspeita. Deliberadamente NÃO é um veredito de malware — o Vacuon não é
/// antivírus. É "isto merece um olhar", com o motivo explícito para o usuário julgar.
/// </summary>
public enum Suspicion
{
    /// <summary>Padrão esperado. Aparece só quando o usuário pede a listagem completa.</summary>
    Normal,
    /// <summary>Incomum, mas com explicação legítima frequente.</summary>
    Notable,
    /// <summary>Combinação que raramente é legítima. Vale investigar.</summary>
    Suspicious,
    /// <summary>Padrão fortemente associado a persistência maliciosa.</summary>
    HighlySuspicious,
}

public enum FindingKind
{
    RegistryAutorun,
    RegistryHijack,
    StartupFolder,
    ScheduledTask,
    SuspiciousFile,
    Service,
}

/// <summary>Um achado do módulo de segurança, sempre com o "por quê" junto.</summary>
public sealed record SecurityFinding
{
    public required FindingKind Kind { get; init; }
    public required Suspicion Level { get; init; }

    /// <summary>Onde foi encontrado (caminho do registro, do arquivo, nome da tarefa).</summary>
    public required string Location { get; init; }

    /// <summary>Nome do valor / do arquivo.</summary>
    public required string Name { get; init; }

    /// <summary>Conteúdo: linha de comando, caminho do alvo.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Explicação em uma frase de por que isto está na lista.</summary>
    public required string Reason { get; init; }

    /// <summary>O que se sabe do binário alvo, quando aplicável.</summary>
    public string? TargetPath { get; init; }
    public bool? TargetExists { get; init; }
    public string? Signer { get; init; }
    public long TargetSizeBytes { get; init; }
    public DateTime? TargetModifiedUtc { get; init; }

    public override string ToString() => $"[{Level}] {Location}\\{Name} = {Value}";
}

public sealed record SecurityReport
{
    public required IReadOnlyList<SecurityFinding> Findings { get; init; }
    public required int LocationsInspected { get; init; }
    public required int EntriesInspected { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required bool WasElevated { get; init; }

    public int CountAtLeast(Suspicion level)
    {
        int n = 0;
        foreach (SecurityFinding f in Findings)
            if (f.Level >= level) n++;
        return n;
    }
}
