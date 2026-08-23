# Política de segurança

## Versões suportadas

O Vacuon está em desenvolvimento inicial (v0.x). Correções de segurança vão apenas para a versão mais recente.

## Reportando uma vulnerabilidade

Use o [Security Advisories](../../security/advisories/new) do GitHub — **não** abra uma issue pública.

Descreva o impacto, o caminho de reprodução e a versão (`vacuon version`). A resposta vem em até 7 dias.

## Superfície de risco deste projeto

O Vacuon faz quatro coisas que merecem atenção de quem audita o código:

### 1. Abre o volume em modo bruto (`\\.\C:`)

`Vacuon.Native/Interop/VolumeDevice.cs` abre o dispositivo com `GENERIC_READ` e **apenas leitura** — nunca `GENERIC_WRITE`. O handle serve para ler a MFT e nada mais. Exige privilégio de Administrador; sem ele, o app cai para a travessia pela API do Windows.

### 2. Lê chaves do registro associadas a persistência de malware

`Vacuon.Core/Security/RegistryPersistenceScanner.cs` abre todas as chaves com `writable: false`. O módulo **não altera, não apaga e não desabilita** nada — ele lista e explica, e a interface e o CLI afirmam isso ("Vacuon changed no key").

**Mas o app, como um todo, deixou de ser somente leitura.** A seção **Otimizar** escreve no registro: `Optimization/AiComponentSwitch.cs` desliga componentes de IA da Microsoft e `Optimization/StartupPrograms.cs` desabilita entradas de inicialização pelo mesmo valor que o Gerenciador de Tarefas usa. São as **únicas duas** partes do app que escrevem, ficam atrás da mesma porta de propósito, e cada mudança é gravada em `Optimization/PolicyJournal.cs` **antes** de ser aplicada — desfazer restaura o valor anterior do diário, e não escreve um "ligado" chutado por cima. Toda escrita é lida de volta: "escrevi" e "está lá" são afirmações diferentes, e só a segunda aparece na tela.

### 3. Apaga arquivos, de três jeitos com consequências diferentes

Implementado. A **quarentena** (`Actions/QuarantineService.cs`) é o destino padrão: move para `<volume>\$Vacuon.Quarantine\<lote>\` no mesmo volume e devolve quando você pedir. O manifesto é escrito **antes** do primeiro rename — a falha que perde dado não é o lote pela metade, é um arquivo parado sem nada dizendo de onde veio. Quarentena **não libera espaço**, e o app diz `BytesHeld`, não `BytesFreed`.

A **Lixeira** e a **exclusão permanente** são cada uma uma escolha explícita à parte, e só a segunda é irreversível — junto com o expurgo de um lote da quarentena.

A lista de proteção absoluta é `Vacuon.Core/Safety/ProtectedPaths.cs` e **não tem flag, opção nem argumento que a contorne**. Todo item é conferido contra ela, não só a raiz do padrão. É por isso que as regras de limpeza que precisam mexer dentro de `%WINDIR%` chamam a ferramenta da Microsoft (DISM, powercfg, vssadmin) em vez de apagar arquivo na mão.

### 4. Cria tarefas agendadas do Windows

`Vacuon.Core/Scheduling/ScheduledCleanup.cs` chama `schtasks.exe` para o comando `schedule`. É opcional, só acontece se você pedir, e a tarefa é listável e removível pelo mesmo comando — ou pelo Agendador de Tarefas do Windows.

## O que o Vacuon deliberadamente NÃO faz

- Não envia nada para lugar nenhum. Sem servidor, sem conta, sem telemetria, sem verificação de atualização automática. Não há `HttpClient` nem `WebRequest` em nenhum dos três projetos — a única ocorrência de "webrequest" no código é uma *string* dentro de uma heurística que detecta comando suspeito.
- Não limpa RAM. O painel de Memória mostra o que está em uso e ordena por memória **privada**; o botão de trim reporta o que foi **movido**, nunca "liberado", e o número pode vir negativo quando o Windows traz as páginas de volta.
- Não executa nada que encontra. Autoruns suspeitos são exibidos como texto, nunca invocados.
- Não hidrata placeholders de nuvem. Ler um arquivo do OneDrive o baixaria; o scanner checa `FILE_ATTRIBUTE_RECALL_ON_*` e passa longe.
- Não é antivírus. Não há base de assinaturas nem quarentena de ameaça. As heurísticas apontam padrões, com o motivo sempre visível, para o usuário decidir.

## Falsos positivos

Um falso positivo no módulo de segurança é tratado como **defeito**, não como ruído aceitável. Se o Vacuon marcar algo legítimo, abra uma issue com a linha completa do relatório — ela vira caso de teste.
