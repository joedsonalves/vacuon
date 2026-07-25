using Vacuon.Core.Localization;

namespace Vacuon.Core.Analyzers;

/// <summary>
/// Classificação de arquivos por categoria. Além de colorir o treemap, é o que decide
/// se um item ganha miniatura do conteúdo ou o ícone padrão do tipo (PRD F6.5).
/// <para>
/// A categoria é identificada por uma <b>chave estável</b> (<c>category.video</c>), não
/// pelo texto exibido: assim comparações, cores e testes não quebram quando o idioma
/// muda. O nome legível sai de <see cref="DisplayName"/>.
/// </para>
/// </summary>
public static class FileCategories
{
    public const string Video = "category.video";
    public const string Image = "category.image";
    public const string Audio = "category.audio";
    public const string Document = "category.document";
    public const string Archive = "category.archive";
    public const string Installer = "category.installer";
    public const string Code = "category.code";
    public const string Executable = "category.executable";
    public const string Build = "category.build";
    public const string Disk = "category.disk";
    public const string Database = "category.database";
    public const string Font = "category.font";
    public const string Log = "category.log";
    public const string Other = "category.other";

    public const string NoExtension = "category.noExtension";

    private static readonly Dictionary<string, string> Map = BuildMap();

    /// <summary>Chave da categoria da extensão informada.</summary>
    public static string Of(string extension) =>
        Map.TryGetValue(extension, out string? category) ? category : Other;

    public static string Of(ReadOnlySpan<char> fileName) =>
        Of(SizeAnalyzer.ExtractExtension(fileName));

    /// <summary>Nome da categoria no idioma ativo.</summary>
    public static string DisplayName(string categoryKey) => L.T(categoryKey);

    /// <summary>Nome da categoria de um arquivo, direto do nome dele.</summary>
    public static string DisplayNameOf(ReadOnlySpan<char> fileName) => L.T(Of(fileName));

    /// <summary>
    /// Categorias que ganham miniatura do próprio conteúdo. As demais recebem o ícone
    /// do tipo, resolvido pelo Shell do Windows.
    /// </summary>
    public static bool HasContentThumbnail(string categoryKey) =>
        categoryKey is Video or Image or Document;

    private static Dictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string category, params string[] extensions)
        {
            foreach (string e in extensions) map[e] = category;
        }

        Add(Video, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg",
                   ".mpeg", ".ts", ".m2ts", ".vob", ".3gp", ".ogv", ".mts", ".braw", ".r3d");

        Add(Image, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic",
                   ".heif", ".avif", ".svg", ".ico", ".psd", ".xcf", ".raw", ".cr2", ".cr3",
                   ".nef", ".arw", ".dng", ".orf", ".rw2", ".jfif");

        Add(Audio, ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".aiff",
                   ".alac", ".mid", ".midi", ".ape");

        Add(Document, ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods",
                      ".odp", ".rtf", ".txt", ".md", ".epub", ".mobi", ".djvu", ".pages");

        Add(Archive, ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".zst", ".cab",
                     ".arj", ".lzh", ".tgz", ".pkg");

        Add(Installer, ".msi", ".msix", ".appx", ".msu", ".msp", ".deb", ".rpm");

        Add(Code, ".cs", ".c", ".cpp", ".h", ".hpp", ".java", ".py", ".js", ".ts", ".jsx", ".tsx",
                  ".go", ".rs", ".rb", ".php", ".swift", ".kt", ".lua", ".sh", ".ps1", ".sql",
                  ".html", ".css", ".scss", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini");

        Add(Executable, ".exe", ".dll", ".sys", ".ocx", ".scr", ".com", ".pif", ".cpl", ".drv");

        Add(Build, ".obj", ".o", ".pdb", ".ilk", ".lib", ".a", ".class", ".pyc", ".pyo",
                   ".nupkg", ".whl", ".jar", ".war", ".map", ".tlog", ".idb");

        Add(Disk, ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".vdi", ".qcow2", ".dmg", ".bin",
                  ".nrg", ".mdf", ".wim", ".esd");

        Add(Database, ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb", ".dbf", ".ldf", ".bak");

        Add(Font, ".ttf", ".otf", ".woff", ".woff2", ".fon", ".fnt");

        Add(Log, ".log", ".tmp", ".temp", ".etl", ".dmp", ".mdmp", ".evtx", ".old", ".bak2",
                 ".part", ".crdownload", ".chk", ".gid");

        return map;
    }
}
