using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vacuon.Core.Analyzers;
using Vacuon.Native.Interop;

namespace Vacuon.Core.Preview;

/// <summary>
/// Tamanhos de miniatura oferecidos ao usuário (PRD F6.5/F6.12).
/// <para>
/// Existe para o usuário conseguir <b>ver o que vai apagar</b> sem abrir o arquivo:
/// nos tamanhos grandes, um frame do vídeo ou a própria foto aparecem na lista.
/// </para>
/// </summary>
public enum ThumbnailSize
{
    /// <summary>16 px — lista compacta, só ícone do tipo.</summary>
    Tiny = 16,
    /// <summary>32 px — lista padrão.</summary>
    Small = 32,
    /// <summary>64 px — já dá para reconhecer uma foto.</summary>
    Medium = 64,
    /// <summary>128 px — grade de mídia.</summary>
    Large = 128,
    /// <summary>256 px — grade grande, reconhece frame de vídeo.</summary>
    ExtraLarge = 256,
    /// <summary>512 px — pré-visualização antes de apagar.</summary>
    Huge = 512,
}

/// <summary>Bitmap cru em BGRA32, top-down. Sem dependência de UI (ADR-2).</summary>
public sealed record ThumbnailBitmap(int Width, int Height, byte[] Bgra32, bool IsContentThumbnail)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Miniaturas e ícones via Shell do Windows, com cache LRU.
/// <para>
/// Imagem, vídeo, PDF e projetos ganham miniatura do próprio conteúdo; o resto recebe
/// o ícone registrado do tipo. Em ambos os casos quem decodifica é o Windows, o que
/// significa reaproveitar o cache de miniaturas que o Explorer já construiu.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThumbnailProvider(int cacheCapacity = 2048) : IDisposable
{
    private readonly Dictionary<(string Path, ThumbnailSize Size), LinkedListNode<CacheItem>> _map = new();
    private readonly LinkedList<CacheItem> _lru = new();
    private readonly Lock _gate = new();

    private sealed record CacheItem((string Path, ThumbnailSize Size) Key, ThumbnailBitmap Bitmap);

    /// <summary>
    /// Miniatura do arquivo. Devolve <c>null</c> quando o Shell não consegue produzir
    /// nada — o chamador então mostra o ícone genérico.
    /// </summary>
    /// <param name="preferContent">
    /// <c>true</c> pede o conteúdo (frame do vídeo, a foto); <c>false</c> força o ícone
    /// do tipo, que é instantâneo e não toca no arquivo.
    /// </param>
    public ThumbnailBitmap? Get(string path, ThumbnailSize size, bool preferContent = true)
    {
        var key = (path, size);

        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<CacheItem>? node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        ThumbnailBitmap? bitmap = Produce(path, size, preferContent);
        if (bitmap is null) return null;

        lock (_gate)
        {
            if (!_map.ContainsKey(key))
            {
                LinkedListNode<CacheItem> node = _lru.AddFirst(new CacheItem(key, bitmap));
                _map[key] = node;

                while (_map.Count > cacheCapacity && _lru.Last is not null)
                {
                    LinkedListNode<CacheItem> last = _lru.Last;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }

        return bitmap;
    }

    /// <summary>Decide se este arquivo merece miniatura de conteúdo ou ícone do tipo.</summary>
    public static bool WantsContentThumbnail(string fileName) =>
        FileCategories.HasContentThumbnail(FileCategories.Of(fileName.AsSpan()));

    private static ThumbnailBitmap? Produce(string path, ThumbnailSize size, bool preferContent)
    {
        // ResizeToFit sozinho, sem BiggerSizeOk: com BiggerSizeOk o Shell devolve o que
        // tiver em cache (pedir 64 devolvia 96), e a lista precisa do tamanho que o
        // usuário escolheu, não do que o Windows achou conveniente.
        const SIIGBF fit = SIIGBF.ResizeToFit;

        nint hBitmap = 0;
        try
        {
            int hr = Shell32.SHCreateItemFromParsingName(
                path, 0, Shell32.IID_IShellItemImageFactory, out IShellItemImageFactory factory);

            if (hr != Shell32.S_OK || factory is null) return null;

            try
            {
                var nativeSize = new NativeSize { Width = (int)size, Height = (int)size };

                // ThumbnailOnly primeiro: assim o "veio do conteúdo" é um fato verificado,
                // não um palpite. Sem esta separação, um .md sem handler de preview
                // devolveria o ícone genérico rotulado como se fosse o conteúdo.
                if (preferContent && WantsContentThumbnail(path))
                {
                    hr = factory.GetImage(nativeSize, fit | SIIGBF.ThumbnailOnly, out hBitmap);
                    if (hr == Shell32.S_OK && hBitmap != 0) return Convert(hBitmap, isContent: true);

                    if (hBitmap != 0) { Gdi32.DeleteObject(hBitmap); hBitmap = 0; }
                }

                hr = factory.GetImage(nativeSize, fit | SIIGBF.IconOnly, out hBitmap);
                if (hr != Shell32.S_OK || hBitmap == 0) return null;

                return Convert(hBitmap, isContent: false);
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (hBitmap != 0) Gdi32.DeleteObject(hBitmap);
        }
    }

    /// <summary>Converte o HBITMAP do Shell em pixels BGRA32 top-down.</summary>
    private static ThumbnailBitmap? Convert(nint hBitmap, bool isContent)
    {
        var bmp = default(BITMAP);
        if (Gdi32.GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), ref bmp) == 0) return null;
        if (bmp.bmWidth <= 0 || bmp.bmHeight <= 0) return null;

        int width = bmp.bmWidth;
        int height = bmp.bmHeight;

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            // Altura negativa = varredura top-down, que é como a UI espera receber.
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Gdi32.BI_RGB,
        };

        byte[] pixels = new byte[width * height * 4];

        nint hdc = Gdi32.GetDC(0);
        try
        {
            int scanned = Gdi32.GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref header, Gdi32.DIB_RGB_COLORS);
            if (scanned == 0) return null;
        }
        finally
        {
            Gdi32.ReleaseDC(0, hdc);
        }

        return new ThumbnailBitmap(width, height, pixels, isContent);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
        }
    }
}
