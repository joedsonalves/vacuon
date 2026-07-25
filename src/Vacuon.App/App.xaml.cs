using System.Windows;
using Microsoft.Win32;
using Vacuon.App.Infra;
using Vacuon.Core.Localization;

namespace Vacuon.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = AppSettings.Load();

        // "Sempre abrir como administrador": relança ANTES de criar a janela, para
        // não aparecer uma janela que morre em seguida. A guarda no argumento evita
        // laço infinito se o UAC for recusado.
        if (Settings.AlwaysRunAsAdministrator
            && !ElevationService.IsElevated
            && !ElevationService.WasRelaunchAttempted(e.Args)
            && ElevationService.RelaunchElevated())
        {
            return; // esta instância encerra; a elevada assume
        }

        // Idioma antes do tema e antes da janela: a barra lateral e o cabeçalho são
        // construídos já com o texto certo, sem um piscar em inglês.
        L.Use(Settings.Language);
        LocalizationBridge.Attach();

        ThemeManager.Apply(Settings.Theme);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        base.OnStartup(e);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // General cobre a troca de claro/escuro do Windows.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            ThemeManager.OnSystemPreferenceChanged();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        LocalizationBridge.Detach();
        Settings.Save();
        base.OnExit(e);
    }
}
