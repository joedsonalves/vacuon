<#
.SYNOPSIS
  Gera os tres manifestos que o winget-pkgs exige para uma versao do Vacuon.

.DESCRIPTION
  Os manifestos sao gerados, nao editados a mao. Sao tres arquivos que repetem
  PackageIdentifier e PackageVersion, e manter isso em sincronia manualmente e
  como o numero de versao acaba divergindo entre eles — o validador reclama, mas
  so depois de o PR estar aberto.

  O SHA256 vem do arquivo publicado, nunca digitado: o winget recusa o download
  se nao bater, e um hash errado no manifesto quebra a instalacao de todo mundo
  em vez de falhar aqui.

  A URL aponta para o asset COM versao no nome. O link 'releases/latest/download'
  serve para o README, onde a conveniencia vale mais que a precisao — aqui ele
  seria um erro, porque o hash e fixo e o conteudo do 'latest' muda.

.EXAMPLE
  .\new-manifest.ps1 -Version 0.3.1 -ExePath ..\..\artifacts\Vacuon-0.3.1-win-x64.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,

    # Arquivo local a partir do qual o hash e calculado. Passe o MESMO arquivo que
    # foi enviado para a release.
    [Parameter(Mandatory)] [string] $ExePath,

    [string] $OutputRoot = $PSScriptRoot,

    # Rodar 'winget validate' no fim. Desligue so se o winget nao estiver instalado.
    [switch] $SkipValidate
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) { throw "Nao achei o binario: $ExePath" }

$sha = (Get-FileHash -Algorithm SHA256 -Path $ExePath).Hash.ToUpperInvariant()
$size = (Get-Item $ExePath).Length
$date = (Get-Date).ToString('yyyy-MM-dd')

$id = 'Joedsonalves.Vacuon'
$repo = 'https://github.com/joedsonalves/vacuon'
$asset = "Vacuon-$Version-win-x64.exe"
$url = "$repo/releases/download/v$Version/$asset"

$dir = Join-Path $OutputRoot $Version
New-Item -ItemType Directory -Force -Path $dir | Out-Null

Write-Host "  versao   $Version"
Write-Host "  binario  $ExePath  ($([math]::Round($size/1MB,1)) MB)"
Write-Host "  sha256   $sha"
Write-Host "  url      $url"
Write-Host ""

# ---------------------------------------------------------------- version
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json

PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@ | Set-Content -Path (Join-Path $dir "$id.yaml") -Encoding utf8

# ---------------------------------------------------------------- installer
# InstallerType: portable — nao existe instalador. O winget guarda o .exe e cria
# um atalho na pasta de links dele, que ja esta no PATH.
#
# O atalho vem de 'Commands'. NAO usar 'PortableCommandAlias' aqui: esse campo so
# existe dentro de NestedInstallerFiles, para quando o portavel vem dentro de um
# zip, e o validador o reporta como campo desconhecido.
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json

PackageIdentifier: $id
PackageVersion: $Version
MinimumOSVersion: 10.0.19044.0
InstallerType: portable
Commands:
- vacuon
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@ | Set-Content -Path (Join-Path $dir "$id.installer.yaml") -Encoding utf8

# ---------------------------------------------------------------- locale en-US
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json

PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Joedson Alves
PublisherUrl: https://github.com/joedsonalves
PublisherSupportUrl: $repo/issues
PackageName: Vacuon
PackageUrl: $repo
License: MIT
LicenseUrl: $repo/blob/main/LICENSE
ShortDescription: Disk space analyzer that reads the NTFS MFT directly and shows what it is about to delete.
Description: |-
  Vacuon finds where the space on a Windows disk went. It reads the NTFS Master File
  Table straight off the volume, which indexes a million files in seconds instead of
  minutes, and falls back to the Windows API on volumes where that is not possible.

  Images and videos are shown as thumbnails in six sizes, so you can tell which of five
  large renders is the final one without opening any of them. Deletion goes to the Recycle
  Bin by default, and permanently only behind a confirmation you have to tick, with a
  protection list that refuses the volume root, Windows, System32 and kernel-owned files.

  It also finds files with identical content, draws the volume as a treemap where every
  box is sized by what it occupies, and can set files aside in a quarantine it can restore
  them from, instead of deleting them.

  Every scan cross-checks the space it attributed to files against what the volume reports
  as used, and says so when the two disagree.

  The interface is available in English and Portuguese. The fast path needs Administrator;
  without it the app still works through the slower traversal and says which one it used.
Moniker: vacuon
Tags:
- disk
- disk-analyzer
- disk-space
- disk-usage
- duplicate-finder
- mft
- ntfs
- storage
- treemap
ReleaseNotesUrl: $repo/releases/tag/v$Version
Documentations:
- DocumentLabel: Readme
  DocumentUrl: $repo/blob/main/README.md
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@ | Set-Content -Path (Join-Path $dir "$id.locale.en-US.yaml") -Encoding utf8

# ---------------------------------------------------------------- locale pt-BR
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.1.6.0.schema.json

PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: pt-BR
Publisher: Joedson Alves
PackageName: Vacuon
License: MIT
ShortDescription: Analisador de espaco em disco que le a MFT do NTFS direto e mostra o que voce esta apagando.
Description: |-
  O Vacuon encontra para onde foi o espaco de um disco no Windows. Ele le a Master File
  Table do NTFS direto do volume, o que indexa um milhao de arquivos em segundos em vez de
  minutos, e cai para a API do Windows nos volumes onde isso nao e possivel.

  Imagens e videos aparecem como miniaturas em seis tamanhos, para voce saber qual de cinco
  renderizacoes grandes e a final sem abrir nenhuma. A exclusao vai para a Lixeira por
  padrao, e definitiva so atras de uma confirmacao que voce precisa marcar, com uma lista
  de protecao que recusa a raiz do volume, Windows, System32 e arquivos do kernel.

  Ele tambem acha arquivos com conteudo identico, desenha o volume como um treemap onde
  cada caixa tem o tamanho do que ocupa, e guarda arquivos numa quarentena de onde da para
  restaurar, em vez de apagar de uma vez.

  Toda varredura confere o espaco atribuido a arquivos contra o que o volume declara em uso,
  e avisa quando os dois nao batem.

  Interface em ingles e portugues. O caminho rapido exige Administrador; sem ele o app
  funciona pela travessia mais lenta e diz qual dos dois usou.
ManifestType: locale
ManifestVersion: 1.6.0
"@ | Set-Content -Path (Join-Path $dir "$id.locale.pt-BR.yaml") -Encoding utf8

Get-ChildItem $dir | ForEach-Object { "  gerado  $($_.Name)" }

if (-not $SkipValidate) {
    Write-Host ""
    winget validate --manifest $dir
}
