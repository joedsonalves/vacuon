<#
    Gera assets/vacuon.ico a partir do mesmo desenho do vacuon-logo.svg.

    O .ico é redesenhado em cada tamanho em vez de ser reamostrado de uma imagem
    grande: reamostrar 256 -> 16 vira um borrão. Aqui o 16x16 ganha uma versão
    simplificada, que é o que faz o ícone ser legível na barra de tarefas.

    Uso:  powershell -ExecutionPolicy Bypass -File assets\generate-icon.ps1
#>

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$icoPath = Join-Path $outDir 'vacuon.ico'
$pngPath = Join-Path $outDir 'vacuon-256.png'

$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-VacuonBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0
    $simple = $size -le 24   # abaixo disso, detalhe vira sujeira

    # --- placa de fundo ---
    $radius = [Math]::Max(2.0, 56 * $s)
    $plate = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $plate.AddArc(0, 0, $d, $d, 180, 90)
    $plate.AddArc($size - $d, 0, $d, $d, 270, 90)
    $plate.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $plate.AddArc(0, $size - $d, $d, $d, 90, 90)
    $plate.CloseFigure()

    $plateBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 28, 32, 48),
        [System.Drawing.Color]::FromArgb(255, 13, 15, 22))
    $g.FillPath($plateBrush, $plate)

    # --- gradiente âmbar reutilizado por todas as formas ---
    $amber = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, [int](40 * $s))),
        (New-Object System.Drawing.Point(0, [int](230 * $s))),
        [System.Drawing.Color]::FromArgb(255, 255, 194, 75),
        [System.Drawing.Color]::FromArgb(255, 242, 101, 10))

    # --- barras (os arquivos sendo puxados) ---
    # Alocação explícita de propósito: `$x = if (...) { ,@(1,2,3,4) }` faz o PowerShell
    # desenrolar o array externo na saída do bloco e $x vira 4 inteiros soltos.
    if ($simple) {
        $bars = New-Object 'object[]' 1
        $bars[0] = @(76, 60, 104, 26)               # uma só barra, mais grossa
        $opacities = @(255)
    }
    else {
        $bars = New-Object 'object[]' 3
        $bars[0] = @(58, 52, 140, 17)
        $bars[1] = @(76, 84, 104, 17)
        $bars[2] = @(94, 116, 68, 17)
        $opacities = @(115, 184, 255)
    }

    for ($i = 0; $i -lt $bars.Count; $i++) {
        $b = $bars[$i]
        $x = $b[0] * $s; $y = $b[1] * $s; $w = $b[2] * $s; $h = $b[3] * $s
        $r = [Math]::Min($h / 2, $w / 2)

        $alpha = $opacities[$i]
        $solid = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb($alpha, 255, 154, 31))

        # Abaixo de ~2 px de raio o arredondamento some no antialias e o GDI+
        # rejeita arcos de dimensão zero — retângulo reto é o caminho honesto.
        if (($r * 2) -ge 2) {
            $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
            $bar.AddArc($x, $y, $r * 2, $h, 90, 180)
            $bar.AddArc($x + $w - $r * 2, $y, $r * 2, $h, 270, 180)
            $bar.CloseFigure()
            $g.FillPath($solid, $bar)
            $bar.Dispose()
        }
        else {
            $g.FillRectangle($solid, $x, $y, [Math]::Max($w, 1), [Math]::Max($h, 1))
        }

        $solid.Dispose()
    }

    # --- o funil: um V que também é o vácuo ---
    $funnelTop = if ($simple) { 104 } else { 146 }
    $funnel = New-Object System.Drawing.Drawing2D.GraphicsPath
    $funnel.AddPolygon(@(
        (New-Object System.Drawing.PointF((74 * $s), ($funnelTop * $s))),
        (New-Object System.Drawing.PointF((182 * $s), ($funnelTop * $s))),
        (New-Object System.Drawing.PointF((128 * $s), (226 * $s)))
    ))
    $g.FillPath($amber, $funnel)

    # --- ponto de fuga (só onde cabe) ---
    if (-not $simple) {
        $hole = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(255, 13, 15, 22))
        # Centro em y=202: mais para baixo o círculo ultrapassa a lateral do funil,
        # que ali já tem menos de 18 px de largura.
        $g.FillEllipse($hole, (119 * $s), (193 * $s), (18 * $s), (18 * $s))
        $hole.Dispose()
    }

    $funnel.Dispose(); $amber.Dispose(); $plateBrush.Dispose(); $plate.Dispose(); $g.Dispose()
    return $bmp
}

# ---------------------------------------------------------------------------
# Monta o .ico com entradas PNG (suportado desde o Vista e muito menor que BMP)
# ---------------------------------------------------------------------------
$pngBlobs = @()
foreach ($size in $sizes) {
    $bmp = New-VacuonBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBlobs += , @{ Size = $size; Bytes = $ms.ToArray() }

    if ($size -eq 256) { [System.IO.File]::WriteAllBytes($pngPath, $ms.ToArray()) }

    $ms.Dispose(); $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)

$w.Write([UInt16]0)                    # reservado
$w.Write([UInt16]1)                    # tipo 1 = ícone
$w.Write([UInt16]$pngBlobs.Count)

$offset = 6 + (16 * $pngBlobs.Count)
foreach ($blob in $pngBlobs) {
    $dim = if ($blob.Size -ge 256) { 0 } else { $blob.Size }
    $w.Write([Byte]$dim)               # largura (0 = 256)
    $w.Write([Byte]$dim)               # altura
    $w.Write([Byte]0)                  # cores da paleta
    $w.Write([Byte]0)                  # reservado
    $w.Write([UInt16]1)                 # planos
    $w.Write([UInt16]32)                # bits por pixel
    $w.Write([UInt32]$blob.Bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $blob.Bytes.Length
}

foreach ($blob in $pngBlobs) { $w.Write($blob.Bytes) }

$w.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$w.Dispose(); $out.Dispose()

Write-Output "Gerado: $icoPath ($($pngBlobs.Count) tamanhos: $($sizes -join ', '))"
Write-Output "Gerado: $pngPath"
