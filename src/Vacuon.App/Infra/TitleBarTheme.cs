using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Vacuon.App.Infra;

/// <summary>
/// Pinta a barra de título junto com o tema do app.
/// <para>
/// A barra de título é desenhada pelo Windows, não pelo WPF: sem isto, o tema escuro
/// fica com uma faixa branca no topo. O DWM expõe a preferência por janela desde o
/// Windows 10 1809 — em builds anteriores a chamada falha em silêncio e a barra
/// continua clara, que é degradação aceitável.
/// </para>
/// </summary>
public static class TitleBarTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window, bool dark)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;

        int value = dark ? 1 : 0;

        // O atributo mudou de número no 20H1. Tentar o novo e cair para o antigo
        // cobre as duas famílias sem detectar versão.
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
    }
}
