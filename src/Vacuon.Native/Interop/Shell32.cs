using System.Runtime.InteropServices;

namespace Vacuon.Native.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct NativeSize
{
    public int Width;
    public int Height;
}

/// <summary>
/// <c>IShellItemImageFactory</c> — a via oficial para a miniatura de qualquer arquivo.
/// <para>
/// É o que o Explorer usa: entrega o frame de um vídeo, a primeira página de um PDF,
/// a composição de um PSD e a capa de um MP3, tudo aproveitando o cache de miniaturas
/// do próprio Windows. Reimplementar decodificação de vídeo para isso seria absurdo.
/// </para>
/// </summary>
[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(NativeSize size, SIIGBF flags, out nint phbm);
}

[Flags]
public enum SIIGBF
{
    ResizeToFit = 0x00000000,
    BiggerSizeOk = 0x00000001,
    MemoryOnly = 0x00000002,
    IconOnly = 0x00000004,
    ThumbnailOnly = 0x00000008,
    InCacheOnly = 0x00000010,
    ScaleUp = 0x00000100,
}

[Flags]
public enum SHGFI : uint
{
    Icon = 0x000000100,
    DisplayName = 0x000000200,
    TypeName = 0x000000400,
    SysIconIndex = 0x000004000,
    LargeIcon = 0x000000000,
    SmallIcon = 0x000000001,
    UseFileAttributes = 0x000000010,
    OpenIcon = 0x000000002,
    ShellIconSize = 0x000000004,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SHFILEINFO
{
    public nint hIcon;
    public int iIcon;
    public uint dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
}

/// <summary>Operation codes for <c>SHFileOperation</c>.</summary>
public enum FileOperation : uint
{
    Move = 0x0001,
    Copy = 0x0002,
    Delete = 0x0003,
    Rename = 0x0004,
}

[Flags]
public enum FileOperationFlags : ushort
{
    /// <summary>Send to the Recycle Bin instead of deleting outright.</summary>
    AllowUndo = 0x0040,
    /// <summary>Do not ask the user to confirm.</summary>
    NoConfirmation = 0x0010,
    /// <summary>Do not show the progress dialog.</summary>
    Silent = 0x0004,
    /// <summary>Do not show an error dialog; report through the return code instead.</summary>
    NoErrorUi = 0x0400,
    /// <summary>Do not ask about creating directories.</summary>
    NoConfirmMkDir = 0x0200,
    /// <summary>Suppress the "which files?" summary.</summary>
    NoConfirmation2 = 0x0010,
}

/// <summary>
/// <c>SHFILEOPSTRUCT</c> — the only API that moves files to the Recycle Bin.
/// <para>
/// <see cref="pFrom"/> is a double-null-terminated list, not a plain string. Passing a
/// single path without the extra terminator silently truncates the batch.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SHFILEOPSTRUCT
{
    public nint hwnd;
    public FileOperation wFunc;
    [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
    [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
    public FileOperationFlags fFlags;
    [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
    public nint hNameMappings;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
}

public static partial class Shell32
{
    public const int S_OK = 0;

    /// <summary>
    /// Moves items to the Recycle Bin (with <see cref="FileOperationFlags.AllowUndo"/>)
    /// or deletes them.
    /// </summary>
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    // DllImport e não LibraryImport: o gerador de origem não sabe marshalar
    // interfaces COM (SYSLIB1052). Aqui o marshalling em runtime é obrigatório.
    [DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHCreateItemFromParsingName(
        string pszPath,
        nint pbc,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    public static extern nint SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        SHGFI uFlags);

    /// <summary>Abre o Explorer com o arquivo já selecionado (PRD F6.8).</summary>
    public static void RevealInExplorer(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            // As aspas são obrigatórias: sem elas, caminho com espaço abre a pasta errada.
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = false,
        })?.Dispose();
    }

    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
}

public static partial class User32
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);
}
