using System.IO;
using System.Windows;
using Microsoft.Win32;
using Vacuon.App.Infra;
using Vacuon.App.ViewModels;
using Vacuon.Core.Localization;

namespace Vacuon.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before anything else, so a failure while starting up still has somewhere to land.
        // Without this the app died to exit code 0xC000041D with no window and no message —
        // the only evidence was a Windows Error Reporting entry naming KERNELBASE.dll, which
        // tells whoever hit it precisely nothing.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportCrash(args.ExceptionObject as Exception, "AppDomain");
        DispatcherUnhandledException += (_, args) =>
            ReportCrash(args.Exception, "Dispatcher");

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

    /// <summary>
    /// Writes what went wrong somewhere the person can find it, and says where.
    /// <para>
    /// Deliberately free of translation and of every other part of the app: a crash during
    /// startup can happen before the language is loaded, and a crash handler that itself
    /// depends on the thing that broke reports nothing.
    /// </para>
    /// </summary>
    private static void ReportCrash(Exception? exception, string source)
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Vacuon", "crash.log");

        string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] Vacuon {MainViewModel.AppVersion}\n" +
                      $"{exception}\n\n";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            path = "(could not be written)";
        }

        try
        {
            MessageBox.Show(
                $"{exception?.GetType().Name}: {exception?.Message}\n\nDetails written to:\n{path}",
                "Vacuon stopped", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex) when (ex is InvalidOperationException) { /* sem UI ainda */ }
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
