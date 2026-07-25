using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Vacuon.Core.Localization;

/// <summary>
/// Idiomas disponíveis. Inglês é o padrão; português é opcional.
/// </summary>
public enum AppLanguage
{
    /// <summary>Inglês (en-US). Padrão do aplicativo.</summary>
    English,
    /// <summary>Português do Brasil (pt-BR).</summary>
    Portuguese,
    /// <summary>Segue o idioma do Windows, caindo para inglês se não houver tradução.</summary>
    System,
}

/// <summary>
/// Localizador do Vacuon.
/// <para>
/// Dicionários JSON embutidos no assembly, um por idioma, com o inglês servindo de
/// base: qualquer chave ausente na tradução cai para o texto em inglês em vez de
/// aparecer como <c>[chave]</c> na interface. Isso mantém a tradução parcial
/// utilizável, o que importa quando novas strings entram antes de serem traduzidas.
/// </para>
/// <para>
/// Vive no núcleo porque a CLI, a interface e as heurísticas de segurança todas
/// produzem texto para o usuário — não faria sentido cada uma ter o seu.
/// </para>
/// </summary>
public static class L
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, string> _fallback = [];
    private static Dictionary<string, string> _active = [];

    /// <summary>Idioma escolhido, como veio da configuração.</summary>
    public static AppLanguage Language { get; private set; } = AppLanguage.English;

    /// <summary>Cultura efetiva — decide separador decimal, milhar e formato de data.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Disparado após a troca de idioma, para as interfaces se atualizarem.</summary>
    public static event Action? Changed;

    static L() => Use(AppLanguage.English);

    public static void Use(AppLanguage language)
    {
        lock (Gate)
        {
            Language = language;

            string tag = Resolve(language);
            Culture = CultureInfo.GetCultureInfo(tag);

            // O inglês é sempre carregado, mesmo quando outro idioma está ativo:
            // é dele que vem o texto de qualquer chave ainda não traduzida.
            _fallback = Load("en-US");
            _active = tag == "en-US" ? _fallback : Load(tag);
        }

        // A cultura corrente do processo acompanha, para que ToString("N0") e afins
        // usem o separador certo sem ninguém passar CultureInfo em cada chamada.
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;

        Changed?.Invoke();
    }

    /// <summary>
    /// Converte a preferência em uma etiqueta de cultura concreta.
    /// <para>
    /// Só português tem tradução, então qualquer outro idioma do sistema resolve para
    /// inglês. Isso é deliberado: é melhor entregar inglês inteiro do que uma mistura.
    /// </para>
    /// </summary>
    private static string Resolve(AppLanguage language) => language switch
    {
        AppLanguage.Portuguese => "pt-BR",
        AppLanguage.System => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("pt", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : "en-US",
        _ => "en-US",
    };

    /// <summary>Texto da chave, com o inglês como rede de segurança.</summary>
    public static string T(string key)
    {
        if (_active.TryGetValue(key, out string? text)) return text;
        if (_fallback.TryGetValue(key, out text)) return text;

        // Chave inexistente é erro de programação, não de tradução. Aparecer entre
        // colchetes na tela é melhor do que sumir em silêncio.
        return $"[{key}]";
    }

    /// <summary>Texto da chave com substituição de <c>{0}</c>, <c>{1}</c>… na cultura ativa.</summary>
    public static string T(string key, params object?[] args) =>
        string.Format(Culture, T(key), args);

    /// <summary>Todas as chaves do idioma ativo. Usado para popular os recursos do WPF.</summary>
    public static IEnumerable<KeyValuePair<string, string>> All()
    {
        foreach (string key in _fallback.Keys)
            yield return new KeyValuePair<string, string>(key, T(key));
    }

    private static Dictionary<string, string> Load(string tag)
    {
        string resource = $"Vacuon.Core.Localization.Strings.{tag}.json";

        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream is null) return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
