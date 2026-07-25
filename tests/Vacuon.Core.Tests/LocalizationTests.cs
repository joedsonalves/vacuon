using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Localization;
using Vacuon.Core.Security;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Guarda-corpo da tradução. Sem estes testes, uma chave nova entra só no inglês e o
/// português passa meses caindo para o fallback sem ninguém notar — e um dicionário
/// que não carrega quebra a interface inteira sem erro nenhum no build.
/// </summary>
public class LocalizationTests
{
    private static Dictionary<string, string> Load(string tag)
    {
        string resource = $"Vacuon.Core.Localization.Strings.{tag}.json";

        using Stream? stream = typeof(L).Assembly.GetManifestResourceStream(resource);

        Assert.NotNull(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream!)!;
    }

    [Fact]
    public void BothDictionariesAreEmbeddedInTheMainAssembly()
    {
        // "Strings.en-US.json" casa com o padrão nome.cultura.extensão, e sem
        // WithCulture="false" no csproj o MSBuild manda o arquivo para um assembly
        // SATÉLITE. O build passa, GetManifestResourceStream devolve null e toda a
        // interface vira [chave]. Este teste é o que impede a regressão.
        string[] names = typeof(L).Assembly.GetManifestResourceNames();

        Assert.Contains("Vacuon.Core.Localization.Strings.en-US.json", names);
        Assert.Contains("Vacuon.Core.Localization.Strings.pt-BR.json", names);
    }

    [Fact]
    public void PortugueseTranslatesEveryEnglishKey()
    {
        Dictionary<string, string> en = Load("en-US");
        Dictionary<string, string> pt = Load("pt-BR");

        var missing = en.Keys
            .Where(k => !k.StartsWith('_'))
            .Where(k => !pt.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            $"Chaves sem tradução em pt-BR ({missing.Count}): {string.Join(", ", missing)}");
    }

    [Fact]
    public void PortugueseHasNoOrphanKeys()
    {
        Dictionary<string, string> en = Load("en-US");
        Dictionary<string, string> pt = Load("pt-BR");

        // Chave que só existe no português é lixo: nada a consome, e ela sugere que
        // alguém renomeou a chave em inglês e esqueceu de acompanhar.
        var orphans = pt.Keys
            .Where(k => !k.StartsWith('_'))
            .Where(k => !en.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphans.Count == 0,
            $"Chaves órfãs em pt-BR ({orphans.Count}): {string.Join(", ", orphans)}");
    }

    [Fact]
    public void PlaceholdersMatchBetweenLanguages()
    {
        Dictionary<string, string> en = Load("en-US");
        Dictionary<string, string> pt = Load("pt-BR");

        var mismatches = new List<string>();

        foreach ((string key, string english) in en)
        {
            if (key.StartsWith('_')) continue;
            if (!pt.TryGetValue(key, out string? portuguese)) continue;

            // {0}, {1}… precisam ser os mesmos: um {1} sobrando em uma das línguas
            // vira FormatException em runtime, e só na linha que ninguém testou.
            HashSet<string> a = Placeholders(english);
            HashSet<string> b = Placeholders(portuguese);

            if (!a.SetEquals(b))
                mismatches.Add($"{key} (en: {Join(a)} / pt: {Join(b)})");
        }

        Assert.True(mismatches.Count == 0,
            $"Marcadores divergentes: {string.Join(" · ", mismatches)}");

        static HashSet<string> Placeholders(string text) =>
            [.. Regex.Matches(text, @"\{\d+\}").Select(m => m.Value)];

        static string Join(HashSet<string> set) =>
            set.Count == 0 ? "nenhum" : string.Join(",", set.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// Varre o código-fonte procurando literais com forma de chave e cobra que todos
    /// existam no dicionário.
    /// <para>
    /// Existe porque uma chave errada não quebra o build: ela só aparece como
    /// <c>[chave]</c> na tela de quem usa. Dois casos reais escaparam de uma conferência
    /// manual — <c>theme.switchToLight</c> e <c>theme.switchToDark</c>, escondidos dentro
    /// de um ternário — e foi este teste que os pegou.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryKeyUsedInSourceExistsInTheDictionary()
    {
        string? repo = FindRepositoryRoot();
        if (repo is null) return; // fora da árvore de código (pacote publicado): nada a checar

        Dictionary<string, string> en = Load("en-US");

        // Chaves de tradução têm a forma grupo.subChave. O conjunto de grupos vem do
        // próprio dicionário, para não confundir "settings.json" com uma chave.
        HashSet<string> groups = [.. en.Keys.Where(k => !k.StartsWith('_')).Select(k => k.Split('.')[0])];
        var keyShape = new Regex(@"""([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)""");

        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(Path.Combine(repo, "src"), "*.*",
                                                        SearchOption.AllDirectories))
        {
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) is false &&
                file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) is false) continue;

            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            string text = File.ReadAllText(file);

