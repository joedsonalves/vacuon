using System.Buffers.Binary;

namespace Vacuon.Core.Preview;

/// <summary>
/// Grava um <see cref="ThumbnailBitmap"/> como BMP de 32 bits.
/// <para>
/// Existe para a CLI conseguir provar que a miniatura saiu certa sem arrastar um
/// codificador de imagem para dentro do núcleo. A GUI não usa isto: ela consome os
/// pixels BGRA direto, sem passar por arquivo.
/// </para>
/// </summary>
public static class BmpWriter
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;

    public static void Write(ThumbnailBitmap bitmap, string path)
    {
        using FileStream fs = File.Create(path);
        Write(bitmap, fs);
    }

    public static void Write(ThumbnailBitmap bitmap, Stream stream)
    {
        int pixelBytes = bitmap.Width * bitmap.Height * 4;
        int fileSize = FileHeaderSize + InfoHeaderSize + pixelBytes;

        Span<byte> header = stackalloc byte[FileHeaderSize + InfoHeaderSize];
        header.Clear();

        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header[2..], fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[10..], FileHeaderSize + InfoHeaderSize);

        Span<byte> info = header[FileHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(info, InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(info[4..], bitmap.Width);
        // Negativo = top-down, mesma orientação em que o GetDIBits entregou.
        BinaryPrimitives.WriteInt32LittleEndian(info[8..], -bitmap.Height);
        BinaryPrimitives.WriteInt16LittleEndian(info[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(info[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(info[16..], 0); // BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(info[20..], pixelBytes);

        stream.Write(header);
        stream.Write(bitmap.Bgra32, 0, pixelBytes);
    }
}
