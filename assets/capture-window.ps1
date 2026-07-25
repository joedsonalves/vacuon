<#
    Captura a janela do Vacuon para os prints do README.

    Usa PrintWindow com PW_RENDERFULLCONTENT em vez de CopyFromScreen: assim a
    captura pega o conteúdo real da janela mesmo que ela esteja parcialmente
    coberta, e não vaza nada do resto da área de trabalho do usuário.

    Uso:  powershell -ExecutionPolicy Bypass -File assets\capture-window.ps1 -Output docs\img\painel.png
#>
param(
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$ProcessName = 'Vacuon',
    [int]$DelaySeconds = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ($DelaySeconds -gt 0) { Start-Sleep -Seconds $DelaySeconds }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinCap
{
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // PW_RENDERFULLCONTENT: obrigatorio para janelas compostas pelo DWM (WPF).
    public const uint PW_RENDERFULLCONTENT = 2;

    // DWMWA_EXTENDED_FRAME_BOUNDS: GetWindowRect devolve a moldura invisivel de
    // redimensionamento e a captura sai com borda transparente sobrando.
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
}
'@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1

if (-not $proc) { throw "Processo '$ProcessName' sem janela principal." }

$hwnd = $proc.MainWindowHandle
[void][WinCap]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 700

$rect = New-Object WinCap+RECT
$hr = [WinCap]::DwmGetWindowAttribute($hwnd, [WinCap]::DWMWA_EXTENDED_FRAME_BOUNDS,
                                      [ref]$rect, [System.Runtime.InteropServices.Marshal]::SizeOf($rect))
if ($hr -ne 0) { throw "DwmGetWindowAttribute falhou: 0x$($hr.ToString('X8'))" }

$width  = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { throw "Dimensoes invalidas: ${width}x${height}" }

$bmp = New-Object System.Drawing.Bitmap($width, $height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $gfx.GetHdc()
try {
    if (-not [WinCap]::PrintWindow($hwnd, $hdc, [WinCap]::PW_RENDERFULLCONTENT)) {
        throw 'PrintWindow falhou.'
    }
}
finally {
    $gfx.ReleaseHdc($hdc)
    $gfx.Dispose()
}

$dir = Split-Path -Parent $Output
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

$bmp.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output "Capturado ${width}x${height} -> $Output"
