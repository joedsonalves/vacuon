using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Vacuon.App.Infra;

/// <summary>
/// Lets a file dragged from Explorer reach this window while it is running elevated.
/// <para>
/// ⚠️ <b>Without this, drag and drop simply does not happen when elevated</b>, and nothing
/// anywhere says why. Windows blocks messages travelling from a lower integrity level to a
/// higher one (UIPI), and the drop handler is never called — no error, no log, not even the
/// "no entry" cursor in every case. It looks exactly like a handler that was never wired up,
/// which is why this cost an afternoon of reading correct code.
/// </para>
/// <para>
/// Measured on this machine on 02/09/2026: Explorer sits at <c>S-1-16-8192</c> (medium) and
/// an elevated Vacuon at <c>S-1-16-12288</c> (high). Reading the MFT needs administrator, so
/// the app being on the far side of that line is the normal case, not the odd one.
/// </para>
/// <para>
/// ⚠️ <b>This opens a hole, and it is worth naming.</b> Allowing these three messages lets a
/// medium-integrity process post data into an elevated one — that is the price of accepting
/// drops at all while elevated, and every elevated app that takes a drop pays it. It is not
/// applied when the app is not elevated, because then there is no line to cross and no
/// reason to widen anything.
/// </para>
/// </summary>
public static class ElevatedDrop
{
    private const int WmCopyData = 0x004A;

    /// <summary>Undocumented, and the one that actually carries the dragged bytes.</summary>
    private const int WmCopyGlobalData = 0x0049;

    private const int WmDropFiles = 0x0233;

    private const int MsgfltAllow = 1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(nint hwnd, int message, int action, nint info);

    /// <summary>
    /// Allows the three messages an OLE drop needs, on this window only.
    /// </summary>
    /// <remarks>
    /// Per window rather than per process: the process-wide call was deprecated in Windows 7
    /// exactly because it widens more than the caller meant to.
    /// </remarks>
    public static void Allow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!ElevationService.IsElevated) return;

        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;

        ChangeWindowMessageFilterEx(handle, WmDropFiles, MsgfltAllow, 0);
        ChangeWindowMessageFilterEx(handle, WmCopyGlobalData, MsgfltAllow, 0);
        ChangeWindowMessageFilterEx(handle, WmCopyData, MsgfltAllow, 0);
    }
}
