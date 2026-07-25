# PRD — VACUON

**Analisador e liberador de espaço em disco para Windows**

| Campo | Valor |
|---|---|
| Codinome | **VACUON** |
| Versão do documento | 1.1 |
| Data | 2026-07-24 |
| Plataforma | Windows 10 21H2+ / Windows 11 (x64, ARM64) |
| Stack | C# / .NET 10 · WPF · P/Invoke Win32 · LibVLCSharp |
| Autor | Joedson |
| Status | M0 e M1 implementados · M2 em aberto |

---

## Sumário

1. [Resumo executivo](#1-resumo-executivo)
2. [Problema](#2-problema)
3. [Objetivos e não-objetivos](#3-objetivos-e-não-objetivos)
4. [Personas e casos de uso](#4-personas-e-casos-de-uso)
5. [Princípios de produto](#5-princípios-de-produto)
6. [Arquitetura](#6-arquitetura)
7. [Motor de varredura](#7-motor-de-varredura-o-coração-do-app)
8. [Requisitos funcionais](#8-requisitos-funcionais)
9. [Segurança e modelo de risco](#9-segurança-e-modelo-de-risco)
10. [UI/UX](#10-uiux)
11. [Performance — metas e engenharia](#11-performance--metas-e-engenharia)
12. [Configuração](#12-configuração)
13. [Modelo de dados](#13-modelo-de-dados)
14. [CLI e automação](#14-cli-e-automação)
15. [Telemetria local, logs e relatórios](#15-telemetria-local-logs-e-relatórios)
16. [Roadmap](#16-roadmap)
17. [Riscos e armadilhas conhecidas](#17-riscos-e-armadilhas-conhecidas)
18. [Métricas de sucesso](#18-métricas-de-sucesso)
19. [Anexo A — Catálogo de regras de limpeza](#anexo-a--catálogo-de-regras-de-limpeza)
20. [Anexo B — Estrutura do projeto](#anexo-b--estrutura-do-projeto)
21. [Anexo C — Glossário](#anexo-c--glossário)

---

## 1. Resumo executivo

O Vacuon é um utilitário desktop que responde três perguntas em segundos, não em minutos:

1. **Para onde foi meu espaço?** — mapa completo do disco (treemap + árvore + top-N), lido direto da MFT do NTFS.
2. **O que é seguro apagar?** — catálogo de regras para lixo de sistema e cache de aplicativos, duplicados exatos e quase-duplicados (imagem/vídeo/áudio), arquivos órfãos, e "arquivos que você esqueceu que existem".
3. **Isso aqui é o quê?** — preview instantâneo de qualquer item: **player de vídeo/áudio embutido**, visualizador de imagem, hex/texto, thumbnail nativa do Windows — com **1 clique para abrir a pasta de origem** com o arquivo já selecionado.

O que separa o Vacuon de um "limpador de registro" genérico:

- **Velocidade real**: varredura de um volume NTFS com 1,2 milhão de arquivos em **3–8 s** (leitura bruta da MFT), contra 90–180 s de `FindFirstFile` e 240–400 s de scripts em Python.
- **Nada é apagado de forma irreversível por padrão.** Toda exclusão passa por uma **quarentena reversível** com manifesto e restauração de 1 clique. A exclusão permanente é uma escolha consciente, não o caminho padrão.
- **Paralelismo e memória configuráveis** — o usuário decide quanto do PC entregar para a varredura (de "modo silencioso, 2 threads" a "modo bulldozer, todos os núcleos + índice inteiro em RAM").
- **Honestidade de números**: distingue *tamanho lógico* de *tamanho em disco*, não conta hardlink duas vezes, não segue junction e mostra o espaço real que cada ação vai recuperar — antes de executá-la.

---

## 2. Problema

### 2.1 Dores concretas

| Dor | Situação real |
|---|---|
| Disco cheio sem explicação | O Explorer diz "5 GB livres de 500 GB" e não há como saber o culpado sem clicar pasta por pasta. |
| Ferramentas nativas são cegas | A "Limpeza de Disco" do Windows enxerga ~15 categorias e ignora 90% do lixo real (cache de navegador, shader cache, `node_modules`, sobras de instalador, projetos de vídeo antigos). |
| Varredura demorada | Ferramentas de terceiros varrem por API do Windows e levam minutos; o usuário desiste no meio. |
| Medo de apagar | Sem preview e sem desfazer, o usuário não apaga nada — ou apaga algo importante. |
| Arquivos de mídia grandes | Um projeto de vídeo de 40 GB em cinco versões de render; sem *dar play* não há como saber qual é o final. |
| Duplicados invisíveis | A mesma foto em `Downloads`, `Desktop`, `OneDrive` e `Backup` — quatro cópias, nomes diferentes. |
| Lixo pequeno em massa | 380 mil arquivos de 2 KB em caches: pouco tamanho lógico, muito *slack space* e lentidão de backup/antivírus. |

### 2.2 Análise da concorrência

| Ferramenta | Ponto forte | Onde falha |
|---|---|---|
| WizTree | Varredura por MFT, muito rápida | Só mede; não limpa, não faz preview, não trata duplicados de verdade |
| WinDirStat | Treemap clássico, gratuito | Varredura lenta (API), UI datada, sem regras de limpeza |
| TreeSize Free | Boa árvore | Recursos de valor estão pagos, sem player |
| BleachBit / CCleaner | Catálogo de regras de limpeza | Não visualiza o disco, sem duplicados sérios, sem preview, histórico de sustos |
| dupeGuru | Duplicados razoáveis | Lento, UI travada, não integra com análise de espaço |

**Lacuna que o Vacuon ocupa:** ninguém entrega *velocidade de MFT* + *catálogo de limpeza* + *duplicados/near-duplicates* + *preview com player* + *desfazer confiável* em um só app.

---

## 3. Objetivos e não-objetivos

### 3.1 Objetivos (v1.0)

| ID | Objetivo | Critério de aceite |
|---|---|---|
| O1 | Varredura relâmpago | Volume NTFS de 1 M arquivos indexado em ≤ 10 s em SATA SSD; ≤ 25 s em HDD 5400 rpm |
| O2 | Visão completa do espaço | Treemap + árvore + top-N + agrupamento por tipo/idade/dono, todos sobre o mesmo índice |
| O3 | Limpeza segura e ampla | ≥ 120 regras de limpeza catalogadas, cada uma com classificação de risco e estimativa de ganho |
| O4 | Detecção de duplicados | 100 % de precisão em duplicados exatos; near-duplicates de imagem/vídeo/áudio com limiar ajustável |
| O5 | Preview universal | Play de vídeo/áudio embutido sem depender de codec pack; imagem, texto, hex, PDF |
| O6 | Reversibilidade | Toda operação destrutiva registrada e restaurável enquanto estiver na quarentena |
| O7 | Recursos configuráveis | Threads, memória máxima, prioridade de I/O e limite de CPU ajustáveis em tempo de execução |
| O8 | Zero instalação obrigatória | `.exe` único portátil que roda de um pendrive; instalador opcional |

### 3.2 Não-objetivos (explicitamente fora do escopo)

- ❌ **"Limpeza de registro"** — ganho de espaço nulo e risco alto. Não entra, nem como opção.
- ❌ **Otimizador de boot / tweaks de sistema / "acelerador de RAM"** — não é um pacote de snake oil.
- ❌ **Antivírus ou detecção de malware.**
- ❌ **Desinstalador de programas** (avaliado para v2; a v1 só *mostra* resíduos de programas já desinstalados).
- ❌ **Nuvem, conta de usuário, telemetria remota.** O app é 100 % offline; nenhum byte sai da máquina.
- ❌ **Linux/macOS** na v1 (o motor de MFT é intrinsecamente NTFS; um back-end alternativo é possível na v3).
- ❌ **Desfragmentação.**

---

## 4. Personas e casos de uso

### P1 — Joedson, criador de conteúdo (persona primária)

Trabalha com vídeo: renders de 4K, caches do CapCut/Premiere, downloads de episódios, `venv` de projetos Python, modelos de IA de 8 GB. Precisa saber **qual render é o final** antes de apagar os outros — daí o player embutido ser requisito, não enfeite.

> **UC-1** — "Meu SSD de 1 TB tem 12 GB livres e vou renderizar hoje." → varre, vê que `AppData\Local\Temp` tem 60 GB de sobras de render + 4 cópias do mesmo `.mp4` de 9 GB; dá play em cada uma, guarda a boa, manda 3 para a quarentena. **Ganho: 87 GB em 4 minutos.**

### P2 — Usuário doméstico

Notebook com SSD de 256 GB. Quer clicar em um botão, ver "você pode liberar 34 GB com segurança" e confirmar.

> **UC-2** — Modo *Limpeza Rápida*: só regras classificadas como SEGURO, um botão, relatório do que foi feito.

### P3 — Técnico / suporte de TI

Roda em máquina de cliente, quer diagnóstico rápido, relatório exportável e modo linha de comando para scriptar em várias máquinas.

> **UC-3** — `vacuon.exe scan C: --report=html --out=\\servidor\laudos\` sem abrir a GUI.

### P4 — Desenvolvedor

`node_modules`, `.venv`, `target/`, `__pycache__`, caches do npm/pip/cargo/gradle/NuGet, imagens Docker, disco virtual do WSL2 inflado.

> **UC-4** — Regra "artefatos de build com mais de 90 dias sem acesso, em projetos sem alteração recente" → lista 42 pastas, 210 GB.

---

## 5. Princípios de produto

Estes princípios resolvem empates de decisão em todo o resto do documento.

1. **Reversível por padrão, permanente por escolha.** O botão grande move para a quarentena. Apagar de verdade exige um segundo passo explícito.
2. **Nunca mentir sobre números.** Se um hardlink aparece em dois lugares, ele conta uma vez. Se um arquivo é comprimido pelo NTFS, mostramos os dois tamanhos. Se apagar não libera espaço (arquivo em Shadow Copy), avisamos.
3. **O usuário decide, o app informa.** Nenhuma exclusão automática silenciosa. Nenhuma "otimização" que o usuário não pediu.
4. **Velocidade é uma feature de UX.** Se o usuário espera, ele desiste. Nada bloqueia a UI. Resultados aparecem em streaming, não no final.
5. **Preview antes de julgar.** Todo item da lista pode ser inspecionado sem sair do app.
6. **Sem paywall escondido, sem "PC Health Score" alarmista, sem inflar números.** Se não há nada para limpar, o app diz que o disco está saudável.
7. **Explicável.** Cada item na lista de sugestões responde "por que isso está aqui?" em uma frase.

---

## 6. Arquitetura

### 6.1 Camadas

```
┌────────────────────────────────────────────────────────────────────┐
│  Vacuon.App (WPF, .NET 10)                                          │
│  MVVM · CommunityToolkit.Mvvm · virtualização · temas claro/escuro │
│  Telas: Dashboard · Explorer · Treemap · Limpeza · Duplicados ·    │
│         Mídia · Quarentena · Relatórios · Configurações            │
└──────────────────────────┬─────────────────────────────────────────┘
                           │ IAsyncEnumerable / Channels / progresso
┌──────────────────────────▼─────────────────────────────────────────┐
│  Vacuon.Core  (sem dependência de UI — testável e usável pela CLI) │
│  ├─ Scan/        ScanOrchestrator · MftReader · UsnEnumerator ·     │
│  │               Win32Walker · PathResolver · VolumeInfo           │
│  ├─ Index/       FileIndex (arrays planos) · NameBlob · Snapshot   │
│  ├─ Analyzers/   SizeTree · Duplicates · NearDuplicates ·          │
│  │               AgeHeatmap · EmptyItems · BrokenLinks ·           │
│  │               Orphans · SlackSpace · Extensions                 │
│  ├─ Rules/       RuleEngine · RuleCatalog (JSON) · RiskClassifier  │
│  ├─ Actions/     Quarantine · RecycleBin · HardDelete · Shred ·    │
│  │               Compress · Relocate · Dedupe(hardlink)            │
│  ├─ Preview/     ShellThumbnail · MediaProbe · TextPeek · HexPeek  │
│  ├─ Safety/      ProtectedPaths · LockedFileResolver · RestorePoint│
│  └─ Infra/       Config · Log · Metrics · Elevation · Pools        │
└──────────────────────────┬─────────────────────────────────────────┘
┌──────────────────────────▼─────────────────────────────────────────┐
│  Vacuon.Native (P/Invoke + helpers)                                │
│  DeviceIoControl · FSCTL_ENUM_USN_DATA · FSCTL_READ_USN_JOURNAL ·  │
│  SHGetFileInfo · IShellItemImageFactory · SHFileOperation ·        │
│  MoveFileEx · RestartManager · GetDiskFreeSpaceEx · DeviceIoControl│
│  (FSCTL_SET_COMPRESSION, FSCTL_GET_RETRIEVAL_POINTERS)            │
└────────────────────────────────────────────────────────────────────┘
     Vacuon.Cli  ─────────► usa Vacuon.Core diretamente (sem WPF)
```

### 6.2 Decisões arquiteturais (ADR resumido)

| # | Decisão | Por quê | Alternativa rejeitada |
|---|---|---|---|
| ADR-1 | Leitura bruta da MFT como caminho primário | Única forma de obter nome **e tamanho** de milhões de arquivos em segundos | `FSCTL_ENUM_USN_DATA` puro — dá nome e pai, **não dá tamanho**; buscar tamanho por handle anularia o ganho |
| ADR-2 | Núcleo sem WPF (`Vacuon.Core`) | CLI, testes e futura UI alternativa reusam tudo | Lógica no code-behind |
| ADR-3 | Índice em arrays planos de `struct`, não grafo de objetos | 1 M arquivos = ~64 MB previsíveis, sem pressão de GC | `List<FileNode>` com `Parent`/`Children` → ~400 MB e Gen2 sofrendo |
| ADR-4 | Snapshot binário próprio + SQLite só para histórico | Snapshot precisa carregar em <1 s; SQLite é ótimo para consulta de relatório, ruim para 1 M inserts | Tudo em SQLite |
| ADR-5 | LibVLCSharp para o player | Toca H.265/AV1/MKV/FLAC sem codec pack instalado; `MediaElement` depende do que a máquina tem | `MediaElement` (WMP) |
| ADR-6 | BLAKE3 para hash forte, XxHash128 para pré-filtro | BLAKE3 satura o SSD; XxHash128 é praticamente grátis no head/tail | SHA-256 (3–5× mais lento, sem ganho aqui) |
| ADR-7 | `asInvoker` no manifesto + relançamento elevado sob demanda | O app abre sem UAC; só pede elevação quando o usuário escolhe varredura por MFT | `requireAdministrator` (atrito no primeiro clique) |
| ADR-8 | Quarentena por `rename` no mesmo volume | Mover dentro do volume é instantâneo, independente do tamanho | Copiar para pasta de backup (dobra o I/O e o espaço) |

---

## 7. Motor de varredura (o coração do app)

### 7.1 Estratégias em cascata

O orquestrador escolhe a estratégia por volume, e cai para a seguinte se a anterior falhar:

| # | Estratégia | Requisitos | Velocidade (1 M arq.) | Observações |
|---|---|---|---|---|
| **S1** | **Leitura bruta da MFT** | NTFS + Administrador + handle no volume (`\\.\C:`) | **3–8 s** | Parseia registros `FILE` de 1024 B; extrai `$STANDARD_INFORMATION`, `$FILE_NAME`, `$DATA` |
| **S2** | **USN `ENUM_USN_DATA` + tamanhos sob demanda** | NTFS + Administrador | 15–40 s | Usado quando a MFT tem layout inesperado; tamanho vem de `GetFileInformationByHandleEx` só para arquivos acima de um limiar |
| **S3** | **`FindFirstFileEx` paralelo** | Qualquer FS (exFAT, FAT32, ReFS, rede) | 60–200 s | `FIND_FIRST_EX_LARGE_FETCH` + fila de trabalho por diretório |
| **S4** | **Atualização incremental por USN Journal** | NTFS + snapshot anterior válido | **< 1 s** | Lê só o delta desde o último `USN` conhecido — é o que permite "reabrir o app e já ver tudo" |

### 7.2 Detalhes do parser de MFT

- Abrir `\\.\C:` com `FILE_SHARE_READ | FILE_SHARE_WRITE`, ler o **boot sector** para `BytesPerSector`, `SectorsPerCluster`, `ClustersPerFileRecord`, `MftStartLcn`.
- Ler o registro 0 (`$MFT`) e extrair sua **data run list** — a MFT é fragmentada; ignorar isso é o bug clássico de "faltam arquivos no fim do disco".
- Ler em blocos grandes (padrão **8 MB**, alinhados a cluster, `FILE_FLAG_NO_BUFFERING` quando o alinhamento permitir) para maximizar throughput sequencial.
- Por registro `FILE`:
  - aplicar **fixups do update sequence array** antes de qualquer parse (sem isso, registros vêm corrompidos de forma silenciosa);
  - `$FILE_NAME` (0x30): preferir namespace **Win32** ou **Win32&DOS**; descartar o `DOS` puro (8.3) para não duplicar entradas;
  - `$DATA` (0x80) sem nome → tamanho do arquivo. **Residente**: tamanho do conteúdo do atributo. **Não residente**: `DataSize` (lógico) e `AllocatedSize` (em disco);
  - `$DATA` **com nome** → *Alternate Data Stream*: somar ao arquivo pai e marcar a flag `HasAds`;
  - `$ATTRIBUTE_LIST` (0x20) → atributos em registros de extensão; seguir as referências (é aqui que arquivos muito fragmentados escondem o tamanho real);
  - flags: diretório, comprimido, esparso, criptografado, reparse point, hidden/system;
  - `HardLinkCount` de `$STANDARD_INFORMATION` > 1 → registrar por **FRN** e contar espaço **uma única vez**.
- **Reconstrução de caminhos**: cada registro guarda o índice do pai. Caminhos completos são materializados *lazily* (só para o que aparece na tela ou vai para relatório). Rodar `for each file: build full path` no scan é o segundo gargalo clássico — aqui não acontece.
- **Reparse points / junctions / symlinks**: nunca atravessar. Registrar o alvo e marcar. Isso evita ciclos infinitos (`C:\Documents and Settings` → `C:\Users`) e contagem dupla.

### 7.3 Contabilidade honesta de espaço

O app apresenta **três** números, com rótulos claros:

| Métrica | Definição | Onde aparece |
|---|---|---|
| **Tamanho lógico** | `$DATA.DataSize` | Padrão nas listas |
| **Tamanho em disco** | `AllocatedSize` (reflete compressão, esparsidade e overhead de cluster) | Coluna opcional, e sempre no cálculo de "espaço a recuperar" |
| **Espaço recuperável** | `AllocatedSize` menos o que continuará ocupado (hardlinks remanescentes, blocos travados em Shadow Copy) | No painel de confirmação de qualquer ação |

Contabilizados separadamente no dashboard, porque não são "arquivos" mas comem disco: `pagefile.sys`, `hiberfil.sys`, `swapfile.sys`, **Volume Shadow Copies**, espaço não alocado, `$MFT` e metadados do NTFS, e o **slack space** agregado (soma de `AllocatedSize − DataSize`).

### 7.4 Paralelismo

Três pools independentes e configuráveis, porque têm perfis opostos:

| Pool | Trabalho | Padrão | Limite |
|---|---|---|---|
| `IoPool` | Leitura sequencial da MFT / travessia de diretórios | 1 por **volume físico distinto** | Aumentar em NVMe (fila profunda ajuda); manter 1 em HDD (paralelizar seek em disco mecânico *piora*) |
| `HashPool` | Hash de duplicados (I/O + CPU) | `min(nCores, 8)` | Configurável até `nCores × 2` |
| `CpuPool` | Perceptual hash, decode de thumbnail, treemap | `nCores − 1` | Configurável; respeita o limite global de CPU |

Detecção automática do tipo de mídia via `IOCTL_STORAGE_QUERY_PROPERTY` / `DeviceSeekPenaltyProperty` para escolher os padrões de HDD vs SSD. Coordenação por `System.Threading.Channels` (produtor único da MFT → N consumidores), com *backpressure* limitada para a memória não explodir se a UI ficar atrás.

**Modo Bulldozer** (um toggle): eleva todos os pools ao máximo, sobe a prioridade do processo para `AboveNormal`, desliga o *throttling* de I/O e avisa que o PC vai ficar lento durante a varredura.
**Modo Silencioso**: 2 threads, prioridade `BelowNormal`, `FILE_FLAG_SEQUENTIAL_SCAN` com pausas — para varrer enquanto se trabalha.

---

## 8. Requisitos funcionais

Prioridade: **P0** = v1.0 obrigatório · **P1** = v1.0 desejável · **P2** = pós-v1.

### 8.1 Varredura e mapeamento

| ID | Requisito | Pri |
|---|---|---|
| F1.1 | Selecionar múltiplos volumes, pastas específicas ou "todo o computador" | P0 |
| F1.2 | Varrer unidades de rede e externas (via S3) | P0 |
| F1.3 | Resultados em **streaming**: a árvore vai crescendo durante a varredura, sem tela de espera | P0 |
| F1.4 | Cancelar/pausar/retomar varredura a qualquer momento | P0 |
| F1.5 | Snapshot persistente por volume + atualização incremental via USN na reabertura | P0 |
| F1.6 | **Comparar dois snapshots** ("o que cresceu desde ontem?") — mata a pergunta "o que encheu meu disco esta semana?" | P1 |
| F1.7 | Escopo com include/exclude por glob (`**/node_modules/**`, `*.iso`) | P0 |
| F1.8 | Contabilizar ADS, arquivos esparsos, comprimidos e hardlinks corretamente | P0 |
| F1.9 | Detectar e reportar espaço de Volume Shadow Copy, pagefile e hiberfil | P0 |
| F1.10 | Varredura de arquivos de placeholder do OneDrive/nuvem **sem hidratá-los** (checar `FILE_ATTRIBUTE_RECALL_ON_*`) | P0 |

### 8.2 Visualização

| ID | Requisito | Pri |
|---|---|---|
| F2.1 | **Treemap squarified** com zoom, drill-down, cor por tipo de arquivo e tooltip; renderização em `DrawingVisual` para aguentar 100 k retângulos | P0 |
| F2.2 | **Árvore de pastas** com barra de proporção, `%` do pai, `%` do disco, ordenável por tamanho/contagem/data; virtualizada | P0 |
| F2.3 | **Top-N arquivos** (100 / 1000 / configurável) global ou por subárvore | P0 |
| F2.4 | **Top-N pastas** por tamanho *próprio* e por tamanho *total* (duas colunas — a distinção importa) | P0 |
| F2.5 | Agrupamento por **extensão / categoria** (vídeo, imagem, áudio, arquivo compactado, instalador, documento, código, build, VM, jogos) com tamanho e contagem | P0 |
| F2.6 | **Mapa de calor por idade**: distribuição por `LastAccessTime` / `LastWriteTime` em faixas (7 d, 30 d, 90 d, 1 a, 2 a+) — clicável para filtrar | P1 |
| F2.7 | **Histograma de tamanhos** (<4 KB, 4–64 KB, … >1 GB) com contagem e slack agregado — é o painel que responde "arquivos pequenos que atrapalham" | P1 |
| F2.8 | Sunburst como visualização alternativa ao treemap | P2 |
| F2.9 | Busca instantânea sobre o índice em memória (substring + regex + filtros compostos), resposta < 100 ms em 1 M arquivos | P0 |
| F2.10 | Filtros compostos salváveis: `tamanho > 500MB E não acessado há > 180d E extensão em (mp4,mkv) E fora de C:\Windows` | P0 |

### 8.3 Limpeza por regras (lixo de sistema e cache de apps)

| ID | Requisito | Pri |
|---|---|---|
| F3.1 | **Catálogo de regras em JSON externo** (≥ 120 regras — ver [Anexo A](#anexo-a--catálogo-de-regras-de-limpeza)), editável e extensível pelo usuário sem recompilar | P0 |
| F3.2 | Cada regra traz: nome, descrição em 1 linha, **nível de risco**, caminhos/globs, condições (idade, tamanho, processo não rodando), ganho estimado, ação recomendada e link "saiba mais" | P0 |
| F3.3 | **Dry-run sempre**: a lista mostra exatamente o que será tocado, item por item, antes de qualquer execução | P0 |
| F3.4 | Regras que dependem de ferramenta do sistema chamam a ferramenta certa em vez de apagar arquivos na mão: `DISM /StartComponentCleanup` (WinSxS), `vssadmin` (Shadow Copies), `powercfg /h off` (hiberfil), `Optimize-VHD`/`diskpart compact` (WSL/VHDX) | P0 |
| F3.5 | Detectar processo dono do cache e oferecer "fechar app e limpar" (navegador aberto = cache travado) | P1 |
| F3.6 | **Resíduos de programas desinstalados**: pastas em `Program Files`/`AppData` sem entrada de uninstall correspondente | P1 |
| F3.7 | **Perfis**: `Limpeza Rápida` (só SEGURO), `Limpeza Profunda` (SEGURO + ATENÇÃO com confirmação), `Modo Dev`, `Modo Criador de Conteúdo`, `Personalizado` | P0 |
| F3.8 | Agendamento (Agendador de Tarefas do Windows) com perfil, hora e ação fixos + notificação de resultado | P1 |
| F3.9 | Gatilho por limiar: "quando C: cair abaixo de 10 GB livres, avise / rode o perfil X" | P1 |

### 8.4 Duplicados

| ID | Requisito | Pri |
|---|---|---|
| F4.1 | **Duplicados exatos em 4 estágios**: (1) agrupar por tamanho → (2) XxHash128 dos primeiros 8 KB → (3) XxHash128 dos últimos 8 KB → (4) **BLAKE3 do arquivo inteiro**. Só grupos que sobrevivem ao estágio anterior avançam | P0 |
| F4.2 | Zero falso positivo: o veredito final é sempre o hash completo (byte-comparison opcional para paranoicos, via config) | P0 |
| F4.3 | **Seleção inteligente do "guardar"**: por caminho preferido, data mais antiga/recente, nome mais curto, fora de `Downloads`, na unidade mais rápida — regras encadeáveis e pré-visualizáveis | P0 |
| F4.4 | Ação alternativa **sem apagar**: substituir duplicatas por **hardlinks** (mesmo volume NTFS) — libera o espaço mantendo todos os caminhos funcionando | P1 |
| F4.5 | **Near-duplicates de imagem**: pHash/dHash + distância de Hamming ajustável; agrupa a mesma foto em resoluções/qualidades diferentes | P1 |
| F4.6 | **Near-duplicates de vídeo**: fingerprint por keyframes amostrados (N frames → pHash → assinatura); detecta o mesmo vídeo em bitrates/containers diferentes | P1 |
| F4.7 | **Duplicados de áudio** por fingerprint acústico (Chromaprint), ignorando tags/bitrate | P2 |
| F4.8 | **Pastas duplicadas**: detectar árvores inteiras idênticas e propor a exclusão da pasta, não de 4.000 arquivos | P1 |
| F4.9 | Nunca sugerir apagar **todas** as cópias de um grupo — a UI impede estruturalmente | P0 |

### 8.5 Outros detectores

| ID | Requisito | Pri |
|---|---|---|
| F5.1 | **Arquivos gigantes** acima de limiar configurável, com contexto ("está em C:\Users\...\Downloads, nunca aberto") | P0 |
| F5.2 | **Arquivos vazios** (0 byte) e **pastas vazias** (recursivamente vazias) | P0 |
| F5.3 | **Atalhos quebrados** (`.lnk`/`.url` apontando para o nada) | P1 |
| F5.4 | **Downloads antigos**: instaladores (`.exe`/`.msi`/`.iso`/`.zip`) em pastas de download, não acessados há N dias | P0 |
| F5.5 | **Artefatos de build**: `node_modules`, `.venv`, `__pycache__`, `target`, `build`, `bin`, `obj`, `dist`, `.next`, `.gradle`, `Pods` — com detecção do projeto pai e da última modificação dele | P0 |
| F5.6 | **Arquivos temporários órfãos**: `~$*`, `*.tmp`, `*.part`, `*.crdownload`, `*.!ut`, `Thumbs.db`, `desktop.ini`, `.DS_Store`, dumps `*.dmp`/`*.mdmp` | P0 |
| F5.7 | **Logs**: `*.log`/`*.etl`/`*.evtx` acima de tamanho ou idade, com rotação sugerida em vez de exclusão cega | P1 |
| F5.8 | **Instaladores já instalados**: casar nome/versão de `.msi`/`.exe` com programas presentes | P2 |
| F5.9 | **Overhead de arquivos pequenos**: relatório de slack space por pasta, respondendo "380 mil arquivos de 2 KB desperdiçam X GB de cluster" | P1 |
| F5.10 | **Candidatos a compressão NTFS**: arquivos grandes, texto/log, raramente lidos → compactar em vez de apagar (ganho sem perda) | P1 |
| F5.11 | **Candidatos a realocação**: "estes 240 GB de mídia podem ir para o D:" com criação de junction para os caminhos não quebrarem | P2 |

### 8.6 Preview e navegação (requisito explícito do usuário)

| ID | Requisito | Pri |
|---|---|---|
| F6.1 | **Player de vídeo/áudio embutido** (LibVLCSharp): play/pause, seek, volume, mudo, velocidade, próximo/anterior na seleção, tela cheia, timeline com thumbnails ao passar o mouse | P0 |
| F6.2 | **Play instantâneo**: um clique na linha (ou barra de espaço) começa a tocar no painel lateral, sem abrir janela nova | P0 |
| F6.3 | O player **nunca segura o arquivo**: fecha o handle ao trocar de item, para não bloquear a exclusão. Ao apagar o item que está tocando, para o player primeiro | P0 |
| F6.4 | **Visualizador de imagem** com zoom/pan, EXIF (câmera, data, GPS), suporte a HEIC/WebP/AVIF/RAW via WIC | P0 |
| F6.5 | **Miniatura do conteúdo na própria lista**: imagem e vídeo mostram a foto / um frame real; todo outro tipo mostra o ícone registrado dele. É o que permite decidir o que apagar **sem abrir o arquivo** | P0 |
| F6.5a | **Tamanho de ícone escolhido pelo usuário**, alternável na hora: 16 / 32 / 64 / 128 / 256 / 512 px, com atalho `Ctrl +` / `Ctrl −` e persistência da escolha | P0 |
| F6.5b | Miniatura via `IShellItemImageFactory`, pedida com `SIIGBF_THUMBNAILONLY` primeiro e caindo para `SIIGBF_ICONONLY`. A separação existe para o rótulo "veio do conteúdo" ser **fato verificado**, não palpite — sem ela, um `.md` sem handler de preview seria anunciado como se a miniatura fosse do conteúdo | P0 |
| F6.5c | Sem `SIIGBF_BIGGERSIZEOK`: o Shell devolveria o tamanho que tiver em cache (pedir 64 devolve 96) e a lista precisa do tamanho que o usuário escolheu | P0 |
| F6.6 | **Preview de texto/código** com syntax highlight, e **hex viewer** para binário (primeiros 64 KB) | P1 |
| F6.7 | Ficha técnica de mídia: duração, resolução, codec, bitrate, canais, sample rate (`MediaInfo`/libVLC) — permite "apagar a versão 720p, guardar a 4K" | P0 |
| F6.8 | **Ícone/ação "abrir pasta de origem"** que chama `explorer.exe /select,"<caminho>"` deixando o arquivo já selecionado | P0 |
| F6.9 | **Ícone "abrir arquivo"** com o app padrão, e **"abrir com…"** | P0 |
| F6.10 | **Menu de contexto nativo do Shell** dentro do app (`IContextMenu`) — todas as opções que o Explorer daria | P1 |
| F6.11 | Copiar caminho / caminho UNC / nome / pasta; arrastar-e-soltar itens para fora do app | P0 |
| F6.12 | Ícone de arquivo real (`SHGetFileInfo` + `IImageList`) com cache LRU em memória, carregado fora da thread de UI | P0 |
| F6.13 | Modo galeria/grade de miniaturas para varreduras de mídia | P1 |
| F6.14 | Comparação lado a lado de duplicados (imagem A vs B, ou dois players sincronizados) | P1 |

### 8.7 Ações e reversibilidade

| ID | Requisito | Pri |
|---|---|---|
| F7.1 | **Quarentena** — ação padrão. `MoveFile` no mesmo volume (instantâneo) para `<vol>\$Vacuon.Quarantine\<lote>\`, com `manifest.json` guardando caminho original, tamanho, hashes, timestamps e ACLs | P0 |
| F7.2 | **Restauração de 1 clique** de um item, de uma seleção ou do lote inteiro, para o caminho original | P0 |
| F7.3 | Política de expurgo da quarentena: por idade (padrão 30 d), por tamanho máximo, ou só manual | P0 |
| F7.4 | **Lixeira do Windows** como alternativa (`SHFileOperation` com `FOF_ALLOWUNDO`), avisando que arquivos maiores que a cota da Lixeira serão apagados de vez | P0 |
| F7.5 | **Exclusão permanente** com confirmação que exige digitar o total ou marcar "entendi que é irreversível" | P0 |
| F7.6 | **Shred seguro** (sobrescrita) — com aviso honesto e explícito: **em SSD/NVMe o wear leveling torna a sobrescrita ineficaz**; a recomendação correta é criptografia de volume ou `TRIM` | P1 |
| F7.7 | **Arquivos em uso**: identificar o processo dono via Restart Manager, oferecer (a) fechar o app, (b) `MoveFileEx(..., DELAY_UNTIL_REBOOT)`, (c) pular | P0 |
| F7.8 | **Ponto de restauração do sistema** opcional antes de lotes com itens de risco ATENÇÃO+ | P1 |
| F7.9 | Compactar seleção em `.zip`/`.7z` antes de apagar os originais | P1 |
| F7.10 | Mover seleção para outro volume, opcionalmente deixando junction/symlink no lugar | P1 |
| F7.11 | Ativar compressão NTFS na seleção (`FSCTL_SET_COMPRESSION`) | P1 |
| F7.12 | Converter duplicatas em hardlinks (`CreateHardLink`) | P1 |
| F7.13 | Toda ação em fila, cancelável, com relatório por item (sucesso / pulado / erro + motivo) | P0 |
| F7.14 | **Histórico de operações** persistente: o que foi feito, quando, quanto liberou, restaurável enquanto a quarentena existir | P0 |

### 8.8 Monitoramento

| ID | Requisito | Pri |
|---|---|---|
| F8.1 | Monitor em tempo real via `FSCTL_READ_USN_JOURNAL`: "quem está criando arquivos agora?" — mata o mistério do "meu disco perde 1 GB por hora" | P1 |
| F8.2 | Widget de espaço livre por volume, com tendência (setinha) e projeção "cheio em ~N dias" | P1 |
| F8.3 | Notificação nativa do Windows ao cruzar limiar de espaço | P1 |
| F8.4 | Ícone na bandeja com espaço livre e acesso rápido à Limpeza Rápida | P2 |

### 8.9 Inspeção de persistência e arquivos suspeitos

Módulo exclusivo, **somente-leitura**, sem equivalente nos limpadores de disco do mercado. Não é antivírus: não há base de assinaturas. O que existe é heurística de comportamento, sempre com o motivo visível, para o usuário julgar.

| ID | Requisito | Pri |
|---|---|---|
| F9.1 | **Catálogo de 44 pontos de autorun do registro** onde malware persiste, seguindo Sysinternals Autoruns e MITRE ATT&CK T1547/T1546: `Run`/`RunOnce`/`RunOnceEx`/`RunServices`, `Policies\Explorer\Run`, `Winlogon` (Shell, Userinit, Taskman, Notify), `AppInit_DLLs`, `AppCertDlls`, `BootExecute`, `Image File Execution Options\Debugger`, `SilentProcessExit\MonitorProcess`, pacotes do `Lsa`, `Command Processor\AutoRun`, `UserInitMprLogonScript`, Active Setup, BHOs, `ShellServiceObjectDelayLoad`, `SharedTaskScheduler`, sequestro de associação de arquivo, `User Shell Folders\Startup` | P0 |
| F9.2 | **Comparação com o valor esperado** onde ele existe: `Winlogon\Shell` deve ser `explorer.exe`, `Userinit` deve ser `userinit.exe,`, `AppInit_DLLs` deve estar vazio. Divergir é o próprio sinal | P0 |
| F9.3 | **Heurísticas de linha de comando**: PowerShell com `-EncodedCommand`, janela oculta, `ExecutionPolicy Bypass`, `DownloadString`/`Invoke-Expression`, URL embutida, LOLBins (`mshta`, `certutil -decode`, `bitsadmin`, `regsvr32`, `rundll32 javascript:`), executável em pasta volátil, script no perfil do usuário, nome imitando binário do sistema (`svch0st.exe`) | P0 |
| F9.4 | **Pastas de Inicialização e Tarefas Agendadas** inspecionadas junto | P0 |
| F9.5 | **Assinatura Authenticode** do alvo exibida quando existe | P1 |
| F9.6 | **Arquivos suspeitos** durante a varredura de disco: extensão dupla (`nota.pdf.exe`), caractere Unicode RLO invertendo a extensão visível, executável oculto, executável com ADS grande, extensões de phishing (`.scr`/`.pif`/`.hta`/`.jse`), executável recém-criado em System32 | P0 |
| F9.7 | **Zero escrita.** O módulo lista, explica e para. Nenhuma chave é alterada, desabilitada ou removida | P0 |
| F9.8 | **Falso positivo é bug.** Toda heurística nova exige um teste positivo e um negativo | P0 |

**Calibração — os falsos positivos que já custaram caro** (encontrados rodando contra uma máquina real e corrigidos):

| Sinal ingênuo | Por que estava errado |
|---|---|
| "binário sem assinatura digital" | Binários do Windows são assinados por **catálogo** (`.cat`), não com assinatura embutida no PE. Cobrar assinatura embutida marcava `rundll32.exe`, `unregmp2.exe` e `ie4uinit.exe` como suspeitos |
| "arquivo apontado não existe" | `msv1_0`, `scecli`, `{CLSID}` e `IEToEdge BHO` são **nomes**, não caminhos. Só vale checar existência quando o valor realmente parece um caminho |
| "usa rundll32 (LOLBin)" | O Active Setup do próprio Windows chama `rundll32` o tempo todo. Só conta fora do diretório do sistema |
| "executável em pasta volátil: AppData\Local" | Chrome, Discord, Opera e Roblox **instalam ali por padrão**. Sinalizar a pasta gerava 4 alarmes falsos em toda máquina |
| "autorun órfão: `/UserInstall`" | Um switch de linha de comando não é caminho. Normalizar `/` para `\` inventava um arquivo inexistente |

Efeito da calibração numa máquina real: **de 21 achados (19 deles ruído) para 1** — e o que sobrou é verdadeiro ("Tarefas Agendadas exigem Administrador para serem lidas").

---

## 9. Segurança e modelo de risco

Esta seção é a mais importante do documento. Um app que apaga arquivos tem exatamente uma chance de errar.

### 9.1 Classificação de risco (obrigatória em toda regra e todo item)

| Nível | Cor | Significado | Comportamento na UI |
|---|---|---|---|
| 🟢 **SEGURO** | verde | Regenerável pelo sistema/app. Perda = nenhuma, além de um cache frio na próxima abertura | Pré-selecionado nos perfis rápidos |
| 🟡 **ATENÇÃO** | amarelo | Perda funcional aceitável mas perceptível (histórico do navegador, thumbnails, pontos de restauração antigos) | Nunca pré-selecionado; exige marcação manual |
| 🟠 **PERIGOSO** | laranja | Pode quebrar um app específico ou perder dado do usuário (cache de projeto, `hiberfil`, saves de jogo em pasta de cache) | Exige confirmação individual + aviso do que se perde |
| 🔴 **BLOQUEADO** | vermelho | O Vacuon **se recusa a apagar**, ponto. Não há opção, config ou flag que libere | Item aparece cinza, com o motivo |

### 9.2 Lista de proteção absoluta (🔴 BLOQUEADO)

Verificação em **duas camadas**: por caminho canônico (resolvendo symlink, junction, `8.3`, `\\?\`, e variações de case/Unicode) **e** por regra estrutural.

- `%SystemRoot%\System32`, `SysWOW64`, `WinSxS` (exceto pela via oficial do DISM), `Boot`, `Fonts`, `assembly`, `servicing`
- `%SystemDrive%\$Recycle.Bin` como estrutura, `System Volume Information`, `$MFT` e todos os metafiles do NTFS
- `pagefile.sys`, `swapfile.sys`, `hiberfil.sys`, `DumpStack.log.tmp` — só via API/`powercfg`, nunca por exclusão de arquivo
- Raiz de qualquer volume (o item "volume" nunca é alvo de exclusão)
- `Program Files` / `Program Files (x86)` / `ProgramData` — **binários e configurações**; apenas subcaminhos explicitamente listados como cache são elegíveis
- `%UserProfile%` — `Documents`, `Desktop`, `Pictures`, `Videos`, `Music`, `Downloads` (os arquivos do usuário nunca entram em regra automática; só em seleção manual explícita)
- Bases de dados de credencial e chave: `AppData\Roaming\Microsoft\Crypto`, `Protect`, `Vault`, `%windir%\System32\config`
- Qualquer caminho de instalação do próprio Vacuon
- Extensões nunca alvo de regra automática: `.sys`, `.dll`, `.exe` do sistema, `.kdbx`, `.pfx`, `.key`, `.pem`, `.wallet`, `.dat` de carteira
- Arquivos com `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (placeholder de nuvem) — apagar apaga da nuvem também; exige confirmação especial e nunca entra em regra

### 9.3 Salvaguardas de comportamento

1. **Dry-run universal.** Nenhuma ação sem lista revisável antes.
2. **Teto de segurança.** Lote acima de 50 GB ou 50 000 itens exige confirmação reforçada com resumo por categoria.
3. **Sem exclusão automática.** Mesmo agendado, o padrão é mover para a quarentena, nunca apagar.
4. **Idempotência e atomicidade por item.** Falha em um item nunca aborta o lote nem deixa estado inconsistente; tudo vai para o relatório.
5. **Verificação de espaço pós-operação.** Se `GetDiskFreeSpaceEx` não mostrar o ganho previsto, o app avisa e explica (Shadow Copy, hardlink remanescente, cota da Lixeira).
6. **Elevação mínima.** Admin só é pedido para varredura por MFT e para ações em áreas de sistema — nunca "por padrão".
7. **Instância única por volume.** Trava para duas varreduras não concorrerem pelo mesmo dispositivo.
8. **Modo somente-leitura** (`--readonly`) que desabilita fisicamente toda a camada de ações — para uso em máquina de cliente/auditoria.
9. **Autoproteção.** O app se recusa a operar sobre a própria pasta, a própria quarentena e o próprio log.

---

## 10. UI/UX

### 10.1 Estrutura de navegação

```
┌───────────────────────────────────────────────────────────────────────────┐
│ Vacuon            [C:] 41 GB livres de 931 GB  ▁▂▃█ ██████████░░  [⚙] [?] │
├──────────┬────────────────────────────────────────────────────────────────┤
│          │                                                               │
│ ▣ Painel │   ← área principal, muda por seção                            │
│ ⌕ Explor.│                                                               │
│ ◫ Treemap│                                                               │
│ ✦ Limpeza│                                                               │
│ ⧉ Duplic.│                                                               │
│ ▶ Mídia  │                                                               │
│ ⏱ Antigos│                                                               │
│ ⌫ Quarent│                                                               │
│ ▤ Relat. │                                                               │
├──────────┴────────────────────────────────────────────────────────────────┤
│ Selecionado: 214 itens · 87,4 GB  [▶ Preview] [📁 Abrir pasta]           │
│                                   [⌫ Quarentena] [🗑 Lixeira] [✕ Apagar] │
└───────────────────────────────────────────────────────────────────────────┘
```

**Painel (Dashboard)** — cartões de volume com barra de uso; "você pode liberar até **X GB**" quebrado por categoria (temporários, cache, duplicados, mídia antiga, artefatos de build); botão *Limpeza Rápida*; o que mudou desde o último snapshot.

**Explorer** — a tela de trabalho. Árvore virtualizada à esquerda, lista de arquivos à direita (colunas: ícone, nome, tamanho, em disco, tipo, modificado, acessado, atributos, caminho), painel de preview à direita ou embaixo (dockável), barra de filtros no topo, barra de ação embaixo.

**Treemap** — tela cheia, drill-down por clique, breadcrumb, legenda de cor por categoria, hover mostrando caminho + tamanho; clique com o botão direito abre o mesmo menu de contexto da lista.

**Limpeza** — árvore de categorias → regras, com checkbox tri-estado, ganho estimado por nó, badge de risco, painel de detalhe explicando a regra em linguagem humana e listando os arquivos alvo.

**Duplicados** — grupos colapsáveis; dentro do grupo, o item "guardar" marcado com 🔒 e as demais cópias com checkbox; comparação lado a lado; regras de seleção automática no topo.

**Mídia** — grade de miniaturas + player, ordenável por tamanho/duração/resolução; ideal para o caso "qual render é o final?".

**Quarentena** — lotes por data, tamanho recuperável, dias até o expurgo, botões restaurar/expurgar.

**Relatórios** — histórico de operações, total liberado ao longo do tempo, exportação HTML/CSV/JSON.

### 10.2 Diretrizes de interação

- **Nada bloqueia a UI.** Toda operação longa é assíncrona, com progresso determinístico (arquivos/s, MB/s, ETA) e cancelamento real.
- **Seleção robusta**: Ctrl/Shift-clique, "selecionar tudo que combina com o filtro atual", inverter, e um chip persistente mostrando "214 itens · 87,4 GB" que sobrevive à troca de tela.
- **Undo visível**: após qualquer ação, uma barra "87,4 GB movidos para a quarentena · **Desfazer**" fica na tela.
- **Empty states úteis**: "Nenhum duplicado encontrado — seus 340 mil arquivos são todos únicos" em vez de uma lista vazia.
- **Acessibilidade**: navegação completa por teclado, foco visível, contraste AA, compatível com leitor de tela nos controles principais, respeita a escala de DPI e a preferência de tema do sistema.
- **Sem alarmismo.** Zero linguagem de "seu PC está em risco!". O tom é de instrumento de medição.

### 10.3 Atalhos de teclado

| Tecla | Ação | Tecla | Ação |
|---|---|---|---|
| `Espaço` | Play/pause do preview | `Enter` | Abrir arquivo |
| `Ctrl+Enter` | Abrir pasta de origem | `Del` | Mover para quarentena |
| `Shift+Del` | Apagar permanentemente (com confirmação) | `Ctrl+Z` | Desfazer último lote |
| `Ctrl+F` | Busca | `Ctrl+Shift+F` | Filtro avançado |
| `F5` | Rescan incremental | `Ctrl+Shift+F5` | Rescan completo |
| `Ctrl+C` | Copiar caminho | `Ctrl+A` | Selecionar tudo |
| `F11` | Tela cheia do player | `Esc` | Cancelar operação atual |
| `Ctrl+1..9` | Ir para seção | `Ctrl+D` | Marcar/desmarcar item |

### 10.4 Identidade visual

Tema escuro como padrão (é ferramenta de trabalho), claro disponível, seguindo o tema do sistema quando configurado. Acento **âmbar/laranja** (o "E" de Vacuon), verde/amarelo/laranja/vermelho reservados exclusivamente para a escala de risco — nenhum outro elemento da UI usa essas cores, para o risco nunca ser ambíguo. Tipografia: Segoe UI Variable; números sempre tabulares e monoespaçados nas colunas de tamanho.

---

## 11. Performance — metas e engenharia

### 11.1 Metas mensuráveis

| Cenário | Meta | Limite aceitável |
|---|---|---|
| Varredura MFT, 1 M arquivos, SATA SSD | 5 s | 10 s |
| Varredura MFT, 1 M arquivos, HDD 5400 | 15 s | 25 s |
| Update incremental via USN | 300 ms | 1 s |
| Carregar snapshot do disco | 400 ms | 1 s |
| Busca por substring em 1 M arquivos | 50 ms | 100 ms |
| Ordenar 1 M itens por tamanho | 150 ms | 400 ms |
| Treemap de 100 k retângulos | 60 fps | 30 fps |
| Duplicados: hash de 50 GB de candidatos, NVMe | 90 s | 180 s |
| Memória, índice de 1 M arquivos | 120 MB | 250 MB |
| Memória, ocioso após varredura | 80 MB | 150 MB |
| Início a frio até janela interativa | 400 ms | 1 s |
| Tamanho do `.exe` single-file | 40 MB | 80 MB |

### 11.2 Técnicas

**Layout de dados**
- Um `struct FileEntry` de **64 bytes** (alinhado a linha de cache) em um único `FileEntry[]`. Sem `class`, sem ponteiro por nó, sem `Dictionary` no caminho quente.
- Nomes em um **único blob** `char[]`; a entrada guarda `(offset, length)`. Evita 1 M objetos `string` (que custariam ~40 MB de overhead só de cabeçalho).
- Hierarquia por **índice do pai** (`int`), não por referência. Subir a árvore é aritmética de array.
- Caminhos completos materializados **sob demanda**, com `stackalloc`/`ArrayPool` e nunca em lote.

**Alocação**
- `ArrayPool<byte>` para todos os buffers de leitura (8 MB reusados, não realocados por bloco).
- `Span<T>`/`ReadOnlySpan<T>` em todo o parser — parse de MFT com **zero alocação por registro**.
- Server GC + `TieredPGO` + `ReadyToRun`. `GCHeapHardLimit` configurável para o usuário limitar o teto de RAM.
- Sem LINQ, sem `foreach` sobre interface, sem boxing dentro dos loops de parse e de agregação.

**I/O**
- Leitura sequencial em blocos grandes; `FILE_FLAG_NO_BUFFERING` quando o alinhamento permite (evita cópia dupla pelo cache do SO).
- Fila profunda em NVMe, serialização em HDD (decidido pelo `SeekPenalty`).
- Hash com `MemoryMappedFile` para arquivos grandes; leitura em chunks para os pequenos.
- Pré-filtro de duplicados descarta tipicamente **>95 %** dos candidatos antes de qualquer leitura completa.

**UI**
- `VirtualizingStackPanel` com `VirtualizationMode=Recycling` e `ScrollUnit=Item` em toda lista.
- Treemap desenhado em `DrawingVisual` com `RenderTargetBitmap` cacheado por nível de zoom — não são 100 k elementos visuais no árvore visual do WPF.
- Ícones e thumbnails resolvidos em fila de baixa prioridade, com placeholder imediato e cache LRU.
- Progresso reportado com *throttle* (máx. 20 updates/s); sem `Dispatcher.Invoke` por item.

**Snapshot**
- Formato binário próprio: cabeçalho + arrays crus (`FileEntry[]`, blob de nomes), gravado com `SequentialWrite`, comprimido com LZ4 (opcional). Carregar = ler bloco + `MemoryMarshal.Cast`. Praticamente instantâneo.

---

## 12. Configuração

`config.ini` ao lado do `.exe` (modo portátil) ou em `%AppData%\Vacuon\` (instalado). **Toda** chave também é sobrescrevível por argumento de linha de comando. Chave ausente ou comentada volta ao default interno — o valor precisa estar *presente* para valer.

```ini
[scan]
STRATEGY              = auto        ; auto | mft | usn | walk
THREADS_IO            = auto        ; auto = 1 por dispositivo físico
THREADS_HASH          = auto        ; auto = min(nCores, 8)
THREADS_CPU           = auto        ; auto = nCores - 1
MEMORY_BUDGET_MB      = 1024        ; teto do índice + caches; 0 = sem limite
READ_BLOCK_MB         = 8
FOLLOW_REPARSE_POINTS = 0           ; 0 = nunca atravessar junction/symlink
INCLUDE_HIDDEN        = 1
INCLUDE_SYSTEM        = 1
SCAN_ADS              = 1           ; alternate data streams
HYDRATE_CLOUD_FILES   = 0           ; 0 = nunca baixar placeholder do OneDrive
EXCLUDE_GLOBS         = C:\Windows\WinSxS\**;**\System Volume Information\**
INCREMENTAL_USN       = 1
SNAPSHOT_DIR          = %LOCALAPPDATA%\Vacuon\snapshots

[performance]
PROFILE               = balanced    ; quiet | balanced | bulldozer
PROCESS_PRIORITY      = normal      ; belownormal | normal | abovenormal
IO_PRIORITY           = normal      ; low | normal
CPU_LIMIT_PERCENT     = 0           ; 0 = sem limite
SERVER_GC             = 1
LOW_MEMORY_MODE       = 0           ; índice em arquivo mapeado em vez de RAM

[duplicates]
MIN_SIZE_BYTES        = 4096
PREFILTER_CHUNK_KB    = 8
STRONG_HASH           = blake3      ; blake3 | xxh128 | sha256
BYTE_VERIFY           = 0           ; 1 = comparação byte a byte no veredito final
NEAR_DUP_IMAGES       = 1
NEAR_DUP_THRESHOLD    = 8           ; distância de Hamming máxima (0 = idêntico)
NEAR_DUP_VIDEO        = 1
VIDEO_KEYFRAMES       = 12
KEEP_RULE             = oldest,shortest_path,not_in_downloads

[cleanup]
RULES_FILE            = rules\catalog.json
USER_RULES_FILE       = rules\user.json
PROFILE               = quick       ; quick | deep | dev | creator | custom
MIN_AGE_DAYS          = 7           ; padrão para regras sensíveis a idade
CLOSE_APPS_FOR_CACHE  = 0
CREATE_RESTORE_POINT  = 1           ; antes de lotes com risco ATENÇÃO+

[actions]
DEFAULT_ACTION        = quarantine  ; quarantine | recyclebin | delete
QUARANTINE_DIR        = <volume>\$Vacuon.Quarantine
QUARANTINE_KEEP_DAYS  = 30
QUARANTINE_MAX_GB     = 50
CONFIRM_ABOVE_GB      = 50
CONFIRM_ABOVE_ITEMS   = 50000
SHRED_PASSES          = 1
DELETE_LOCKED_ON_BOOT = 1
READONLY              = 0           ; 1 = desabilita toda ação destrutiva

[preview]
PLAYER                = vlc         ; vlc | shell
AUTOPLAY              = 1
THUMBNAIL_SIZE        = 256
THUMBNAIL_CACHE_MB    = 256
MEDIA_PROBE           = 1           ; codec, bitrate, resolução, duração

[ui]
THEME                 = system      ; system | dark | light
LANGUAGE              = pt-BR       ; pt-BR | en-US
SIZE_UNITS            = binary      ; binary (GiB) | decimal (GB)
SHOW_SIZE_ON_DISK     = 1
CONFIRM_SOUND         = 0

[logging]
LEVEL                 = info        ; trace | debug | info | warn | error
DIR                   = %LOCALAPPDATA%\Vacuon\logs
KEEP_DAYS             = 30
AUDIT_ALL_ACTIONS     = 1           ; log imutável de tudo que foi apagado
```

---

## 13. Modelo de dados

```csharp
// 64 bytes, alinhado — a unidade fundamental do índice
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct FileEntry
{
    public ulong  FileRefNumber;   //  8  FRN do NTFS (identidade estável)
    public int    ParentIndex;     //  4  índice no mesmo array (-1 = raiz)
    public int    NameOffset;      //  4  posição no blob de nomes
    public ushort NameLength;      //  2
    public EntryFlags Flags;       //  2  Directory|Compressed|Sparse|Reparse|
                                   //     Hidden|System|Encrypted|HasAds|
                                   //     CloudPlaceholder|HardLinked
    public long   LogicalSize;     //  8  $DATA.DataSize
    public long   AllocatedSize;   //  8  tamanho em disco
    public long   LastWriteUtc;    //  8  FILETIME
    public long   LastAccessUtc;   //  8
    public long   CreatedUtc;      //  8
    public ushort HardLinkCount;   //  2
    public ushort _pad;            //  2
}                                  // = 64

public sealed class VolumeIndex
{
    public FileEntry[] Entries;      // 1 M arquivos ≈ 64 MB
    public char[]      NameBlob;     // ≈ 40 MB
    public long        LastUsn;      // para o update incremental
    public VolumeInfo  Volume;       // letra, GUID, FS, cluster size, total/livre
    public DateTime    ScannedAtUtc;
}
```

**Regra (JSON, carregada em runtime):**

```json
{
  "id": "browser.chrome.cache",
  "name": "Cache do Google Chrome",
  "category": "Navegadores",
  "description": "Páginas e imagens guardadas para carregar sites mais rápido. Regenera sozinho; sites ficam um pouco mais lentos na primeira visita.",
  "risk": "safe",
  "targets": [
    "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\Cache\\**",
    "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\Code Cache\\**",
    "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\GPUCache\\**"
  ],
  "conditions": { "processNotRunning": ["chrome"], "minAgeDays": 0 },
  "action": "delete",
  "typicalGainMB": 800,
  "learnMore": "docs/rules/browser-cache.md"
}
```

**Manifesto de quarentena (`manifest.json`):**

```json
{
  "batchId": "2026-07-24T14-32-08Z-a91f",
  "createdUtc": "2026-07-24T14:32:08Z",
  "profile": "creator",
  "totalBytes": 93_842_112_512,
  "items": [
    {
      "quarantinePath": "$Vacuon.Quarantine/2026-07-24.../00001.bin",
      "originalPath": "C:\\Users\\romeu\\Videos\\render_v3.mp4",
      "logicalSize": 9_412_233_216,
      "allocatedSize": 9_412_235_264,
      "blake3": "9f1c…",
      "timestamps": { "created": "…", "written": "…", "accessed": "…" },
      "attributes": "Archive",
      "acl": "O:BAG:BAD:(A;;FA;;;SY)…",
      "reason": "rule:duplicates.exact · grupo 47 · cópia 3 de 4",
      "restorable": true
    }
  ]
}
```

---

## 14. CLI e automação

O mesmo binário, sem GUI quando recebe subcomando:

```bash
# Mapear e relatar (nunca apaga)
vacuon scan C: D: --report=html --out=laudo.html --top=200

# Duplicados, só listar
vacuon dupes "C:\Users\romeu\Videos" --min-size=100MB --json > dupes.json

# Limpeza segura, com dry-run explícito
vacuon clean --profile=quick --dry-run
vacuon clean --profile=quick --action=quarantine --yes

# Filtro arbitrário sobre o índice
vacuon find --min-size=1GB --not-accessed-days=180 --ext=mp4,mkv \
            --exclude="C:\Windows\**" --action=quarantine

# Quarentena
vacuon quarantine list
vacuon quarantine restore 2026-07-24T14-32-08Z-a91f
vacuon quarantine purge --older-than=30d

# Modo auditoria (toda ação desabilitada no binário)
vacuon scan C: --readonly --report=json
```

Códigos de saída: `0` sucesso · `1` sucesso parcial (itens pulados) · `2` erro de argumento · `3` sem permissão/elevação · `4` volume inacessível · `5` cancelado. Saída em JSON com `--json` para consumo por script. `--quiet` para agendamento.

---

## 15. Telemetria local, logs e relatórios

**Nenhum dado sai da máquina. Não há servidor, não há conta, não há "analytics".**

- **Log de auditoria** (`audit.jsonl`, append-only): uma linha por item tocado, com caminho, tamanho, ação, resultado, lote e timestamp. É a resposta para "onde foi meu arquivo?".
- **Log de diagnóstico** rotativo por dia, nível configurável, com métricas de varredura (estratégia usada, arquivos/s, MB/s, tempo por fase).
- **Relatórios exportáveis**: HTML autocontido (com treemap embutido em SVG), CSV para planilha, JSON para script.
- **Painel de histórico**: total liberado por mês, categorias mais recorrentes, tendência de espaço livre.

---

## 16. Roadmap

| Marco | Escopo | Entregável verificável | Estado |
|---|---|---|:-:|
| **M0 — Fundação** | Solução .NET, `Vacuon.Core`, `Vacuon.Native`, config, log, testes | `dotnet test` verde; esqueleto compila em single-file | ✅ |
| **M1 — Motor** | MftReader (runs, fixups, ADS, registros de extensão), Win32Walker de fallback, FileIndex, orquestrador com queda em cascata, CLI | CLI `vacuon scan C:` imprime top-100; 69 testes verdes sobre registros de MFT sintéticos | ✅ |
| **M1b — Segurança** | 44 pontos de autorun do registro, heurísticas de comando, detecção de arquivo disfarçado, calibração contra falso positivo | `vacuon security` em máquina limpa devolve 0 achados falsos | ✅ |
| **M1c — Miniaturas** | `IShellItemImageFactory` com `THUMBNAILONLY`→`ICONONLY`, 6 tamanhos, cache LRU | `vacuon thumb video.mkv --size=256` grava um frame real; `.md` é rotulado como ícone, não como conteúdo | ✅ |
| **M1d — Snapshot** | Formato binário do índice + atualização incremental por USN Journal | Snapshot faz round-trip exato e recusa versão ou tamanho de struct divergente; toda recusa de delta tem explicação própria; 170 testes | ✅ |
| **M2 — Visualização** | WPF, Dashboard, Explorer virtualizado, árvore, top-N, busca, filtros, ícones do Shell | Navegar 1 M arquivos a 60 fps; busca < 100 ms | ⬜ |
| **M3 — Preview** | LibVLCSharp, player, viewer de imagem, thumbnails nativas, media probe, "abrir pasta de origem", menu de contexto | Dar play em `.mkv` H.265 sem codec pack; `explorer /select` funcionando | ⬜ |
| **M2b — Exclusão** | Lixeira e exclusão permanente com multi-seleção, lista de caminhos protegidos, diálogo de confirmação com plano | 6 arquivos apagados de vez e 2 pastas para a Lixeira, recuperáveis com a origem preservada; 136 testes | ✅ |
| **M4 — Ações** | Quarentena + manifesto + restauração, Lixeira, exclusão permanente, arquivos travados, histórico, undo | Mover 90 GB para quarentena e restaurar 100 % dos itens, byte a byte idênticos | ⬜ |
| **M5 — Limpeza** | RuleEngine, catálogo de ≥ 120 regras, perfis, integrações DISM/vssadmin/powercfg, classificador de risco, listas de proteção | Limpeza Rápida em máquina real libera ≥ 10 GB sem quebrar nada; suíte de testes de proteção 100 % verde | ⬜ |
| **M6 — Duplicados** | Pipeline de 4 estágios, seleção inteligente, hardlink dedupe, pastas duplicadas | Zero falso positivo em corpus de teste de 200 k arquivos | ⬜ |
| **M7 — Treemap** | Treemap squarified, drill-down, mapa de calor por idade, histograma de tamanhos | 100 k retângulos a 60 fps | ⬜ |
| **M8 — Inteligência** | Near-duplicates de imagem e vídeo, candidatos a compressão, resíduos de desinstalação, comparação de snapshots | Agrupar a mesma foto em 5 resoluções; detectar o mesmo vídeo em 2 bitrates | ⬜ |
| **M9 — Automação** | CLI completa, agendamento, monitor USN em tempo real, notificações, gatilho por limiar | Tarefa agendada roda e notifica; monitor identifica o processo que enche o disco | ⬜ |
| **M10 — Acabamento** | Instalador + portátil, i18n pt-BR/en-US, acessibilidade, docs, assinatura de código | Instalador assinado; `.exe` portátil roda de pendrive em máquina limpa | ⬜ |

Marcos são sequenciais em dependência, não em calendário. M1 é o risco técnico concentrado — se a MFT não render a velocidade prometida, o resto do produto muda de forma.

---

## 17. Riscos e armadilhas conhecidas

### 17.1 Riscos de produto

| Risco | Impacto | Mitigação |
|---|---|---|
| Usuário apaga algo importante | Crítico — mata a confiança no app de uma vez | Quarentena como padrão, listas de proteção em duas camadas, dry-run universal, `Ctrl+Z` visível, ponto de restauração |
| Antivírus marca o app como suspeito (abre volume bruto, apaga em massa) | Alto — o app não abre | Assinatura de código com certificado EV, submissão ao Microsoft Defender/SmartScreen, evitar packers, publicar hashes |
| Números divergem de outras ferramentas → usuário desconfia | Médio | Documentar a metodologia; expor lógico vs em-disco; validar contra WizTree/TreeSize na suíte de testes |
| Complexidade da UI assusta o usuário doméstico | Médio | Dashboard + Limpeza Rápida resolvem 80 % dos casos em 2 cliques; profundidade fica nas outras abas |
| Escopo grande demais, projeto nunca termina | Alto | M1–M5 já formam um produto útil e lançável; M6+ é incremento |

### 17.2 Armadilhas técnicas (aprender aqui, não em produção)

1. **MFT fragmentada.** Ler a MFT como um bloco contíguo funciona em disco novo e **perde arquivos silenciosamente** em disco usado. É obrigatório processar a *data run list* do registro 0.
2. **Fixups do update sequence array.** Sem aplicá-los antes do parse, dois bytes de cada setor vêm errados — o parser "quase funciona", o que é pior que falhar.
3. **`FSCTL_ENUM_USN_DATA` não traz tamanho.** Quem descobre isso depois de construir o pipeline em cima dele refaz tudo. Tamanho vem do `$DATA` da MFT (ADR-1).
4. **Nomes 8.3 duplicam entradas.** Um arquivo com namespace `DOS` + `Win32` gera dois `$FILE_NAME`; contar os dois dobra o número de arquivos.
5. **Hardlinks contados N vezes.** `HardLinkCount > 1` exige dedupe por FRN, senão `C:\Windows\WinSxS` "ocupa" muito mais do que realmente ocupa.
6. **Junctions criam ciclos.** `C:\Documents and Settings` → `C:\Users`. Nunca atravessar reparse points.
7. **Placeholders do OneDrive.** Ler o arquivo **baixa** o conteúdo (enche o disco em vez de liberar) e apagar remove **da nuvem**. Checar `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` antes de qualquer toque.
8. **Apagar sem liberar espaço.** Se o bloco está referenciado por uma Volume Shadow Copy, a exclusão não devolve nada. Verificar `GetDiskFreeSpaceEx` depois e explicar ao usuário.
9. **`MAX_PATH`.** Caminhos acima de 260 caracteres exigem prefixo `\\?\` em **toda** chamada Win32, inclusive nas de exclusão. Falhar aqui é falhar exatamente nos casos mais profundos (`node_modules`).
10. **O player segurando o arquivo.** Se o preview mantém o handle aberto, a exclusão falha com "arquivo em uso" — pelo próprio app. Fechar o handle ao trocar de seleção (F6.3).
11. **`LastAccessTime` desabilitado.** O Windows desliga a atualização de último acesso por padrão (`NtfsDisableLastAccessUpdate`). Regras baseadas em "não acessado há N dias" precisam detectar isso e cair para `LastWriteTime`, avisando na UI.
12. **Cota da Lixeira.** Arquivos maiores que a cota são apagados de vez mesmo com `FOF_ALLOWUNDO`. Avisar antes, não depois.
13. **Shred em SSD é teatro.** Wear leveling faz a sobrescrita não atingir os blocos originais. Dizer isso ao usuário em vez de vender segurança falsa.
14. **`Dispatcher.Invoke` por item.** Reportar progresso arquivo por arquivo trava a UI mais que a varredura em si. Throttle obrigatório.
15. **`FILE_FLAG_NO_BUFFERING` exige alinhamento** de buffer, offset e tamanho ao setor. Desalinhado, falha com erro genérico difícil de diagnosticar.
16. **ReFS/exFAT não têm MFT.** Detectar o filesystem antes de escolher a estratégia; nunca assumir NTFS.
17. **`SHFILEOPSTRUCT.pFrom` é uma lista com terminador duplo,** não uma string. Um terminador só trunca o lote em silêncio.
18. **`Path.GetFullPath("C:")` devolve o diretório ATUAL daquela unidade,** não a raiz — especificação de unidade sem separador é relativa à unidade. Como alvo de exclusão isso é uma armadilha, então `C:` é lido como raiz de volume e recusado.
19. **`ListView.SelectedItems` não é propriedade de dependência bindável.** A seleção múltipla tem que ser empurrada ao ViewModel pelo code-behind.
20. **Recurso chamado `Strings.en-US.json` vira assembly satélite** (ver §8.9 e o README).
21. **Configuração comentada volta ao default interno.** Comentar `THREADS_HASH` não significa "sem limite" — significa `auto`. Documentar isso na cara do arquivo.

---

## 18. Métricas de sucesso

| Métrica | Meta v1.0 |
|---|---|
| Espaço liberado na primeira sessão, máquina típica | ≥ 20 GB |
| Tempo do início até a primeira sugestão útil | ≤ 15 s |
| Cliques do abrir o app até liberar espaço (Limpeza Rápida) | ≤ 3 |
| Incidentes de "apaguei algo que não devia e não recuperei" | **0** |
| Taxa de restauração bem-sucedida na quarentena | 100 % |
| Falsos positivos em duplicados exatos | 0 |
| Divergência de tamanho total vs WizTree | ≤ 0,5 % |
| Crashes por 100 sessões | ≤ 1 |
| Cobertura de testes em `Vacuon.Core` | ≥ 75 % (100 % em `Safety/` e `Actions/`) |

---

## Anexo A — Catálogo de regras de limpeza

Estrutura de categorias e itens do `rules/catalog.json`. Cada entrada carrega risco, condições e ganho típico. 🟢 SEGURO · 🟡 ATENÇÃO · 🟠 PERIGOSO.

### A.1 Windows — temporários e sistema

| Regra | Caminho / método | Risco | Ganho típico |
|---|---|---|---|
| Temp do usuário | `%TEMP%\**` (idade > 1 d) | 🟢 | 0,5–60 GB |
| Temp do Windows | `%WINDIR%\Temp\**` | 🟢 | 0,2–10 GB |
| Cache do Windows Update | `%WINDIR%\SoftwareDistribution\Download\**` | 🟢 | 1–15 GB |
| Delivery Optimization | `%WINDIR%\SoftwareDistribution\DeliveryOptimization\**` | 🟢 | 0,5–20 GB |
| Limpeza de componentes (WinSxS) | `DISM /Online /Cleanup-Image /StartComponentCleanup` | 🟡 | 2–10 GB |
| `Windows.old` | pasta inteira (via `cleanmgr` API) | 🟡 | 8–40 GB |
| Prefetch | `%WINDIR%\Prefetch\**` | 🟡 | 0,05–0,5 GB |
| Cache de fontes | `%WINDIR%\ServiceProfiles\LocalService\AppData\Local\FontCache\**` | 🟢 | < 0,2 GB |
| Cache de ícones/thumbnails | `%LOCALAPPDATA%\Microsoft\Windows\Explorer\*.db` | 🟢 | 0,1–2 GB |
| Dumps de memória | `%WINDIR%\MEMORY.DMP`, `Minidump\**`, `**\*.mdmp` | 🟢 | 0,5–16 GB |
| Relatórios de erro (WER) | `%LOCALAPPDATA%\Microsoft\Windows\WER\**`, `%PROGRAMDATA%\Microsoft\Windows\WER\**` | 🟢 | 0,1–3 GB |
| Logs CBS/DISM | `%WINDIR%\Logs\CBS\**`, `Logs\DISM\**` | 🟢 | 0,1–2 GB |
| Logs de evento antigos | `%WINDIR%\System32\winevt\Logs\*.evtx` (rotação) | 🟡 | 0,1–1 GB |
| Rastreamentos ETL | `**\*.etl` (idade > 30 d) | 🟢 | 0,1–5 GB |
| Lixeira | `SHEmptyRecycleBin` por volume | 🟡 | variável |
| Arquivos de otimização de entrega P2P | `%WINDIR%\SoftwareDistribution\**` | 🟢 | 0,5–20 GB |
| Hibernação | `powercfg /h off` | 🟠 | = 40 % da RAM |
| Pontos de restauração antigos | `vssadmin delete shadows /for=C: /oldest` | 🟠 | 1–30 GB |
| Cache de instalação do .NET | `%WINDIR%\Microsoft.NET\**\Temporary ASP.NET Files\**` | 🟢 | < 0,5 GB |
| Instaladores órfãos do Installer | `%WINDIR%\Installer\**` (só órfãos comprovados) | 🟠 | 1–10 GB |
| Cache do Windows Defender | `%PROGRAMDATA%\Microsoft\Windows Defender\Scans\History\**` | 🟢 | 0,1–2 GB |
| Cache de shader do DirectX | `%LOCALAPPDATA%\D3DSCache\**` | 🟢 | 0,1–3 GB |
| Arquivos de linguagem não usados | pacotes de idioma via `lpksetup` | 🟡 | 0,5–3 GB |
| Cache de miniaturas de vídeo | `%LOCALAPPDATA%\Microsoft\Windows\Caches\**` | 🟢 | < 0,5 GB |
| Downloads do Windows Store | `%LOCALAPPDATA%\Packages\*\LocalCache\**` | 🟡 | 0,2–5 GB |
| Cache de sincronização de nuvem (arquivos já sincronizados) | `%LOCALAPPDATA%\Microsoft\OneDrive\cache\**` | 🟢 | 0,1–2 GB |
| Arquivos `chkdsk` recuperados | `<vol>\found.*\**`, `*.chk` | 🟡 | variável |

### A.2 Navegadores

Para cada um de **Chrome, Edge, Brave, Opera, Vivaldi, Firefox, Chromium**, por perfil:

| Regra | Risco |
|---|---|
| Cache HTTP (`Cache`, `Code Cache`, `GPUCache`, `ShaderCache`, `Service Worker\CacheStorage`) | 🟢 |
| Cache de mídia (`Media Cache`) | 🟢 |
| Downloads antigos da pasta de download do perfil | 🟡 |
| Histórico de navegação | 🟡 |
| Cookies e dados de site (**derruba logins**) | 🟠 |
| Sessões antigas, crash reports, `heavy_ad_intervention` | 🟢 |
| Versões antigas do próprio navegador (`Application\<versão antiga>`) | 🟢 |
| Extensões desinstaladas com dados remanescentes | 🟡 |

### A.3 Aplicativos e comunicação

`Discord` (Cache/Code Cache/GPUCache + versões antigas) 🟢 · `Slack` 🟢 · `Microsoft Teams` (cache clássico e novo) 🟢 · `Telegram Desktop` (cache de mídia) 🟡 · `WhatsApp Desktop` 🟡 · `Zoom` (gravações locais → 🟠, cache → 🟢) · `Spotify` (`Storage`, `Data`) 🟢 · `Steam` (`shadercache`, `depotcache`, `downloading`, `htmlcache`) 🟢 · `Epic Games` (`webcache`) 🟢 · `Battle.net` 🟢 · `Origin/EA` 🟢 · `Riot Client` 🟢 · `Ubisoft Connect` 🟢 · `iTunes/Apple Devices` (backups de iPhone → 🟠) · `Adobe` (Media Cache, Common\Media Cache Files, Camera Raw cache, After Effects Disk Cache) 🟢 · `DaVinci Resolve` (CacheClip, ProxyMedia) 🟡 · `CapCut` (cache de projeto e proxies) 🟡 · `OBS` (gravações órfãs) 🟠 · `Office` (cache de documento, `~$*`, Office Document Cache) 🟢 · `Thunderbird` (cache) 🟢 · `Java` (Deployment cache) 🟢 · `Unity` (Library, Temp de projetos) 🟡 · `Unreal` (`DerivedDataCache`, `Intermediate`, `Saved`) 🟡 · `Blender` (cache de render) 🟢 · `VLC/MPV` (thumbnails, art cache) 🟢

### A.4 Desenvolvimento

| Regra | Alvo | Risco |
|---|---|---|
| `node_modules` de projetos inativos | `**\node_modules` (projeto sem alteração > 90 d) | 🟡 |
| Cache do npm / yarn / pnpm | `%APPDATA%\npm-cache`, `%LOCALAPPDATA%\Yarn\Cache`, store do pnpm | 🟢 |
| Cache do pip / venv órfãos | `%LOCALAPPDATA%\pip\Cache`, `**\.venv` de projetos inativos | 🟢/🟡 |
| `__pycache__`, `*.pyc` | `**\__pycache__` | 🟢 |
| Cache do cargo | `%USERPROFILE%\.cargo\registry\cache`, `**\target` | 🟢/🟡 |
| Pacotes NuGet | `%USERPROFILE%\.nuget\packages` (não referenciados) | 🟢 |
| Cache do Gradle / Maven | `%USERPROFILE%\.gradle\caches`, `.m2\repository` | 🟢 |
| Saídas de build | `**\bin`, `**\obj`, `**\dist`, `**\build`, `**\.next`, `**\out` | 🟡 |
| Cache do Visual Studio | `.vs\**`, `%LOCALAPPDATA%\Microsoft\VisualStudio\**\ComponentModelCache` | 🟢 |
| Extensões e cache do VS Code | `%APPDATA%\Code\Cache*`, `CachedExtensions`, `logs` | 🟢 |
| Imagens e volumes Docker não usados | `docker system prune` (nunca automático) | 🟠 |
| Disco virtual do WSL2 inflado | `Optimize-VHD` / `diskpart compact vdisk` | 🟡 |
| Objetos soltos do Git | `git gc` em repositórios locais | 🟡 |
| Cache do Android SDK / Gradle / emulador | `%LOCALAPPDATA%\Android\Sdk\.temp`, AVDs órfãos | 🟡 |
| Cache do Conda | `conda clean --all` | 🟢 |
| Modelos de IA baixados e não usados | `%USERPROFILE%\.cache\huggingface`, `~\.ollama\models` (idade > 180 d) | 🟠 |

### A.5 Detectores genéricos (não são caminhos fixos, são padrões)

Arquivos 0 byte · pastas recursivamente vazias · atalhos quebrados · `*.tmp`/`*.temp`/`*.old`/`*.bak`/`*.gid`/`*.chk`/`*.~*` · downloads incompletos (`*.part`, `*.crdownload`, `*.!ut`, `*.opdownload`) · `Thumbs.db`/`desktop.ini`/`.DS_Store`/`ehthumbs.db` · duplicados exatos · near-duplicates de mídia · instaladores em pasta de download com idade > N dias · resíduos de programas desinstalados · arquivos gigantes nunca acessados · logs acima de tamanho · árvores de pasta duplicadas · candidatos a compressão NTFS.

---

## Anexo B — Estrutura do projeto

```
Vacuon/
├─ PRD.md                      ← este documento
├─ README.md
├─ Vacuon.sln
├─ src/
│  ├─ Vacuon.Native/           P/Invoke, structs Win32, safe handles
│  │  ├─ Interop/              Kernel32.cs Shell32.cs Ole32.cs Rstrtmgr.cs
│  │  └─ Ntfs/                 BootSector.cs MftRecord.cs AttributeParser.cs
│  ├─ Vacuon.Core/
│  │  ├─ Scan/                 ScanOrchestrator MftReader UsnEnumerator
│  │  │                        Win32Walker PathResolver VolumeProbe
│  │  ├─ Index/                FileIndex NameBlob SnapshotWriter SnapshotReader
│  │  ├─ Analyzers/            SizeTree Duplicates NearDuplicates AgeHeatmap
│  │  │                        EmptyItems BrokenLinks SlackSpace Extensions
│  │  ├─ Rules/                RuleEngine RuleCatalog RiskClassifier Conditions
│  │  ├─ Actions/              Quarantine RecycleBin HardDelete Shred
│  │  │                        Compress Relocate HardlinkDedupe ActionQueue
│  │  ├─ Preview/              ShellThumbnail ShellIcon MediaProbe TextPeek
│  │  ├─ Safety/               ProtectedPaths LockedFileResolver RestorePoint
│  │  └─ Infra/                Config Log Metrics Elevation BufferPools
│  ├─ Vacuon.App/              WPF
│  │  ├─ Views/                Dashboard Explorer Treemap Cleanup Duplicates
│  │  │                        Media Quarantine Reports Settings
│  │  ├─ ViewModels/
│  │  ├─ Controls/             TreemapCanvas VirtualFileGrid MediaPlayerPane
│  │  │                        SizeBar RiskBadge
│  │  ├─ Themes/               Dark.xaml Light.xaml Tokens.xaml
│  │  └─ Resources/            i18n/pt-BR.resx i18n/en-US.resx
│  └─ Vacuon.Cli/              subcomandos scan/dupes/clean/find/quarantine
├─ rules/
│  ├─ catalog.json             catálogo oficial (versionado)
│  └─ user.json                regras do usuário (nunca sobrescrito)
├─ tests/
│  ├─ Vacuon.Core.Tests/
│  ├─ Vacuon.Safety.Tests/     ← 100 % de cobertura obrigatória
│  └─ fixtures/                imagens VHD sintéticas com casos de borda
│                              (MFT fragmentada, hardlinks, ADS, path > 260)
├─ docs/
│  ├─ rules/                   uma página por regra ("saiba mais")
│  ├─ architecture.md
│  └─ mft-format.md
├─ build/                      publish single-file, assinatura, instalador
└─ config.ini
```

---

## Anexo C — Glossário

| Termo | Significado |
|---|---|
| **MFT** (Master File Table) | Índice mestre do NTFS; um registro de 1024 B por arquivo/diretório, contendo nome, tamanho, timestamps e localização dos dados |
| **FRN** (File Reference Number) | Identificador único e estável de um arquivo no volume; sobrevive a renomeações |
| **USN Journal** | Diário de alterações do NTFS; permite saber o que mudou desde um ponto sem revarrer o disco |
| **ADS** (Alternate Data Stream) | Fluxo de dados extra anexado a um arquivo; invisível no Explorer, ocupa espaço real |
| **Reparse point** | Junction, symlink ou placeholder de nuvem; um "atalho" no nível do filesystem |
| **Slack space** | Desperdício entre o tamanho lógico do arquivo e o múltiplo de cluster que ele ocupa |
| **Hardlink** | Dois ou mais caminhos apontando para o mesmo conteúdo; apagar um não libera espaço |
| **Shadow Copy** | Cópia de sombra do volume (pontos de restauração); pode segurar blocos de arquivos já apagados |
| **Squarified treemap** | Algoritmo de treemap que gera retângulos próximos ao quadrado, mais legíveis que o slice-and-dice clássico |
| **pHash / dHash** | Hashes perceptuais: geram assinaturas parecidas para imagens visualmente parecidas |
| **BLAKE3** | Função de hash criptográfico moderna, paralelizável, rápida o bastante para saturar um SSD |
| **Dry-run** | Execução simulada que mostra exatamente o que aconteceria, sem alterar nada |

---

*Fim do documento. Próximo passo: M0 — criar a solução .NET e o esqueleto de `Vacuon.Core`/`Vacuon.Native`, com `MftReader` guiado por testes sobre imagens VHD sintéticas.*
