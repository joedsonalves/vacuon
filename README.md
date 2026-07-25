<div align="center">

<img src="assets/vacuon-logo.svg" width="112" alt="Vacuon">

# Vacuon

**Analisador e liberador de espaço em disco para Windows.**
Lê a MFT do NTFS direto do volume, mostra o conteúdo real do que você vai apagar
e nunca afirma um número que não mediu.

[![Build](https://github.com/joedsonalves/vacuon/actions/workflows/ci.yml/badge.svg)](https://github.com/joedsonalves/vacuon/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/licença-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/plataforma-Windows%2010%2F11-0078D4.svg)](#requisitos)
[![Testes](https://img.shields.io/badge/testes-87-3FB950.svg)](tests)

<img src="docs/img/02-explorer-escuro.png" width="900" alt="Explorer do Vacuon com a árvore de pastas e a lista de arquivos">

</div>

---

## O que é

Três perguntas, respondidas em segundos:

| | |
|---|---|
| **Para onde foi meu espaço?** | Mapa completo: maiores arquivos, maiores pastas, distribuição por tipo, tamanho e idade. |
| **O que é seguro apagar?** | Filtros compostos, arquivos esquecidos, e o catálogo de regras de limpeza (M5). |
| **Isso aqui é o quê?** | Miniatura do **conteúdo real** — o frame do vídeo, a própria foto — em seis tamanhos. |

E uma quarta, que quase nenhum utilitário de disco responde: **tem alguma coisa estranha se alojando na minha máquina?**

> **v0.2.0 — marcos M1 e M2.** Somente leitura: o Vacuon mede, explica e mostra. Ele **ainda não apaga nada** — exclusão e quarentena reversível chegam no M4, porque um app que apaga arquivos tem exatamente uma chance de errar. O roteiro completo está no [PRD.md](PRD.md).

---

## As telas

### Painel — onde está o espaço

<img src="docs/img/01-painel-escuro.png" width="900" alt="Painel com cartão de volume e agregados por tipo, tamanho e idade">

Cartão por volume com a barra de ocupação (vermelha acima de 90%), e três agregados que respondem perguntas diferentes:

- **por tipo** — nesta máquina, 9 arquivos `.vhdx` somam 106 GiB. Discos virtuais de emulador e de WSL são o buraco negro mais comum e o mais invisível;
- **por tamanho** — 4 arquivos acima de 8 GB ocupam 112 GiB, enquanto 1,85 milhão de arquivos abaixo de 4 KB somam 1,9 GiB. Responde de uma vez se o problema é "poucos gigantes" ou "muitos pequenos";
- **por idade** — 177 GiB não são tocados há mais de 90 dias.

### Explorer — a tela de trabalho

<img src="docs/img/02-explorer-claro.png" width="900" alt="Explorer no tema claro">

Árvore de pastas **ordenada por tamanho, não por nome** — quem abre a árvore quer achar o culpado. A barra âmbar sob cada pasta é a fatia do disco que aquela subárvore ocupa. Subpastas carregam sob demanda: a hierarquia inteira nunca é materializada.

Busca instantânea sobre o índice em memória, mais filtros de tamanho mínimo, idade e extensão. Botões para **maiores arquivos**, **maiores pastas** e **suspeitos**.

### Miniaturas — ver antes de apagar

<img src="docs/img/03-miniaturas-escuro.png" width="900" alt="Lista com miniaturas de 256 px mostrando frames de vídeo e imagens">

A razão de existir desta feature: decidir qual dos cinco renders de 9 GB é o final, **sem abrir nenhum deles**.

Imagem e vídeo mostram o conteúdo; todo outro tipo mostra o ícone registrado dele. Seis tamanhos — 16, 32, 64, 128, 256 e 512 px — alternáveis na barra do Explorer ou nas Configurações.

O rótulo "veio do conteúdo" é **fato verificado**, não palpite: o Vacuon pede `SIIGBF_THUMBNAILONLY` primeiro e só cai para `SIIGBF_ICONONLY` se o Shell não tiver miniatura. Sem essa separação, um `.md` sem handler de preview seria anunciado como se a miniatura fosse do arquivo.

> Os arquivos deste print são sintéticos (`smptebars` e `testsrc` do ffmpeg, mais gradientes gerados), justamente para não publicar conteúdo de ninguém.

### Segurança — pontos de persistência no registro

<img src="docs/img/04-seguranca-escuro.png" width="900" alt="Tela de Segurança com o resultado da inspeção">

44 chaves onde malware costuma se alojar — `Run`, `RunOnce`, `RunOnceEx`, `Winlogon` (Shell, Userinit, Taskman, Notify), `AppInit_DLLs`, `AppCertDlls`, `BootExecute`, `Image File Execution Options\Debugger`, `SilentProcessExit`, pacotes do `Lsa`, `Command Processor\AutoRun`, `UserInitMprLogonScript`, Active Setup, BHOs, `SharedTaskScheduler`, sequestro de associação de arquivo, pastas de Inicialização e Tarefas Agendadas.

**Somente leitura.** Nenhuma chave é alterada, desabilitada ou removida. E o Vacuon **não é antivírus**: não existe base de assinaturas aqui, e sim heurística de comportamento com o motivo sempre à vista.

O print acima é o resultado numa máquina limpa: **44 locais, 122 entradas, 51 ms, um único achado** — e ele é verdadeiro ("Tarefas Agendadas exigem Administrador para serem lidas"). Chegar a esse número deu trabalho; veja [falso positivo é bug](#falso-positivo-é-bug).

### Suspeitos — arquivos disfarçados

<img src="docs/img/06-suspeitos-claro.png" width="900" alt="Lista de arquivos marcados pelas heurísticas">

Extensão dupla (`fatura.pdf.cmd`), caractere Unicode RLO invertendo a extensão visível, executável oculto, executável com Alternate Data Stream grande, extensões de phishing, executável recém-criado em System32.

Os dois itens do print são chamarizes sintéticos criados para demonstrar a detecção. Antes da calibração, esta mesma lista trazia **45 itens, 43 deles falsos positivos**.

### Configurações — tema e privilégio

<img src="docs/img/05-config-claro.png" width="900" alt="Configurações com tema, privilégio e miniaturas">

**Tema claro, escuro ou acompanhando o sistema.** A troca é imediata, sem reiniciar, e no modo "acompanhar" o app reage quando você muda o tema do Windows com ele aberto. A barra de título acompanha (ela é desenhada pelo Windows, não pelo WPF — sem tratar isso, o tema escuro fica com uma faixa branca no topo).

**Sempre abrir como administrador.** Ligue e o Vacuon se relança elevado a cada abertura. O UAC aparece — e o app diz isso na cara, em vez de fingir que dá para suprimir. Vale a pena porque **só com elevação existe a leitura da MFT**: é a diferença entre segundos e minutos.

---

## Por que existe

As ferramentas atuais escolhem um lado: ou medem rápido e não limpam (WizTree), ou limpam e não medem (CCleaner). Nenhuma deixa você **ver o conteúdo** antes de decidir.

E nenhuma é honesta com números. O Vacuon é:

- **hardlink conta uma vez** — senão `WinSxS` "ocuparia" o triplo do real;
- **junction nunca é atravessada** — `C:\Documents and Settings` → `C:\Users` é um ciclo infinito;
- **tamanho lógico ≠ tamanho em disco** — os dois aparecem, com rótulo;
- **placeholder do OneDrive é intocável** — ler *baixa* o arquivo (enche o disco em vez de liberar) e apagar remove **da nuvem**;
- **o que não foi medido é declarado como não medido.** Este é o ponto: na travessia pela API do Windows não existe `AllocatedSize`, então o Vacuon escreve *"tamanho em disco não medido"* em vez de repetir o tamanho lógico e imprimir "desperdício: 0 B". Repare nos prints — é exatamente o que a barra lateral mostra.

## Velocidade

O ganho não vem de "mais threads". Vem de **não usar a API do Windows**:

| Estratégia | 1 M de arquivos | Requisitos |
|---|---|---|
| **Leitura bruta da MFT** | **3–8 s** | NTFS + Administrador |
| USN + tamanhos sob demanda | 15–40 s | NTFS + Administrador |
| `FindFirstFileEx` paralelo | 60–200 s | qualquer filesystem |
| Atualização incremental (USN) | **< 1 s** | snapshot anterior |

A escolha é automática e cai em cascata: sem elevação ou fora do NTFS, o Vacuon usa a travessia por API e **diz que caiu, e por quê** — está escrito no cabeçalho de todos os prints acima.

**Medido nesta máquina** (2,86 M de arquivos, 459 GiB, SSD SATA): travessia pela API em **34 s** com cache do sistema quente, **4 min 33 s** a frio, com a interface respondendo durante toda a varredura. A leitura da MFT precisa de um processo elevado.

## Falso positivo é bug

No módulo de segurança, uma lista que alarma sempre é uma lista que o usuário aprende a ignorar. Por isso um falso positivo aqui é tratado como **defeito**, não como ruído aceitável — e cada correção virou teste positivo + negativo.

Rodando contra uma máquina real, a primeira versão cuspiu 21 achados no registro e 45 arquivos suspeitos. Hoje são **1 e 2**. O que estava errado:

| Sinal ingênuo | Por que estava errado |
|---|---|
| "binário sem assinatura digital" | Binários do Windows são assinados por **catálogo** (`.cat`), não com assinatura embutida no PE. Cobrar assinatura embutida marcava `rundll32.exe`, `unregmp2.exe` e `ie4uinit.exe` |
| "arquivo apontado não existe" | `msv1_0`, `scecli`, `{CLSID}` e `IEToEdge BHO` são **nomes**, não caminhos |
| "usa rundll32 (LOLBin)" | O Active Setup do próprio Windows chama `rundll32` o tempo todo. Só conta fora do diretório do sistema |
| "executável em pasta volátil: AppData\Local" | Chrome, Discord, Opera e Roblox **instalam ali por padrão**. Eram 4 alarmes falsos em qualquer máquina |
| "autorun órfão: `/UserInstall`" | Um switch de linha de comando não é caminho. Normalizar `/` para `\` inventava um arquivo |
| "extensão dupla: `Iterator.zip.js`" | É um arquivo de teste do pacote npm `es-iterator-helpers`. Árvores de dependência ficaram de fora |
| "extensão dupla: `relatorio.pdf.lnk`" | É **exatamente como o Windows nomeia um atalho** para `relatorio.pdf`. A pasta Recentes é cheia deles |
| "extensão de phishing: `Bubbles.scr`" | É o protetor de tela que vem com o Windows |

Dois sinais atravessam todas essas exclusões, porque não têm explicação inocente em lugar nenhum: o caractere **RLO** no nome e um **executável recém-criado em System32**.

Se o Vacuon marcar algo legítimo na sua máquina, [abra uma issue](../../issues/new?template=falso-positivo.yml) — o template existe só para isso.

## Instalação

Baixe o `.exe` da [página de releases](../../releases) — é portátil, roda de um pendrive, não instala nada.

Ou compile:

```bash
git clone https://github.com/joedsonalves/vacuon.git
cd vacuon
dotnet build -c Release
dotnet test
```

### Requisitos

- Windows 10 21H2+ ou Windows 11 (x64 / ARM64)
- [.NET 10 Runtime](https://dotnet.microsoft.com/download)
- **Administrador** apenas para a leitura da MFT. O app abre sem UAC e só pede elevação quando você liga a opção nas Configurações.

## Interface gráfica e linha de comando

O mesmo núcleo atende as duas. `Vacuon.exe` abre a interface; `vacuon.exe` é a CLI:

```bash
vacuon volumes                     # o que existe e quanto está cheio
vacuon scan C:                     # mapa completo do volume
vacuon scan "D:\Projetos" --top=50 # escopo de pasta
vacuon scan C: --suspicious        # inclui a caça a arquivos disfarçados
vacuon security                    # chaves de persistência do registro
vacuon thumb video.mkv --size=256  # extrai a miniatura do conteúdo
vacuon reveal "C:\caminho\arq.mp4" # abre o Explorer com o arquivo selecionado
```

<details>
<summary><b>Exemplo de saída — <code>vacuon scan C:</code></b></summary>

```
VARREDURA — C:
──────────────
  Estratégia        travessia pela API do Windows
                    (caiu para o fallback: leitura da MFT exige executar como Administrador)
  Tempo             4 min 33 s
  Arquivos          2.861.572
  Pastas            604.583
  Velocidade        10.477 arquivos/s

  Tamanho lógico    459 GiB
  Tamanho em disco  não medido (só a leitura da MFT expõe AllocatedSize)
  Desperdício       não medido pelo mesmo motivo

MAIORES ARQUIVOS (top 5)
────────────────────────
      67,1 GiB  C:\ProgramData\BlueStacks_nxt\Engine\Pie64\Data.vhdx
      23,7 GiB  C:\...\AppData\Local\Docker\wsl\disk\docker_data.vhdx
      12,7 GiB  C:\hiberfil.sys
       8,6 GiB  C:\...\vm_bundles\claudevm.bundle\rootfs.vhdx
       6,9 GiB  C:\...\.ollama\models\blobs\sha256-1...

DISTRIBUIÇÃO POR TAMANHO
────────────────────────
  1 B – 4 KB          1.856.643 arq.      1,9 GiB
  128 MB – 1 GB             367 arq.     91,6 GiB
  1 GB – 8 GB                17 arq.     48,2 GiB
  acima de 8 GB               4 arq.      112 GiB
```

</details>

Códigos de saída: `0` sucesso · `1` sucesso parcial · `2` erro de argumento · `3` precisa de elevação · `4` volume inacessível · `5` cancelado.

## Segurança e privacidade

- **Nenhum byte sai da máquina.** Sem servidor, sem conta, sem telemetria, sem checagem automática de atualização. As preferências ficam em `%AppData%\Vacuon\settings.json`.
- **O volume é aberto somente para leitura.** `GENERIC_READ`, nunca `GENERIC_WRITE`.
- **O scanner de registro não escreve.** Todas as chaves são abertas com `writable: false`.
- **Nada é executado.** Autoruns suspeitos são exibidos como texto, jamais invocados.
- **Nunca haverá "limpeza de registro"**: ganho de espaço nulo, risco alto. Está nos [não-objetivos](PRD.md#32-não-objetivos-explicitamente-fora-do-escopo), junto com tweaks de sistema e "PC Health Score".

Detalhes em [SECURITY.md](SECURITY.md).

## Marcos

| Marco | O que entrega | Estado |
|---|---|:-:|
| M0 | Solução, núcleo sem UI, testes | ✅ |
| M1 | Leitura bruta da MFT, índice, travessia de fallback, CLI | ✅ |
| M1b | Scanner de persistência no registro + arquivos suspeitos | ✅ |
| M1c | Miniaturas do Shell em seis tamanhos | ✅ |
| **M2** | **GUI: painel, explorer virtualizado, busca, temas claro/escuro, elevação** | ✅ |
| M1d | Snapshot binário + atualização incremental por USN | ⬜ |
| M3 | Player embutido (LibVLCSharp) e preview de mídia | ⬜ |
| M4 | Quarentena reversível, histórico, desfazer | ⬜ |
| M5 | Catálogo de 120+ regras de limpeza | ⬜ |
| M6 | Duplicados exatos e quase-duplicados | ⬜ |
| M7 | Treemap | ⬜ |

## Arquitetura

```
src/
├─ Vacuon.Native/   P/Invoke Win32 + parser on-disk do NTFS
│  ├─ Interop/      VolumeDevice · Shell32 · Gdi32 · Kernel32
│  └─ Ntfs/         MftRecordParser · DataRunList · MftStream · NtfsLayout
├─ Vacuon.Core/     núcleo SEM UI — CLI, testes e GUI consomem isto
│  ├─ Index/        FileEntry (64 bytes) · NameBlob · VolumeIndex
│  ├─ Scan/         ScanOrchestrator · MftScanner · Win32Walker · VolumeProbe
│  ├─ Analyzers/    SizeAnalyzer · FileCategories
│  ├─ Security/     RegistryPersistenceScanner · SuspiciousFileAnalyzer
│  └─ Preview/      ThumbnailProvider · BmpWriter
├─ Vacuon.App/      WPF — MVVM escrito à mão, sem dependência externa
│  ├─ Themes/       Dark.xaml · Light.xaml · Controls.xaml
│  ├─ ViewModels/   MainViewModel · FileRowViewModel · FolderNodeViewModel
│  └─ Views/        Dashboard · Explorer · Security · Settings
└─ Vacuon.Cli/      subcomandos scan/volumes/security/thumb/reveal
```

O índice são **arrays planos de `struct`**, não um grafo de objetos: 1 milhão de arquivos = **64 MB previsíveis**, sem um objeto no heap por arquivo. Um grafo de `class FileNode` com `Parent`/`Children` custaria ~400 MB e manteria a Gen2 sofrendo durante toda a varredura. O teste `FileEntry_IsExactlySixtyFourBytes` existe para que ninguém encoste nesse contrato sem perceber — foi ele que empurrou os bytes de Alternate Data Stream para uma tabela lateral, já que ADS é raro e um campo em toda entrada guardaria zeros.

A hierarquia usa um índice de filhos em formato **CSR** (dois `int[]`, como matriz esparsa): ~23 MB para 2,8 milhões de entradas, contra centenas de MB de um `Dictionary<int, List<int>>`.

## Armadilhas que este código já resolve

Se você for escrever um leitor de MFT ou temas em WPF, estas custam caro:

1. **A MFT é fragmentada.** Lê-la como bloco contíguo funciona em disco novo e perde arquivos **silenciosamente** em disco usado. É obrigatório decodificar os data runs do registro 0.
2. **Fixups do Update Sequence Array.** Sem aplicá-los, os dois últimos bytes de cada setor vêm errados — o parser "quase funciona", que é pior que falhar.
3. **`FSCTL_ENUM_USN_DATA` não traz tamanho.** Quem monta o pipeline em cima disso refaz tudo depois.
4. **Nomes 8.3 duplicam entradas.** Um arquivo com nome longo tem dois `$FILE_NAME`; contar os dois dobra a contagem do volume.
5. **`MAX_PATH`.** Acima de 260 caracteres exige `\\?\` em toda chamada Win32 — e é exatamente em `node_modules` profundo que isso dói.
6. **`LibraryImport` não marshala interface COM** (SYSLIB1052) e **não acrescenta o sufixo W**: `GetObject` precisa ser `GetObjectW`.
7. **`ProgressBar.Value` liga TwoWay por padrão** e explode em propriedade somente-leitura.
8. **Um `Style` com `TargetType="CheckBox"` aplicado a um `RadioButton`** derruba a janela na carga do XAML.
9. **O template padrão do `ComboBox` ignora `Background`** — no tema escuro ele aparece branco. Precisa de template próprio, e o mesmo vale para `ProgressBar` (o brilho animado sobre fundo escuro vira uma barra esbranquiçada).
10. **A barra de título é do Windows.** Sem `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`, o tema escuro fica com uma faixa branca no topo.
11. **`GridViewRowPresenter` exige um `GridView`.** Reusar o estilo de linha de um `ListView` com colunas em outro sem `View` faz o item simplesmente não aparecer.

A lista completa está no [PRD §17](PRD.md#172-armadilhas-técnicas-aprender-aqui-não-em-produção).

## Contribuindo

Leia o [CONTRIBUTING.md](CONTRIBUTING.md). Em resumo: `Vacuon.Core` não referencia UI, `Safety/` e `Actions/` exigem 100% de cobertura, e **nenhuma mudança pode fazer o app afirmar um número que ele não mediu**.

## Licença

[MIT](LICENSE).

---

<div align="center">
<sub>O nome vem do vácuo — o espaço que volta a ser seu.</sub>
</div>
