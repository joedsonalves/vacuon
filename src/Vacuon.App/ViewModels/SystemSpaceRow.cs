using Vacuon.App.Infra;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Localization;

namespace Vacuon.App.ViewModels;

/// <summary>
/// One block of space that belongs to Windows, as the dashboard shows it.
/// <para>
/// Each line says what it is, how big it is, and — the part that matters — <b>how it is
/// dealt with</b>. None of these is deleted the way a file is: the page file is resized in
/// System properties, hibernation goes away with <c>powercfg /h off</c>, and shadow copies
/// are the Volume Shadow Copy service's to remove. A panel that showed the sizes without
/// saying that would be an invitation to go and delete them by hand, which does not work
/// and breaks things when it does.
/// </para>
/// </summary>
public sealed class SystemSpaceRow(SystemSpaceItem item)
{
    public string Label => L.T(item.Kind switch
    {
        SystemSpaceKind.PageFile => "system.pagefile",
        SystemSpaceKind.SwapFile => "system.swapfile",
        SystemSpaceKind.Hibernation => "system.hibernation",
        _ => "system.shadow",
    });

    public string SizeText => item.IsKnown ? Format.Bytes(item.Bytes) : "—";

    /// <summary>How this one is actually dealt with, or why there is no number.</summary>
    public string HowText
    {
        get
        {
            if (!item.IsKnown) return L.T("system.shadowUnknown");

            if (item.Kind != SystemSpaceKind.ShadowCopies)
            {
                return L.T(item.Kind switch
                {
                    SystemSpaceKind.PageFile => "system.pagefileHow",
                    SystemSpaceKind.SwapFile => "system.swapfileHow",
                    _ => "system.hibernationHow",
                });
            }

            // Allocated and ceiling come through as one field, because they only mean
            // anything beside each other: "5.5 GB used, 5.9 GB taken, 9.5 GB allowed".
            string[] parts = (item.Detail ?? string.Empty).Split('/');

            return parts.Length == 2
                   && long.TryParse(parts[0], out long allocated)
                   && long.TryParse(parts[1], out long max)
                ? L.T("system.shadowHow", Format.Bytes(allocated), Format.Bytes(max))
                : L.T("system.shadow");
        }
    }
}
