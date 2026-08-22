using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vacuon.Core.Localization;
using Vacuon.Core.Preview;

namespace Vacuon.App.Infra;

public enum ThemeChoice
{
    /// <summary>Acompanha a preferência do Windows e reage quando ela muda.</summary>
    System,
    Dark,
    Light,
}

/// <summary>
/// Preferências do usuário, em <c>%AppData%\Vacuon\settings.json</c>.
/// <para>
/// Se o arquivo estiver corrompido ou ilegível, o app usa os padrões e segue — uma
/// preferência perdida não é motivo para impedir alguém de abrir o programa.
/// </para>
/// </summary>
public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    /// <summary>
    /// Idioma da interface. Inglês é o padrão; o português é opcional.
    /// <para>
    /// Padrão explícito em vez de "seguir o sistema": o inglês é a única tradução
    /// completa por definição (é a base de onde as outras herdam), então ele é a
    /// escolha previsível para quem abre o app pela primeira vez.
    /// </para>
    /// </summary>
    public AppLanguage Language { get; set; } = AppLanguage.English;

    /// <summary>
    /// Relança elevado no startup. Vale a pena porque só com elevação existe a
    /// leitura da MFT — a diferença entre segundos e minutos.
    /// </summary>
    public bool AlwaysRunAsAdministrator { get; set; }

    public ThumbnailSize IconSize { get; set; } = ThumbnailSize.Medium;

    /// <summary>Miniatura do conteúdo real para imagem e vídeo, em vez do ícone do tipo.</summary>
    public bool ContentThumbnails { get; set; } = true;

    public int TopItemCount { get; set; } = 100;
    public bool ShowSizeOnDisk { get; set; } = true;
    public bool ShowHiddenAndSystem { get; set; } = true;

    /// <summary>Última unidade varrida, para reabrir onde parou.</summary>
    public string? LastVolume { get; set; }

    /// <summary>Icon in the notification area, showing free space.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// Whether crossing the threshold below raises a Windows notification.
    /// <para>
    /// Separate from <see cref="ShowTrayIcon"/> even though the notification is posted
    /// through the icon: wanting the icon out of the way is a different wish from wanting to
    /// hear nothing when the disk fills.
    /// </para>
    /// </summary>
    public bool NotifyOnLowSpace { get; set; } = true;

    /// <summary>Free space below which a volume is worth a notification. Default 10 GiB.</summary>
    public long LowSpaceThresholdBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Whether closing the window leaves the app running in the notification area.
    /// <para>
    /// Off by default. An app that keeps running after being closed, without having been
    /// asked, is one people find in the tray days later wondering what put it there.
    /// </para>
    /// </summary>
    public bool CloseToTray { get; set; }

    // ---------------------------------------------------------------

    [JsonIgnore]
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vacuon");

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(FilePath), Options);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Preferência ilegível não pode impedir o app de abrir.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
