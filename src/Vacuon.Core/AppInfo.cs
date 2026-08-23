namespace Vacuon.Core;

/// <summary>
/// The version, in one place.
/// <para>
/// It used to live in three: the window footer, the CLI help banner and <c>vacuon version</c>.
/// Three copies of a number that must agree is three chances for them not to, and the one
/// place a user checks which build they are running is the worst place to be wrong.
/// </para>
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// Semantic version of this build.
    /// <para>
    /// Bump it here and nowhere else. The winget manifests under <c>packaging/winget/</c>
    /// carry their own copy on purpose — each one describes a release that already shipped
    /// and must keep describing it, whatever the working tree says today.
    /// </para>
    /// </summary>
    public const string Version = "0.5.0";
}
