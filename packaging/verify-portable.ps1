<#
.SYNOPSIS
  Confere que o executavel publicado funciona SOZINHO, longe da pasta em que nasceu.

.DESCRIPTION
  "Compilou" nao e o mesmo que "roda na maquina de quem baixou". A 0.3.1 quase saiu
  quebrada porque o WPF carrega cinco DLLs nativas e, sem
  IncludeNativeLibrariesForSelfExtract, elas ficam SOLTAS ao lado do .exe em vez de
  entrar nele. Rodando da propria pasta de publicacao passava; copiado para
  qualquer outro lugar, o processo morria antes de abrir a janela.

  E morria mal: 0xC000041D, sem janela e sem mensagem, porque a excecao estoura
  dentro do WndProc e vira fail-fast — o que passa por cima de qualquer handler de
  excecao nao tratada. O erro de verdade so aparece no stderr.

  Este script reproduz exatamente a situacao de quem baixa: leva SO o .exe para uma
  pasta vazia e roda de la.

.EXAMPLE
  .\verify-portable.ps1 -PublishDir ..\artifacts\gui -Executable Vacuon.exe -ExpectWindow
  .\verify-portable.ps1 -PublishDir ..\artifacts\cli -Executable vacuon.exe -Arguments version
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $Executable,

    # GUI: espera uma janela. CLI: espera saida e codigo 0.
    [switch] $ExpectWindow,
    [string[]] $Arguments = @(),
    [int] $TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$failures = @()

if (-not (Test-Path $PublishDir)) { throw "Pasta de publicacao nao existe: $PublishDir" }

# ---- 1. nada pode ter ficado solto ao lado do executavel ----------------------
# .pdb e simbolo de depuracao, nao dependencia; qualquer outra coisa significa que
# o "arquivo unico" nao e unico.
$stray = Get-ChildItem $PublishDir -File |
         Where-Object { $_.Name -ne $Executable -and $_.Extension -ne '.pdb' }

if ($stray) {
    $failures += "sobraram arquivos ao lado do executavel: $($stray.Name -join ', ')"
    Write-Host "  [X] publicacao nao e um arquivo unico" -ForegroundColor Red
    $stray | ForEach-Object { Write-Host "      $($_.Name)" -ForegroundColor Red }
} else {
    Write-Host "  [ok] nada solto ao lado do executavel"
}

# ---- 2. roda de uma pasta vazia, com SO o executavel --------------------------
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) "vacuon-portable-$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force $sandbox | Out-Null

try {
    $target = Join-Path $sandbox $Executable
    Copy-Item (Join-Path $PublishDir $Executable) $target

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $target
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    # ArgumentList so existe no .NET Core; o Windows PowerShell 5.1 roda em .NET
    # Framework, onde a propriedade e nula. Os argumentos aqui sao nossos e simples.
    if ($Arguments.Count -gt 0) { $psi.Arguments = ($Arguments -join ' ') }

    $proc = [System.Diagnostics.Process]::Start($psi)

    if ($ExpectWindow) {
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $ok = $false

        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            if ($proc.HasExited) { break }
            $proc.Refresh()
            if ($proc.MainWindowHandle -ne 0) { $ok = $true; break }
        }

        if ($ok) {
            Write-Host "  [ok] janela abriu fora da pasta de publicacao"
            $proc.Kill()
        } else {
            $code = if ($proc.HasExited) { $proc.ExitCode } else { 'sem janela' }
            $failures += "nao abriu janela rodando isolado (exit=$code)"
            Write-Host "  [X] nao abriu janela (exit=$code)" -ForegroundColor Red

            # A causa real quase nunca esta no codigo de saida.
            $err = $proc.StandardError.ReadToEnd()
            if ($err) { Write-Host $err -ForegroundColor DarkGray }
            if (-not $proc.HasExited) { $proc.Kill() }
        }
    } else {
        $out = $proc.StandardOutput.ReadToEnd()
        $err = $proc.StandardError.ReadToEnd()
        if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) { $proc.Kill(); $failures += "travou" }
        elseif ($proc.ExitCode -ne 0) {
            $failures += "saiu com codigo $($proc.ExitCode)"
            Write-Host "  [X] exit=$($proc.ExitCode)" -ForegroundColor Red
            if ($err) { Write-Host $err -ForegroundColor DarkGray }
        } else {
            Write-Host "  [ok] rodou isolado: $(($out -split "`n")[0].Trim())"
        }
    }
} finally {
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures) {
    Write-Host ""
    Write-Host "FALHOU: $($failures -join ' | ')" -ForegroundColor Red
    exit 1
}

Write-Host "  $Executable esta pronto para publicar." -ForegroundColor Green
exit 0
