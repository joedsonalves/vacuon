using System.Windows;
using Vacuon.Core.Localization;

namespace Vacuon.App.Infra;

/// <summary>
/// Espelha os textos do idioma ativo nos recursos da aplicação, com o prefixo <c>S.</c>.
/// <para>
/// É o mesmo mecanismo do tema: o XAML referencia <c>{DynamicResource S.nav.dashboard}</c>
/// e a troca de idioma reescreve as chaves, sem recriar a janela nem tocar em nenhuma
/// View. Com <c>StaticResource</c> ou uma markup extension resolvida na carga, mudar de
/// idioma exigiria reiniciar.
/// </para>
/// </summary>
public static class LocalizationBridge
{
    /// <summary>Prefixo dos recursos de texto. Separa idioma (<c>S.</c>) de cor (sem prefixo).</summary>
    public const string Prefix = "S.";

    public static void Attach()
    {
        Publish();
        L.Changed += Publish;
    }

    public static void Detach() => L.Changed -= Publish;

    private static void Publish()
    {
        if (Application.Current is null) return;

        ResourceDictionary target = Application.Current.Resources;

        foreach ((string key, string value) in L.All())
            target[Prefix + key] = value;
    }
}
