using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vacuon.Core.Localization;
using Vacuon.Core.Monitoring;
using Vacuon.Core.Index;
using Vacuon.Core.Scan;
using Vacuon.Native.Interop;

namespace Vacuon.App.Infra;

/// <summary>
/// The notification-area icon: free space at a glance, a warning when a volume crosses the
/// threshold, and a way back to the window.
/// <para>
/// It is also what keeps the trend on the Dashboard fed. A projection is only as good as the
/// readings behind it, and readings only exist while something is taking them — so the same
/// timer that refreshes the tooltip appends to the history. That is why the projection
/// improves the longer the app has been used, and why a fresh install has none.
/// </para>
/// </summary>
public sealed class TrayService : IDisposable
{
    /// <summary>
    /// How often free space is read.
    /// <para>
    /// Not the history's spacing — <see cref="SpaceHistory"/> keeps its own floor and drops
    /// what arrives too soon. This interval is what the tooltip and the low-space warning
    /// run on, and a minute is well inside the time it takes a disk to fill.
    /// </para>
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly AppSettings _settings;
    private readonly SpaceHistory _history = new();
    private readonly SpaceAlerter _alerter = new();
    private readonly DispatcherTimer _timer;
    private readonly Window _window;

    private TrayIcon? _icon;
    private nint _iconHandle;
    private HwndSource? _source;
    private bool _disposed;

    /// <summary>Raised when someone asks for a quick cleanup from the icon's menu.</summary>
    public event Action? QuickCleanupRequested;

    public TrayService(Window window, AppSettings settings)
    {
        _window = window;
        _settings = settings;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>
    /// Puts the icon up. Must run after the window has a handle.
    /// </summary>
    public void Attach()
    {
        nint handle = new WindowInteropHelper(_window).Handle;
        if (handle == 0) return;

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnMessage);

        if (_settings.ShowTrayIcon) Show();

        // Records a first reading immediately, so history starts accumulating from the first
        // run rather than a minute into it.
        Poll();
        _timer.Start();
    }

    public void Show()
    {
        if (_icon is not null || _disposed) return;

        _iconHandle = LoadOwnIcon();
        _icon = new TrayIcon(new WindowInteropHelper(_window).Handle, _iconHandle,
                             L.T("app.name"));
    }

    public void Hide()
    {
        _icon?.Dispose();
        _icon = null;

        if (_iconHandle != 0)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }
    }

    /// <summary>Reflects a settings change without needing a restart.</summary>
    public void ApplySettings()
    {
        if (_settings.ShowTrayIcon) Show();
        else Hide();
    }

    /// <summary>
    /// Reads every fixed volume, updates the tooltip, records history and warns on a crossing.
    /// </summary>
    private void Poll()
    {
        if (_disposed) return;

        // The history's own spacing floor decides what is kept; this call is cheap when it
        // decides nothing is.
        _history.Record();

        var tooltip = new System.Text.StringBuilder();
        tooltip.Append(L.T("app.name"));

        foreach (VolumeInfo volume in VolumeProbe.EnumerateFixedVolumes())
        {
            long free = FreeSpaceOf(volume.DriveLetter);
            if (free <= 0) continue;

            tooltip.Append('\n').Append(L.T("tray.volumeLine", volume.DriveLetter + ":",
                                            Format.Bytes(free)));

            if (!_settings.NotifyOnLowSpace) continue;

            var reading = new SpaceReading(DateTimeOffset.Now, volume.DriveLetter, free, volume.TotalBytes);
            SpaceAlert? alert = _alerter.Consider(reading, _settings.LowSpaceThresholdBytes);

            if (alert is not null) Warn(alert);
        }

        _icon?.SetTooltip(tooltip.ToString());
    }

    private void Warn(SpaceAlert alert)
    {
        // Posted through the icon, so it needs one. Warning with the icon hidden would mean
        // putting it up unasked, which is worse than the warning is useful.
        _icon?.Notify(
            L.T("tray.lowSpaceTitle", alert.DriveLetter + ":"),
            L.T("tray.lowSpaceBody", Format.Bytes(alert.FreeBytes),
                Format.Bytes(alert.ThresholdBytes)),
            NotificationLevel.Warning);
    }

    private nint OnMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != (int)TrayIcon.CallbackMessage) return 0;

        switch ((int)lParam)
        {
            case TrayIcon.WM_LBUTTONUP:
            case TrayIcon.WM_LBUTTONDBLCLK:
                RestoreWindow();
                handled = true;
                break;

            case TrayIcon.WM_RBUTTONUP:
                ShowMenu();
                handled = true;
                break;
        }

        return 0;
    }

    public void RestoreWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;

        _window.Activate();
    }

    private void ShowMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var open = new System.Windows.Controls.MenuItem { Header = L.T("tray.open") };
        open.Click += (_, _) => RestoreWindow();

        var clean = new System.Windows.Controls.MenuItem { Header = L.T("tray.quickCleanup") };
        clean.Click += (_, _) =>
        {
            RestoreWindow();
            QuickCleanupRequested?.Invoke();
        };

        var exit = new System.Windows.Controls.MenuItem { Header = L.T("tray.exit") };
        exit.Click += (_, _) => Application.Current.Shutdown();

        menu.Items.Add(open);
        menu.Items.Add(clean);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exit);

        // Without foreground, the menu stays open after a click elsewhere — the shell's
        // long-standing requirement for menus raised from a notification icon.
        SetForegroundWindow(new WindowInteropHelper(_window).Handle);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>
    /// The application's own icon, taken from the running executable.
    /// <para>
    /// Reading it out of the exe avoids both a second copy of the artwork and a reference to
    /// System.Drawing for one handle. Falls back to the system application icon, because an
    /// icon that failed to load must not become an invisible tray entry.
    /// </para>
    /// </summary>
    private static nint LoadOwnIcon()
    {
        string? exe = Environment.ProcessPath;

        if (exe is not null)
        {
            var large = new nint[1];
            var small = new nint[1];

            if (ExtractIconEx(exe, 0, large, small, 1) > 0)
            {
                if (small[0] != 0)
                {
                    if (large[0] != 0) DestroyIcon(large[0]);
                    return small[0];
                }

                if (large[0] != 0) return large[0];
            }
        }

        return LoadIcon(0, 32512); // IDI_APPLICATION
    }

    private static long FreeSpaceOf(char driveLetter)
    {
        try { return new DriveInfo(driveLetter + ":\\").AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _source?.RemoveHook(OnMessage);
        Hide();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, nint[] large, nint[] small, int count);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint instance, int name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