            foreach (Match match in keyShape.Matches(text))
            {
                string key = match.Groups[1].Value;
                if (!groups.Contains(key.Split('.')[0])) continue;

                // "settings.json" tem a forma de chave e é nome de arquivo. Descartar
                // por extensão é mais honesto que uma lista de exceções que cresce.
                if (FileExtensions.Contains(key[(key.LastIndexOf('.') + 1)..])) continue;

                if (!en.ContainsKey(key)) missing.Add($"{key} ({Path.GetFileName(file)})");
            }
        }

        Assert.True(missing.Count == 0,
            $"Chaves usadas no código e ausentes do dicionário ({missing.Count}): {string.Join(", ", missing)}");
    }

    /// <summary>Últimos segmentos que denunciam nome de arquivo, não chave de tradução.</summary>
    private static readonly HashSet<string> FileExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "json", "xaml", "cs", "csproj", "sln", "exe", "dll", "ico", "png", "bmp",
            "md", "txt", "log", "manifest", "props", "yml", "resources",
        };

    /// <summary>Sobe do diretório do assembly até achar a raiz do repositório.</summary>
    private static string? FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Vacuon.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public void EnglishIsTheDefault()
    {
        L.Use(AppLanguage.English);

        Assert.Equal("en-US", L.Culture.Name);
        Assert.Equal("Dashboard", L.T("nav.dashboard"));
    }

    [Fact]
    public void PortugueseSwitchesTextAndNumberFormat()
    {
        try
        {
            L.Use(AppLanguage.Portuguese);

            Assert.Equal("pt-BR", L.Culture.Name);
            Assert.Equal("Painel", L.T("nav.dashboard"));

            // A cultura acompanha o idioma: o separador decimal muda junto, senão a
            // interface fica em português com número no formato americano.
            Assert.Equal("1,5", 1.5.ToString("N1", L.Culture));
        }
        finally
        {
            L.Use(AppLanguage.English);
        }
    }

    [Fact]
    public void UnknownKeyIsVisibleInsteadOfSilent()
    {
        // Chave inexistente é erro de programação. Aparecer entre colchetes é melhor
        // do que devolver string vazia e deixar um rótulo sumir da tela.
        Assert.Equal("[nao.existe]", L.T("nao.existe"));
    }

    [Fact]
    public void FormattingUsesTheActiveCulture()
    {
        try
        {
            L.Use(AppLanguage.Portuguese);
            Assert.Contains("1,5", L.T("scan.progress", "1,5%", "10", "20"));
        }
        finally
        {
            L.Use(AppLanguage.English);
        }
    }

    [Fact]
    public void CategoryKeysAllHaveTranslations()
    {
        Dictionary<string, string> en = Load("en-US");

        string[] keys =
        [
            FileCategories.Video, FileCategories.Image, FileCategories.Audio,
            FileCategories.Document, FileCategories.Archive, FileCategories.Installer,
            FileCategories.Code, FileCategories.Executable, FileCategories.Build,
            FileCategories.Disk, FileCategories.Database, FileCategories.Font,
            FileCategories.Log, FileCategories.Other, FileCategories.NoExtension,
        ];

        foreach (string key in keys) Assert.True(en.ContainsKey(key), $"sem tradução: {key}");
    }

    [Fact]
    public void AutorunDescriptionKeysAllResolve()
    {
        Dictionary<string, string> en = Load("en-US");

        foreach (AutorunLocation location in AutorunLocations.All)
        {
            Assert.True(en.ContainsKey(location.DescriptionKey),
                $"chave de autorun sem tradução: {location.DescriptionKey} ({location.DisplayPath})");

            // Description formata com DescriptionArgs; se o número de marcadores não
            // casar, isto lança aqui e não na tela do usuário.
            Assert.False(string.IsNullOrWhiteSpace(location.Description));
        }
    }

    [Fact]
    public void CategoryNamesDifferBetweenLanguages()
    {
        try
        {
            L.Use(AppLanguage.English);
            string english = FileCategories.DisplayName(FileCategories.Video);

            L.Use(AppLanguage.Portuguese);
            string portuguese = FileCategories.DisplayName(FileCategories.Video);

            Assert.Equal("Video", english);
            Assert.Equal("Vídeo", portuguese);
        }
        finally
        {
            L.Use(AppLanguage.English);
        }
    }
}
