using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace Vacuon.Native.Interop;

/// <summary>
/// IShellLinkW, declared in full: the vtable is positional, and a method left out would shift
/// every one after it onto somebody else's slot.
/// </summary>
[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    [PreserveSig]
    int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, nint findData, uint flags);

    [PreserveSig] int GetIDList(out nint idList);
    [PreserveSig] int SetIDList(nint idList);
    [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int capacity);
    [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int capacity);
    [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int capacity);
    [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    [PreserveSig] int GetHotkey(out short hotkey);
    [PreserveSig] int SetHotkey(short hotkey);
    [PreserveSig] int GetShowCmd(out int showCmd);
    [PreserveSig] int SetShowCmd(int showCmd);
    [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
    [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
    [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
    [PreserveSig] int Resolve(nint window, uint flags);
    [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

/// <summary>
/// Shell shortcuts (<c>.lnk</c>): what one points at, read the way the shell reads it.
/// <para>
/// The format has a target id list, a link-info block, relative paths and environment
/// variables, and an installer can make one whose target is not a path at all. Reading it
/// through the shell's own <c>IShellLink</c> takes every form for what the shell takes it
/// for, which hand-parsing the bytes would only approximate.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellLink
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    /// <summary>STGM_READ.</summary>
    private const int ReadMode = 0;

    /// <summary>
    /// The file a shortcut points at, or null when it does not point at one — or when it
    /// could not be read, which is the same answer for a caller deciding whether something
    /// is missing: it does not know, and so says nothing.
    /// </summary>
    /// <remarks>
    /// The stored path, not a repaired one: <c>Resolve</c> is never called, because it goes
    /// looking for a moved target and can rewrite the shortcut, and this only ever reads.
    /// </remarks>
    public static string? TargetOf(string shortcut)
    {
        object? link = null;

        try
        {
            link = Create();
            if (link is null) return null;

            ((IPersistFile)link).Load(shortcut, ReadMode);

            var buffer = new StringBuilder(32_768);

            // S_FALSE, with nothing written, is how a shortcut to something that is not a
            // file answers: a control-panel item, or an installer's advertised shortcut.
            if (((IShellLinkW)link).GetPath(buffer, buffer.Capacity, 0, 0) != 0) return null;

            string target = buffer.ToString();
            return target.Length == 0 ? null : target;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException
                                        or FileNotFoundException or ArgumentException or IOException)
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// Writes a shortcut to <paramref name="target"/>. The app itself never makes one — this
    /// exists so the tests can put a real shortcut where the reading side expects it.
    /// </summary>
    public static bool Write(string shortcut, string target)
    {
        object? link = null;

        try
        {
            link = Create();
            if (link is null) return false;

            if (((IShellLinkW)link).SetPath(target) != 0) return false;

            ((IPersistFile)link).Save(shortcut, true);
            return File.Exists(shortcut);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException
                                        or IOException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        }
    }

    private static object? Create()
    {
        Type? type = Type.GetTypeFromCLSID(ShellLinkClass);
        return type is null ? null : Activator.CreateInstance(type);
    }
}
