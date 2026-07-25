using System.Windows;
using Microsoft.Win32;

namespace Vacuon.App.Infra;

/// <summary>
/// Troca as cores do tema em runtime.
/// <para>
/// Funciona porque todo estilo referencia as cores por <c>DynamicResource</c>. A troca
/// escreve chave por chave em <c>Application.Current.Resources</c> em vez de substituir
/// um item de <c>MergedDictionaries</c>: substituir o item invalida só parte da árvore
/// visual — na prática a barra lateral continuava com o pincel do tema anterior
/// enquanto o resto da janela já havia trocado.
/// </para>
/// </summary>
public static class ThemeManager
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string RegistryValue = "AppsUseLightTheme";

    private static ThemeChoice _choice = ThemeChoice.System;

    /// <summary>Tema efetivamente aplicado (nunca <see cref="ThemeChoice.System"/>).</summary>
    public static ThemeChoice Effective { get; private set; } = ThemeChoice.Dark;

    public static event Action? Changed;

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        ThemeChoice resolved = choice == ThemeChoice.System ? ReadSystemPreference() : choice;

        string uri = resolved == ThemeChoice.Light ? "Themes/Light.xaml" : "Themes/Dark.xaml";

        var theme = new ResourceDictionary
        {
            Source = new Uri(uri, UriKind.Relative),
        };

        ResourceDictionary target = Application.Current.Resources;

        foreach (object key in theme.Keys)
            target[key] = theme[key];

        Effective = resolved;
        Changed?.Invoke();
    }

    /// <summary>
    /// Reaplica se o usuário mudou o tema do Windows enquanto o app estava aberto.
    /// Só faz algo quando a escolha é "acompanhar o sistema".
    /// </summary>
    public static void OnSystemPreferenceChanged()
    {
        if (_choice != ThemeChoice.System) return;
        if (ReadSystemPreference() == Effective) return;
        Apply(ThemeChoice.System);
    }

    private static ThemeChoice ReadSystemPreference()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            // Ausência do valor significa tema escuro nas builds em que ele não é escrito.
            return key?.GetValue(RegistryValue) is int light && light != 0
                ? ThemeChoice.Light
                : ThemeChoice.Dark;
        }
        catch
        {
            return ThemeChoice.Dark;
        }
    }
}
