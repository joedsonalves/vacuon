using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
