using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vacuon.Core.Preview;
using Vacuon.Core.Localization;

namespace Vacuon.App.Services;

/// <summary>
/// Ponte entre o <see cref="ThumbnailProvider"/> do núcleo (que devolve pixels crus)
/// e o WPF (que quer <see cref="BitmapSource"/>).
/// <para>
/// O núcleo não conhece UI (ADR-2), então a conversão vive aqui. Uma fila de baixa
/// prioridade resolve as miniaturas fora da thread de interface: a lista aparece na
/// hora com um espaço reservado e as imagens vão chegando — o contrário de travar a
/// rolagem esperando o Shell decodificar 40 vídeos.
/// </para>
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private readonly ThumbnailProvider _provider = new(cacheCapacity: 4096);
    private readonly ConcurrentDictionary<(string, ThumbnailSize, bool), BitmapSource> _cache = new();
    private readonly SemaphoreSlim _throttle;

    public ThumbnailService(int maxConcurrency = 4)
    {
        // O Shell serializa parte do trabalho internamente; passar de ~4 pedidos
        // simultâneos não acelera e ainda concorre com a thread de UI.
        _throttle = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    /// <summary>Miniatura já em cache, ou <c>null</c>. Nunca bloqueia.</summary>
    public BitmapSource? GetCached(string path, ThumbnailSize size, bool preferContent) =>
        _cache.TryGetValue((path, size, preferContent), out BitmapSource? cached) ? cached : null;

    public async Task<BitmapSource?> GetAsync(string path, ThumbnailSize size, bool preferContent,
                                              CancellationToken cancellationToken = default)
    {
        var key = (path, size, preferContent);
        if (_cache.TryGetValue(key, out BitmapSource? cached)) return cached;

        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out cached)) return cached;

            ThumbnailBitmap? raw = await Task.Run(
                () => _provider.Get(path, size, preferContent), cancellationToken).ConfigureAwait(false);

            if (raw is null) return null;

            BitmapSource bitmap = ToBitmapSource(raw);
            _cache.TryAdd(key, bitmap);
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static BitmapSource ToBitmapSource(ThumbnailBitmap raw)
    {
        BitmapSource bitmap = BitmapSource.Create(
            raw.Width, raw.Height,
            96, 96,
            PixelFormats.Bgra32,
            palette: null,
            raw.Bgra32,
            raw.Stride);

        // Freeze permite usar a imagem em qualquer thread e dispensa o WPF de
        // rastrear mudanças — sem isso, cada miniatura custaria sincronização.
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Decide se este arquivo merece miniatura do conteúdo. Um <c>.exe</c> não tem
    /// "conteúdo visual", então pedir isso ao Shell só gastaria I/O.
    /// </summary>
    public static bool WantsContent(string fileName) => ThumbnailProvider.WantsContentThumbnail(fileName);

    public void Dispose()
    {
        _throttle.Dispose();
        _provider.Dispose();
        _cache.Clear();
    }
}

/// <summary>Tamanhos de ícone oferecidos na barra de ferramentas.</summary>
public sealed record IconSizeOption(ThumbnailSize Size, string LabelKey)
{
    public static IReadOnlyList<IconSizeOption> All { get; } =
    [
        new(ThumbnailSize.Tiny, "icon.tiny"),
        new(ThumbnailSize.Small, "icon.small"),
        new(ThumbnailSize.Medium, "icon.medium"),
        new(ThumbnailSize.Large, "icon.large"),
        new(ThumbnailSize.ExtraLarge, "icon.extraLarge"),
        new(ThumbnailSize.Huge, "icon.huge"),
    ];

    /// <summary>Rótulo no idioma ativo — é o que o ComboBox exibe.</summary>
    public string Label => L.T(LabelKey);

    public override string ToString() => Label;
}
