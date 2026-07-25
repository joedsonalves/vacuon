using System.Runtime.InteropServices;

namespace Vacuon.Native.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct BITMAP
{
    public int bmType;
    public int bmWidth;
    public int bmHeight;
    public int bmWidthBytes;
    public ushort bmPlanes;
    public ushort bmBitsPixel;
    public nint bmBits;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

public static partial class Gdi32
{
    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    // O export real é GetObjectW; LibraryImport não acrescenta o sufixo sozinho.
    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObject(nint hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(
        nint hdc, nint hbmp, uint uStartScan, uint cScanLines,
        [Out] byte[]? lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint hObject);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);
}
