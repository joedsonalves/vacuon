# Contribuindo com o Vacuon

Obrigado pelo interesse. Este documento é curto de propósito: são poucas regras, mas elas não são negociáveis.

## Como rodar

```bash
git clone https://github.com/joedsonalves/vacuon.git
cd vacuon
dotnet build Vacuon.sln -c Debug
dotnet test tests/Vacuon.Core.Tests
```

Requer o SDK do .NET 10 e Windows 10 21H2+. São **587 testes em cerca de 30 s**, e a suíte **não** exige elevação nem toca em disco real — o parser de MFT é exercitado contra registros sintéticos (`tests/Vacuon.Core.Tests/Fixtures/MftRecordBuilder.cs`), o que a torna determinística e executável em CI.

Alguns testes precisam de ferramenta externa (ffmpeg para vídeo, Python com Pillow e piexif para EXIF). Sem ela eles **se declaram pulados**, o que aparece na saída — nunca passam sem conferir nada. Um teste que passa vazio é um teste que parou de guardar.

**Mate o `Vacuon.exe` antes de compilar.** Ele trava as DLLs, e o build às vezes falha em silêncio — aí você roda código velho sem saber.

## As regras que importam

### 1. `Vacuon.Core` não referencia UI

Nunca. Nem `System.Windows`, nem `System.Drawing`, nem `PresentationCore`. A CLI, os testes e a GUI consomem exatamente a mesma superfície. Se você precisa de um bitmap, devolva pixels crus (`ThumbnailBitmap`), não um `BitmapSource`.

### 2. O app não afirma o que não mediu

Esta é a regra número um do produto. Se a estratégia de varredura não expõe `AllocatedSize`, a saída diz **"não medido"** — não repete o tamanho lógico nem imprime "desperdício: 0 B".

O mesmo vale para hardlinks (contam uma vez), junctions (não são atravessadas) e placeholders de nuvem (não são lidos). Um PR que faça o Vacuon exibir um número inventado é rejeitado mesmo que o código esteja impecável.

### 3. Falso positivo é bug

No módulo de segurança, uma lista que alarma sempre é uma lista que o usuário aprende a ignorar. Antes de adicionar uma heurística, responda: *quantas máquinas limpas isso marca?* Se a resposta for "a maioria", a heurística está errada — não o usuário.

Casos já resolvidos que servem de referência: binários do sistema são assinados por catálogo (não cobre assinatura embutida deles); `AppData\Local` é local legítimo de instalação; `rundll32` dentro de System32 é o Windows funcionando normalmente.

Toda heurística nova precisa de um teste **positivo** e um **negativo**.

### 4. Nada destrutivo sem reversão

Qualquer código em `Actions/` precisa: passar por dry-run, registrar no manifesto de quarentena e ter restauração testada. `Safety/` e `Actions/` exigem 100 % de cobertura de teste. Sem exceção.

### 5. Caminho quente é caminho quente

Em `Scan/`, `Index/` e no parser de MFT: sem LINQ, sem alocação por registro, sem `string` onde cabe `ReadOnlySpan<char>`. O `FileEntry` tem 64 bytes e existe um teste que falha se alguém mudar isso — se você precisa de um campo novo, considere uma tabela lateral (foi o que fizemos com os bytes de Alternate Data Stream).

## Estilo

Rode `dotnet format` antes de abrir o PR. O `.editorconfig` decide o resto.

**Comentários** explicam *por quê*, nunca *o quê*. Estes são bons:

```csharp
// Sem isto, os dois últimos bytes de CADA setor vêm com o valor do USN em vez
// do conteúdo real — o parse "quase funciona", que é pior do que falhar.

// AppData\Local ficou de fora de propósito: Chrome, Discord e Opera instalam
// ali. Sinalizar a pasta gera meia dúzia de alarmes falsos em toda máquina.
```

Este não:

```csharp
// Incrementa o contador
count++;
```

Nomes de tipos, métodos e variáveis: **em inglês**, sempre.

**Comentários novos também em inglês.** Os antigos estão em português e ficam onde estão — reescrevê-los em massa só produziria ruído no diff. A interface segue a mesma divisão: inglês é o padrão, português é opcional, e as chaves vivem em `src/Vacuon.Core/Localization/Strings.*.json`, com teste de paridade entre os dois arquivos.

## Abrindo uma issue

Para um **falso positivo** do módulo de segurança, inclua a linha completa do relatório e o que o item realmente é. É o tipo de issue mais útil que existe aqui.

Para um **erro de contagem**, diga o que outra ferramenta reportou (WizTree, TreeSize) e a diferença. A meta é ficar dentro de 0,5 %.

Para um **crash**, a versão (`vacuon version`), o comando e o stack trace.

## O que não vai ser aceito

Vale repetir os três principais:

- **"Limpeza de registro"** — ganho de espaço nulo, risco alto.
- **Tweaks de sistema, "otimizador de RAM", "PC Health Score"** — o Vacuon é um instrumento de medição, não um pacote de promessas.
- **Telemetria** — nenhum byte sai da máquina.
