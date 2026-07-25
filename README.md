<div align="center">

<img src="assets/vacuon-logo.svg" width="120" alt="Vacuon">

# Vacuon

**Analisador e liberador de espaço em disco para Windows.**
Lê a MFT do NTFS direto do volume — 1 milhão de arquivos em segundos, não em minutos.

[![Build](https://github.com/joedson/vacuon/actions/workflows/ci.yml/badge.svg)](https://github.com/joedson/vacuon/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/plataforma-Windows%2010%2F11-0078D4.svg)](#requisitos)

</div>

---

## O que é

Três perguntas, respondidas em segundos:

| | |
|---|---|
| **Para onde foi meu espaço?** | Mapa completo do disco: maiores arquivos, maiores pastas, distribuição por tipo, tamanho e idade. |
| **O que é seguro apagar?** | Lixo de sistema, cache de aplicativos, duplicados exatos e quase-duplicados, artefatos de build, downloads esquecidos. |
| **Isso aqui é o quê?** | Miniatura do **conteúdo real** — frame do vídeo, a própria foto — em 6 tamanhos, para você ver o que vai apagar sem abrir o arquivo. |

E mais uma, que quase nenhum utilitário de disco responde: **tem alguma coisa estranha se alojando na minha máquina?** O Vacuon inspeciona os 44 pontos do registro que malware usa para persistir e as heurísticas de arquivo disfarçado — sempre em modo somente-leitura.

## Por que existe

As ferramentas atuais escolhem um lado: ou medem rápido e não limpam (WizTree), ou limpam e não medem (CCleaner). Nenhuma deixa você **dar play no vídeo** antes de decidir qual dos cinco renders de 9 GB é o final.

E nenhuma delas é honesta com números. O Vacuon é:

- **hardlink conta uma vez** — senão `WinSxS` "ocuparia" o triplo do real;
- **junction nunca é atravessada** — `C:\Documents and Settings` → `C:\Users` é um ciclo infinito;
- **tamanho lógico ≠ tamanho em disco** — os dois aparecem, com rótulo;
- **placeholder do OneDrive é intocável** — ler *baixa* o arquivo (enche o disco em vez de liberar) e apagar remove **da nuvem**;
- **o que não foi medido é declarado como não medido** — na travessia por API não existe `AllocatedSize`, então o Vacuon diz isso em vez de repetir o tamanho lógico e inventar "desperdício: 0 B".

## Estado atual

> **v0.1.0 — marco M1 (motor de varredura).** O núcleo e a CLI funcionam. A interface gráfica (WPF) vem nos marcos M2–M3; o roteiro completo está no [PRD.md](PRD.md).

| Marco | O que entrega | Estado |
|---|---|:-:|
| M0 | Solução, núcleo sem UI, testes | ✅ |
| M1 | Leitura bruta da MFT, índice, travessia de fallback, CLI | ✅ |
| — | Scanner de persistência no registro + arquivos suspeitos | ✅ |
| — | Miniaturas do Shell em 6 tamanhos | ✅ |
| M2 | GUI: painel, explorer virtualizado, busca | ⬜ |
| M3 | Player embutido (LibVLCSharp), preview de mídia | ⬜ |
| M4 | Quarentena reversível, histórico, desfazer | ⬜ |
| M5 | Catálogo de 120+ regras de limpeza | ⬜ |
| M6 | Duplicados e quase-duplicados | ⬜ |
| M7 | Treemap | ⬜ |

## Velocidade

O ganho não vem de "mais threads". Vem de **não usar a API do Windows**:

| Estratégia | 1 M arquivos | Requisitos |
|---|---|---|
| **Leitura bruta da MFT** | **3–8 s** | NTFS + Administrador |
| USN + tamanhos sob demanda | 15–40 s | NTFS + Administrador |
| `FindFirstFileEx` paralelo | 60–200 s | qualquer filesystem |
| Atualização incremental (USN) | **< 1 s** | snapshot anterior |

A escolha é automática, com queda em cascata: sem elevação ou fora do NTFS, o Vacuon cai para a travessia por API e **diz que caiu, e por quê**.

## Instalação

Baixe o `.exe` da [página de releases](../../releases) — é portátil, roda de um pendrive, não instala nada.

Ou compile:

```bash
git clone https://github.com/joedson/vacuon.git
cd vacuon
dotnet build -c Release
dotnet test
```

### Requisitos

- Windows 10 21H2+ ou Windows 11 (x64 / ARM64)
- [.NET 10 Runtime](https://dotnet.microsoft.com/download) — ou use a build self-contained
- **Administrador** apenas para a leitura da MFT. O app abre sem UAC e só pede elevação quando você escolhe a varredura rápida.

## Uso

```bash
vacuon volumes                     # o que existe e quanto está cheio
vacuon scan C:                     # mapa completo do volume
vacuon scan "D:\Projetos" --top=50 # escopo de pasta
vacuon scan C: --suspicious        # inclui a caça a arquivos disfarçados
vacuon security                    # chaves de persistência do registro
vacuon thumb video.mkv --size=256  # miniatura do conteúdo
vacuon reveal "C:\caminho\arq.mp4" # abre o Explorer com o arquivo selecionado
```

<details>
<summary><b>Exemplo de saída — <code>vacuon scan</code></b></summary>

```
VARREDURA — C:\JOEDSON\CANAIS YT\MEU PROJETOS - PROGRAMAS
─────────────────────────────────────────────────────────
  Estratégia        travessia pela API do Windows
  Tempo             640 ms
  Arquivos          33.779
  Pastas            6.150
  Velocidade        52.804 arquivos/s

  Tamanho lógico    4,4 GiB
  Tamanho em disco  não medido (só a leitura da MFT expõe AllocatedSize)
  Desperdício       não medido pelo mesmo motivo
  Volume            464 GiB usados de 476 GiB

MAIORES ARQUIVOS (top 8)
────────────────────────
       180 MiB  ...\output\Aruku-Yugret_REVIVED_as_DEMON.mp4
       165 MiB  ...\DEPRECATED-project\render_tmp\video_track.mp4
      83,6 MiB  ...\imageio_ffmpeg\binaries\ffmpeg-win-x86_64-v7.1.exe
      83,6 MiB  ...\imageio_ffmpeg\binaries\ffmpeg-win-x86_64-v7.1.exe   ← 4 cópias idênticas
```

</details>

<details>
<summary><b>Exemplo de saída — <code>vacuon security</code></b></summary>

```
CHAVES DE PERSISTÊNCIA DO REGISTRO
──────────────────────────────────
  Locais inspecionados   44
  Entradas lidas         122
  Tempo                  65 ms

  [ATENÇÃO] C:\WINDOWS\system32\Tasks
      (acesso negado) =
      → Tarefas agendadas exigem Administrador para serem lidas

  Somente leitura: o Vacuon não alterou nenhuma chave.
  Entradas legítimas aparecem aqui o tempo todo — leia o motivo antes de concluir.
```

</details>

Códigos de saída: `0` sucesso · `1` sucesso parcial · `2` erro de argumento · `3` precisa de elevação · `4` volume inacessível · `5` cancelado.

## Segurança e privacidade

- **Nenhum byte sai da máquina.** Sem servidor, sem conta, sem telemetria. O app é 100 % offline.
- **O scanner de registro é somente-leitura.** Ele lista, explica e para por aí.
- **Não é um antivírus.** Não há base de assinaturas — o que existe é heurística de comportamento com o motivo sempre visível. Entradas legítimas aparecem na lista; leia o "porquê" antes de concluir qualquer coisa.
- **Falso positivo é bug.** Uma lista que alarma sempre é uma lista que se aprende a ignorar. Se o Vacuon marcar algo inofensivo, [abra uma issue](../../issues) — é tratado como defeito, não como ruído aceitável.
- **Reversível por padrão** (a partir do M4): a ação padrão move para uma quarentena com manifesto e restauração de 1 clique. Apagar de verdade é um segundo passo explícito.
- **Nunca haverá "limpeza de registro"**: ganho de espaço nulo, risco alto. Está nos não-objetivos do [PRD](PRD.md#32-não-objetivos-explicitamente-fora-do-escopo).

## Arquitetura

```
src/
├─ Vacuon.Native/   P/Invoke Win32 + parser on-disk do NTFS
│  ├─ Interop/      VolumeDevice · Shell32 · Gdi32 · Kernel32
│  └─ Ntfs/         MftRecordParser · DataRunList · MftStream · NtfsLayout
├─ Vacuon.Core/     núcleo sem UI — a CLI, os testes e a futura GUI consomem isto
│  ├─ Index/        FileEntry (64 bytes) · NameBlob · VolumeIndex
│  ├─ Scan/         ScanOrchestrator · MftScanner · Win32Walker · VolumeProbe
│  ├─ Analyzers/    SizeAnalyzer · FileCategories
│  ├─ Security/     RegistryPersistenceScanner · SuspiciousFileAnalyzer
│  └─ Preview/      ThumbnailProvider · BmpWriter
└─ Vacuon.Cli/      subcomandos scan/volumes/security/thumb/reveal
```

O índice são **arrays planos de `struct`**, não um grafo de objetos: 1 milhão de arquivos = **64 MB previsíveis**, sem um único objeto no heap por arquivo. Um grafo de `class FileNode` com `Parent`/`Children` custaria ~400 MB e manteria a Gen2 sofrendo durante toda a varredura. O teste `FileEntry_IsExactlySixtyFourBytes` existe para que ninguém encoste nesse contrato sem perceber.

## Armadilhas que este código já resolve

Se você for escrever um leitor de MFT, estas são as que custam caro:

1. **A MFT é fragmentada.** Lê-la como bloco contíguo funciona em disco novo e perde arquivos **silenciosamente** em disco usado. É obrigatório decodificar os data runs do registro 0.
2. **Fixups do Update Sequence Array.** Sem aplicá-los, os dois últimos bytes de cada setor vêm errados — o parser "quase funciona", que é pior que falhar.
3. **`FSCTL_ENUM_USN_DATA` não traz tamanho.** Quem monta o pipeline em cima disso refaz tudo depois.
4. **Nomes 8.3 duplicam entradas.** Um arquivo com nome longo tem dois `$FILE_NAME`; contar os dois dobra a contagem do volume.
5. **Binários do Windows são assinados por catálogo,** não com assinatura embutida no PE. Cobrar assinatura embutida de `rundll32.exe` marca metade do System32 como suspeito.
6. **`AppData\Local` é instalação legítima.** Chrome, Discord, Opera e Roblox moram lá. Tratar a pasta como suspeita gera meia dúzia de alarmes falsos em toda máquina.
7. **`MAX_PATH`.** Acima de 260 caracteres exige `\\?\` em toda chamada Win32 — e é exatamente em `node_modules` profundo que isso dói.

A lista completa (17 itens) está no [PRD §17](PRD.md#172-armadilhas-técnicas-aprender-aqui-não-em-produção).

## Contribuindo

Leia o [CONTRIBUTING.md](CONTRIBUTING.md). Em resumo: `Vacuon.Core` não referencia UI, `Security/` e `Actions/` exigem 100 % de cobertura, e nenhuma mudança pode fazer o app afirmar um número que ele não mediu.

## Licença

[MIT](LICENSE).

---

<div align="center">
<sub>O nome vem do vácuo — o espaço que volta a ser seu.</sub>
</div>
