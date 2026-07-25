namespace Vacuon.Core.Analyzers;

/// <summary>
/// Classificação de arquivos por categoria. Além de colorir o treemap, é o que decide
/// se um item ganha miniatura do conteúdo ou o ícone padrão do tipo (PRD F6.5).
/// </summary>
public static class FileCategories
{
    public const string Video = "Vídeo";
    public const string Image = "Imagem";
    public const string Audio = "Áudio";
    public const string Document = "Documento";
    public const string Archive = "Compactado";
    public const string Installer = "Instalador";
    public const string Code = "Código";
    public const string Executable = "Executável";
    public const string Build = "Artefato de build";
    public const string Disk = "Imagem de disco / VM";
    public const string Database = "Banco de dados";
    public const string Font = "Fonte";
    public const string Log = "Log / temporário";
    public const string Other = "Outro";

    private static readonly Dictionary<string, string> Map = BuildMap();

    public static string Of(string extension) =>
        Map.TryGetValue(extension, out string? category) ? category : Other;

    public static string Of(ReadOnlySpan<char> fileName) =>
        Of(SizeAnalyzer.ExtractExtension(fileName));

    /// <summary>
    /// Categorias que ganham miniatura do próprio conteúdo. As demais recebem o ícone
    /// do tipo, resolvido pelo Shell do Windows.
    /// </summary>
    public static bool HasContentThumbnail(string category) =>
        category is Video or Image or Document;

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
