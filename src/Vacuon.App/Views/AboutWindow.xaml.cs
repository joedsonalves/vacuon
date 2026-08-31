using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Vacuon.App.Infra;
using Vacuon.Core;
using Vacuon.Core.Localization;
using Vacuon.Core.Update;

namespace Vacuon.App.Views;

/// <summary>The addresses this app points at, in one place.</summary>
public static class AppLinks
{
    public const string Repository = "https://github.com/joedsonalves/vacuon";
    public const string Releases = Repository + "/releases";
    public const string Site = "https://joedsonalves.github.io/vacuon/";
    public const string Issues = Repository + "/issues";
}

/// <summary>
/// What is running, whether anything newer exists, and how to check that for yourself.
/// <para>
/// The version had one home before this — a line in Settings — and no way to answer the two
/// questions people actually have: am I on the latest, and is this file the one that was
/// published. Both are answered here, and both with measurements rather than claims: the
/// comparison comes from the package manager already on the machine, and the hash is
/// computed from the bytes of the executable that is running right now.
/// </para>
/// </summary>
public partial class AboutWindow : Window
{
    private readonly AppSettings _settings;
    private CancellationTokenSource? _checking;

    private AboutWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        SourceInitialized += (_, _) =>
            TitleBarTheme.Apply(this, ThemeManager.Effective == ThemeChoice.Dark);

        Title = L.T("about.title");
        TitleText.Text = "Vacuon " + AppInfo.Version;
        TaglineText.Text = L.T("about.tagline");

        BuildLabel.Text = L.T("about.buildLabel");
        BuildText.Text = string.Join("  ·  ",
            L.T("about.version") + " " + AppInfo.Version,
            ".NET " + Environment.Version,
            Environment.OSVersion.VersionString,
            Environment.Is64BitProcess ? "x64" : "x86");

        UpdateLabel.Text = L.T("about.updateLabel");
        UpdateText.Text = L.T("about.checking");
        UpdateNote.Text = L.T("about.sourceNote");
        CommandText.Text = UpdateCheck.UpgradeCommand;
        CheckButton.Content = L.T("about.check");
        UpdateButton.Content = L.T("about.update");

        AutoCheck.Content = L.T("about.autoCheck");
        AutoCheck.IsChecked = settings.CheckUpdatesOnStart;

        FileLabel.Text = L.T("about.fileLabel");
        FileText.Text = Environment.ProcessPath ?? L.T("about.unknownPath");
        HashText.Text = string.Empty;
        HashButton.Content = L.T("about.hash");

        LinksText.Text = L.T("about.links");
        ReleasesButton.Content = L.T("about.releases");
        CloseButton.Content = L.T("about.close");

        Loaded += async (_, _) => await CheckAsync();
    }

    public static void Show(Window owner, AppSettings settings)
    {
        var window = new AboutWindow(settings) { Owner = owner };
        window.ShowDialog();
    }

    private async Task CheckAsync()
    {
        _checking?.Cancel();
        _checking = new CancellationTokenSource();

        CheckButton.IsEnabled = false;
        UpdateButton.Visibility = Visibility.Collapsed;
        UpdateText.Text = L.T("about.checking");

        UpdateStatus status = await UpdateCheck.QueryAsync(_checking.Token);

        UpdateText.Text = Describe(status);
        CheckButton.IsEnabled = true;

        // The button only appears when there is something for it to do. A greyed-out
        // "Update" on an app that is up to date is a question mark nobody needed.
        UpdateButton.Visibility = status.Outcome == UpdateOutcome.Available
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>The three versions in one sentence, including when they disagree.</summary>
    private static string Describe(UpdateStatus status) => status.Outcome switch
    {
        UpdateOutcome.NoWinget => L.T("about.noWinget"),
        UpdateOutcome.NotInstalledByWinget => L.T("about.notFromWinget", status.Running),

        UpdateOutcome.Available =>
            L.T("about.available", status.Available ?? "?", status.Installed ?? status.Running),

        // ⚠️ Not a flat "you are up to date". The source can be behind the build that is
        // running — it is, every time a release goes out before its manifest is merged — and
        // telling somebody they are current when they are ahead is a claim with nothing
        // behind it.
        _ when status.RunningIsAhead =>
            L.T("about.aheadOfSource", status.Running, status.Installed ?? "?"),

        _ => L.T("about.upToDate", status.Running),
    };

    private async void OnCheck(object sender, RoutedEventArgs e) => await CheckAsync();

    /// <summary>
    /// Hands the update to winget, in a window the person can see.
    /// <para>
    /// ⚠️ Not silent, and not automatic. Two measured reasons, either of which is enough on
    /// its own: this app is usually started elevated for the MFT read, and winget refuses to
    /// touch a user-scope package from an elevated session; and a copy downloaded as a loose
    /// executable was never installed by a package manager, so there is nothing to upgrade.
    /// An updater that failed silently in both of the common cases would be worse than none.
    /// </para>
    /// </summary>
    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k " + UpdateCheck.UpgradeCommand,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateText.Text = ex.Message;
        }
    }

    /// <summary>
    /// SHA256 of the running executable, computed here and now.
    /// <para>
    /// Every release publishes the hash of what it uploaded. This is the other half of that
    /// sentence — the bytes on this disk — so the two can be compared by the person holding
    /// both, which is the only way an unsigned binary can be checked at all.
    /// </para>
    /// </summary>
    private void OnHash(object sender, RoutedEventArgs e)
    {
        string? path = Environment.ProcessPath;
        if (path is null) return;

        HashButton.IsEnabled = false;
        HashText.Text = L.T("about.hashing");

        try
        {
            using FileStream file = File.OpenRead(path);
            HashText.Text = Convert.ToHexString(SHA256.HashData(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HashText.Text = ex.Message;
        }
        finally
        {
            HashButton.IsEnabled = true;
        }
    }

    private void OnAutoCheckChanged(object sender, RoutedEventArgs e)
    {
        _settings.CheckUpdatesOnStart = AutoCheck.IsChecked == true;
        _settings.Save();
    }

    private void OnReleases(object sender, RoutedEventArgs e) => Open(AppLinks.Releases);

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _checking?.Cancel();
        _checking?.Dispose();
        base.OnClosed(e);
    }
}
