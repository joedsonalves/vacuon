using Microsoft.Win32;

namespace Vacuon.Core.Optimization;

/// <summary>
/// The components Vacuon knows about.
/// <para>
/// Short on purpose. Every entry here points at a control Microsoft documents, and the machine
/// is asked for the current state rather than told what it should be — so an entry whose key
/// never existed reads as "not set", not as a false claim. Third-party AI software does not
/// belong in this list, however much of it is installed: the tab says "Microsoft components",
/// and it has to mean it.
/// </para>
/// </summary>
public static class AiComponentCatalog
{
    private const string CopilotPolicy = @"Software\Policies\Microsoft\Windows\WindowsCopilot";
    private const string WindowsAiPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI";
    private const string SearchPolicy = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search";
    private const string SearchPrefs = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";

    public static IReadOnlyList<AiComponent> All { get; } =
    [
        new()
        {
            Id = "copilot",
            NameKey = "ai.copilot.name",
            DescriptionKey = "ai.copilot.desc",
            Control = ControlKind.RegistryPolicy,
            Hive = RegistryHive.CurrentUser,
            SubKey = CopilotPolicy,
            ValueName = "TurnOffWindowsCopilot",
            OffValue = 1,
            OnValue = 0,
            ProcessNames = ["Copilot", "Microsoft.Copilot", "ai.exe"],
            DocumentationUrl = "https://learn.microsoft.com/windows/client-management/manage-windows-copilot",
        },
        new()
        {
            Id = "copilot-machine",
            NameKey = "ai.copilotMachine.name",
            DescriptionKey = "ai.copilotMachine.desc",
            Control = ControlKind.RegistryPolicy,
            Hive = RegistryHive.LocalMachine,
            SubKey = CopilotPolicy,
            ValueName = "TurnOffWindowsCopilot",
            OffValue = 1,
            OnValue = 0,
            DocumentationUrl = "https://learn.microsoft.com/windows/client-management/manage-windows-copilot",
        },
        new()
        {
            Id = "recall-snapshots",
            NameKey = "ai.recall.name",
            DescriptionKey = "ai.recall.desc",
            Control = ControlKind.RegistryPolicy,
            Hive = RegistryHive.LocalMachine,
            SubKey = WindowsAiPolicy,
            ValueName = "DisableAIDataAnalysis",
            OffValue = 1,
            OnValue = 0,
            ProcessNames = ["AIXHost", "SemanticSearchHost", "PhiSilicaHost"],
            DocumentationUrl = "https://learn.microsoft.com/windows/client-management/manage-recall",
        },
        new()
        {
            Id = "click-to-do",
            NameKey = "ai.clickToDo.name",
            DescriptionKey = "ai.clickToDo.desc",
            Control = ControlKind.RegistryPolicy,
            Hive = RegistryHive.LocalMachine,
            SubKey = WindowsAiPolicy,
            ValueName = "DisableClickToDo",
            OffValue = 1,
            OnValue = 0,
            DocumentationUrl = "https://learn.microsoft.com/windows/client-management/manage-click-to-do",
        },
        new()
        {
            Id = "bing-search",
            NameKey = "ai.bingSearch.name",
            DescriptionKey = "ai.bingSearch.desc",
            Control = ControlKind.RegistryPreference,
            Hive = RegistryHive.CurrentUser,
            SubKey = SearchPrefs,
            ValueName = "BingSearchEnabled",
            OffValue = 0,
            OnValue = 1,
            DocumentationUrl = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-search",
        },
        new()
        {
            Id = "search-suggestions",
            NameKey = "ai.searchSuggestions.name",
            DescriptionKey = "ai.searchSuggestions.desc",
            Control = ControlKind.RegistryPolicy,
            Hive = RegistryHive.LocalMachine,
            SubKey = SearchPolicy,
            ValueName = "DisableSearchBoxSuggestions",
            OffValue = 1,
            OnValue = 0,
            DocumentationUrl = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-search",
        },
        new()
        {
            Id = "cortana",
            NameKey = "ai.cortana.name",
            DescriptionKey = "ai.cortana.desc",
            Control = ControlKind.RegistryPolicy,
            Hive = RegistryHive.LocalMachine,
            SubKey = SearchPolicy,
            ValueName = "AllowCortana",
            OffValue = 0,
            OnValue = 1,
            DocumentationUrl = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-search",
        },
        new()
        {
            Id = "edge-copilot",
            NameKey = "ai.edgeCopilot.name",
            DescriptionKey = "ai.edgeCopilot.desc",
            Control = ControlKind.RegistryPolicy,
            Hive = RegistryHive.LocalMachine,
            SubKey = EdgePolicy,
            ValueName = "HubsSidebarEnabled",
            OffValue = 0,
            OnValue = 1,
            DocumentationUrl = "https://learn.microsoft.com/deployedge/microsoft-edge-policies#hubssidebarenabled",
        },

        // ---- reported only ----------------------------------------------------------------
        // No checkbox for these. Removing a shipped package or an optional feature is a
        // servicing operation, and Windows Update restores several of them; offering a button
        // that quietly loses would be worse than showing the truth.

        new()
        {
            Id = "core-ai-package",
            NameKey = "ai.coreAi.name",
            DescriptionKey = "ai.coreAi.desc",
            Control = ControlKind.Package,
            PackagePrefix = "MicrosoftWindows.Client.CoreAI",
            ReturnsAfterUpdate = true,
            DocumentationUrl = "https://learn.microsoft.com/windows/client-management/manage-windows-copilot",
        },
        new()
        {
            Id = "ai-fabric-package",
            NameKey = "ai.aiFabric.name",
            DescriptionKey = "ai.aiFabric.desc",
            Control = ControlKind.Package,
            PackagePrefix = "Microsoft.AIFabric",
            ProcessNames = ["AIFabricService"],
            ReturnsAfterUpdate = true,
            DocumentationUrl = "https://learn.microsoft.com/windows/ai/",
        },
    ];

    public static AiComponent? ById(string id)
    {
        foreach (AiComponent c in All)
            if (string.Equals(c.Id, id, StringComparison.Ordinal)) return c;
        return null;
    }
}
