using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vacuon.Native.Interop;

[Flags]
internal enum NotifyIconFlags : uint
{
    Message = 0x01,
    Icon = 0x02,
    Tip = 0x04,
    State = 0x08,
    Info = 0x10,
    ShowTip = 0x80,
}

internal enum NotifyIconMessage : uint
{
    Add = 0x00,
    Modify = 0x01,
    Delete = 0x02,
    SetVersion = 0x04,
}

/// <summary>How prominent a notification is, and which glyph it carries.</summary>
public enum NotificationLevel
{
    None = 0x00,
    Information = 0x01,
    Warning = 0x02,
    Error = 0x03,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NotifyIconData
{
    public uint cbSize;
    public nint hWnd;
    public uint uID;
    public NotifyIconFlags uFlags;
    public uint uCallbackMessage;
    public nint hIcon;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szTip;

    public uint dwState;
    public uint dwStateMask;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szInfo;

    public uint uVersion;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string szInfoTitle;

    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

/// <summary>
/// The notification-area icon, and the notifications that come out of it.
/// <para>
/// Driven through <c>Shell_NotifyIcon</c> rather than <c>System.Windows.Forms.NotifyIcon</c>.
/// Not a size decision — enabling WinForms alongside WPF never got as far as producing a
/// binary to weigh, because its implicit usings make <c>Application</c> and
/// <c>UserControl</c> ambiguous in every view in the project. Forty lines of P/Invoke, in the
/// assembly that already exists for exactly this, costs less than renaming types across a
/// codebase to accommodate a namespace nothing else needs.
/// </para>
/// <para>
/// A balloon posted here is a real Windows notification on Windows 10 and 11: the shell
/// routes it into the Action Center, where it survives being missed. That is the whole point
/// of the feature — a disk filling up while the window is behind the browser.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    /// <summary>The window message the shell sends back for clicks on the icon.</summary>
    public const uint CallbackMessage = 0x0400 + 1; // WM_APP + 1

    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_LBUTTONDBLCLK = 0x0203;

    private readonly nint _window;
    private readonly uint _id;
    private nint _icon;
    private bool _added;
    private bool _disposed;

    /// <param name="windowHandle">Any window that pumps messages; it receives the click callbacks.</param>
    /// <param name="iconHandle">An HICON the caller owns and outlives this object.</param>
    public TrayIcon(nint windowHandle, nint iconHandle, string tooltip, uint id = 1)
    {
        _window = windowHandle;
        _icon = iconHandle;
        _id = id;

        NotifyIconData data = Build(NotifyIconFlags.Message | NotifyIconFlags.Icon | NotifyIconFlags.Tip);
        data.szTip = Truncate(tooltip, 127);

        _added = Shell_NotifyIcon(NotifyIconMessage.Add, ref data);
    }

    public bool IsShown => _added;

    /// <summary>Changes the hover text. Silently does nothing if the icon never went up.</summary>
    public void SetTooltip(string tooltip)
    {
        if (!_added || _disposed) return;

        NotifyIconData data = Build(NotifyIconFlags.Tip);
        data.szTip = Truncate(tooltip, 127);

        Shell_NotifyIcon(NotifyIconMessage.Modify, ref data);
    }

    /// <summary>Replaces the icon itself. The caller keeps ownership of the handle.</summary>
    public void SetIcon(nint iconHandle)
    {
        _icon = iconHandle;

        if (!_added || _disposed) return;

        NotifyIconData data = Build(NotifyIconFlags.Icon);
        Shell_NotifyIcon(NotifyIconMessage.Modify, ref data);
    }

    /// <summary>
    /// Posts a notification. Returns whether the shell accepted it.
    /// <para>
    /// It can decline — focus assist, notifications turned off for the app, a full queue —
    /// and that is reported rather than assumed away, so a caller never records having told
    /// someone something the shell dropped.
    /// </para>
    /// </summary>
    public bool Notify(string title, string message, NotificationLevel level = NotificationLevel.Warning)
    {
        if (!_added || _disposed) return false;

        NotifyIconData data = Build(NotifyIconFlags.Info);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = (uint)level;

        return Shell_NotifyIcon(NotifyIconMessage.Modify, ref data);
    }

    private NotifyIconData Build(NotifyIconFlags flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window,
        uID = _id,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    /// <summary>
    /// The fixed-length buffers in NOTIFYICONDATA do not fail loudly on overflow — the
    /// marshaller throws, taking down whatever was reporting. Text arriving from a path or a
    /// volume label has no length anyone controls.
    /// </summary>
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_added) return;

        NotifyIconData data = Build(0);
        Shell_NotifyIcon(NotifyIconMessage.Delete, ref data);
        _added = false;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(NotifyIconMessage message, ref NotifyIconData data);
}
