using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vacuon.Native.Interop;

[ComImport]
[Guid("000214e4-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig]
    int QueryContextMenu(nint hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);

    [PreserveSig]
    int InvokeCommand(ref CmInvokeCommandInfo info);

    [PreserveSig]
    int GetCommandString(nuint idCmd, uint type, nint reserved, nint commandString, uint max);
}

[ComImport]
[Guid("000214f4-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(nint hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
    [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfo info);
    [PreserveSig] int GetCommandString(nuint idCmd, uint type, nint reserved, nint commandString, uint max);

    /// <summary>Owner-drawn menu entries are measured and painted through here.</summary>
    [PreserveSig] int HandleMenuMsg(uint msg, nint wParam, nint lParam);
}

[ComImport]
[Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3
{
    [PreserveSig] int QueryContextMenu(nint hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
    [PreserveSig] int InvokeCommand(ref CmInvokeCommandInfo info);
    [PreserveSig] int GetCommandString(nuint idCmd, uint type, nint reserved, nint commandString, uint max);
    [PreserveSig] int HandleMenuMsg(uint msg, nint wParam, nint lParam);
    [PreserveSig] int HandleMenuMsg2(uint msg, nint wParam, nint lParam, out nint result);
}

[ComImport]
[Guid("000214e6-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig]
    int ParseDisplayName(nint hwnd, nint bc, [MarshalAs(UnmanagedType.LPWStr)] string displayName,
                                       out uint eaten, out nint pidl, ref uint attributes);
    [PreserveSig] int EnumObjects(nint hwnd, int flags, out nint enumIdList);
    [PreserveSig] int BindToObject(nint pidl, nint bc, [In] ref Guid riid, out nint obj);
    [PreserveSig] int BindToStorage(nint pidl, nint bc, [In] ref Guid riid, out nint obj);
    [PreserveSig] int CompareIDs(nint lParam, nint pidl1, nint pidl2);
    [PreserveSig] int CreateViewObject(nint hwndOwner, [In] ref Guid riid, out nint view);
    [PreserveSig] int GetAttributesOf(uint count, nint pidls, ref uint attributes);

    /// <summary>
    /// <paramref name="pidls"/> is a raw pointer to an array of child PIDLs, not a managed
    /// array.
    /// <para>
    /// Declaring it <c>nint[]</c> let the marshaller build the array itself, and the call
    /// faulted with an access violation every time — a crash inside a COM call, which in a
    /// window procedure is a fail-fast that no handler in the process ever sees. The caller
    /// pins one element and passes its address, which is exactly what the shell expects.
    /// </para>
    /// </summary>
    [PreserveSig]
    int GetUIObjectOf(nint hwndOwner, uint count, nint pidls, [In] ref Guid riid,
                      nint reserved, out nint obj);

    [PreserveSig] int GetDisplayNameOf(nint pidl, uint flags, nint name);
    [PreserveSig]
    int SetNameOf(nint hwnd, nint pidl, [MarshalAs(UnmanagedType.LPWStr)] string name,
                                uint flags, out nint outPidl);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct CmInvokeCommandInfo
{
    public int cbSize;
    public uint fMask;
    public nint hwnd;
    public nint lpVerb;
    [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
    [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
    public int nShow;
    public uint dwHotKey;
    public nint hIcon;
}

/// <summary>
/// The Explorer right-click menu, for a file this app is showing.
/// <para>
/// The point is that it is the <b>real</b> one. Open With, Properties, whatever the machine's
/// shell extensions add — a list built by hand would be a smaller, staler imitation of a menu
/// the person already knows, and it would silently lack the entry they were reaching for.
/// </para>
/// <para>
/// <b>What Windows does from here is Windows' business.</b> That menu can delete, and
/// <c>ProtectedPaths</c> does not reach into it — it governs what <i>Vacuon</i> does, and this
/// is the shell acting on its own behalf at somebody's direct instruction, exactly as it would
/// in an Explorer window. The distinction matters and is not softened: nothing here bypasses
/// the app's own guard, because nothing here is the app acting.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellContextMenu
{
    private const uint CmfNormal = 0x00000000;
    private const uint CmfExplore = 0x00000004;

    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmLeftAlign = 0x0000;

    private const uint FirstCommandId = 1;
    private const uint LastCommandId = 0x7FFF;

    private static Guid _shellFolder = new("000214e6-0000-0000-c000-000000000046");
    private static Guid _contextMenu = new("000214e4-0000-0000-c000-000000000046");

    /// <summary>
    /// How many entries the shell would put in a menu for this file, without showing one.
    /// <para>
    /// Two jobs. It lets the interface hide an entry that would open an empty menu — an item
    /// that does nothing when clicked is the kind of quiet lie this project treats as a bug.
    /// And it makes the whole chain — parse, bind, get the object, build the menu — checkable
    /// without a window and without a popup that blocks waiting to be dismissed.
    /// </para>
    /// </summary>
    /// <returns>The number of entries, or -1 when the shell would not offer a menu at all.</returns>
    public static int CountItems(string path)
    {
        if (path.Length == 0) return -1;

        nint pidl = 0, folderPtr = 0, menuPtr = 0, menu = 0;

        try
        {
            if (SHParseDisplayName(path, 0, out pidl, 0, out _) != 0 || pidl == 0) return -1;
            if (SHBindToParent(pidl, ref _shellFolder, out folderPtr, out nint child) != 0 || folderPtr == 0) return -1;

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);

            nint[] one = [child];

            unsafe
            {
                fixed (nint* first = one)
                {
                    if (folder.GetUIObjectOf(0, 1, (nint)first, ref _contextMenu, 0, out menuPtr) != 0
                        || menuPtr == 0)
                        return -1;
                }
            }

            var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);

            menu = CreatePopupMenu();
            if (menu == 0) return -1;

            if (contextMenu.QueryContextMenu(menu, 0, FirstCommandId, LastCommandId,
                                             CmfNormal | CmfExplore) < 0)
                return -1;

            return GetMenuItemCount(menu);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return -1;
        }
        finally
        {
            if (menu != 0) DestroyMenu(menu);
            if (menuPtr != 0) Marshal.Release(menuPtr);
            if (folderPtr != 0) Marshal.Release(folderPtr);
            if (pidl != 0) CoTaskMemFree(pidl);
        }
    }

    /// <summary>
    /// Shows the shell menu for one file at a screen position, and runs whatever was picked.
    /// </summary>
    /// <returns>Whether a menu was shown at all.</returns>
    public static bool Show(nint owner, string path, int screenX, int screenY)
    {
        if (path.Length == 0) return false;

        nint pidl = 0;
        nint folderPtr = 0;
        nint menuPtr = 0;
        nint menu = 0;

        try
        {
            uint attributes = 0;

            if (SHParseDisplayName(path, 0, out pidl, 0, out attributes) != 0 || pidl == 0)
                return false;

            if (SHBindToParent(pidl, ref _shellFolder, out folderPtr, out nint child) != 0 || folderPtr == 0)
                return false;

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);

            nint[] one = [child];

            unsafe
            {
                fixed (nint* first = one)
                {
                    if (folder.GetUIObjectOf(owner, 1, (nint)first, ref _contextMenu, 0, out menuPtr) != 0
                        || menuPtr == 0)
                        return false;
                }
            }

            var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);

            // Kept for the duration of the menu so the owner window's hook can forward the
            // measure and draw messages. Shell extensions that add owner-drawn entries — the
            // ones with icons and headers — require this and misbehave without it.
            _active = contextMenu as IContextMenu3;
            _activeLegacy = contextMenu as IContextMenu2;

            menu = CreatePopupMenu();
            if (menu == 0) return false;

            if (contextMenu.QueryContextMenu(menu, 0, FirstCommandId, LastCommandId,
                                             CmfNormal | CmfExplore) < 0)
                return false;

            // TPM_RETURNCMD makes this return the chosen id instead of posting a message, so
            // the command is invoked here rather than from a WndProc that would have to know
            // about shell menus.
            uint chosen = TrackPopupMenuEx(menu, TpmReturnCmd | TpmLeftAlign,
                                           screenX, screenY, owner, 0);

            if (chosen == 0) return true;   // dismissed; the menu was still shown

            // No CMIC_MASK_UNICODE here, and that is not an oversight. That flag tells the
            // shell the structure is the larger CMINVOKECOMMANDINFOEX; passing it alongside
            // the small ANSI one makes the shell read past the end of the struct. It did
            // exactly that, and the process died without reaching a single exception handler —
            // a fail-fast inside the window procedure, the same shape of failure this project
            // already paid for once with 0xC000041D.
            var invoke = new CmInvokeCommandInfo
            {
                cbSize = Marshal.SizeOf<CmInvokeCommandInfo>(),
                fMask = 0,
                hwnd = owner,
                lpVerb = (nint)(chosen - FirstCommandId),
                nShow = 1,   // SW_SHOWNORMAL
            };

            contextMenu.InvokeCommand(ref invoke);

            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            // A shell extension that misbehaves must not take the window down with it.
            return false;
        }
        finally
        {
            _active = null;
            _activeLegacy = null;

            if (menu != 0) DestroyMenu(menu);
            if (menuPtr != 0) Marshal.Release(menuPtr);
            if (folderPtr != 0) Marshal.Release(folderPtr);
            if (pidl != 0) CoTaskMemFree(pidl);
        }
    }

    private static IContextMenu3? _active;
    private static IContextMenu2? _activeLegacy;

    /// <summary>
    /// Forwards a menu message to the shell extension that owns the entry, while a menu of
    /// ours is up. The owner window's message hook calls this.
    /// </summary>
    /// <returns>Whether the message was consumed.</returns>
    public static bool HandleMenuMessage(int msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;

        // WM_INITMENUPOPUP, WM_DRAWITEM, WM_MEASUREITEM, WM_MENUCHAR.
        if (msg is not (0x0117 or 0x002B or 0x002C or 0x0120)) return false;

        try
        {
            if (_active is not null)
                return _active.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == 0;

            if (_activeLegacy is not null)
                return _activeLegacy.HandleMenuMsg((uint)msg, wParam, lParam) == 0;
        }
        catch (COMException)
        {
            // An extension that throws while drawing its own entry loses the entry, not
            // the window.
        }

        return false;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl,
                                                 uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(nint pidl, ref Guid riid, out nint parent, out nint child);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(nint menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(nint pointer);
}
