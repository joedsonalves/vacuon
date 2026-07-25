# Política de segurança

## Versões suportadas

O Vacuon está em desenvolvimento inicial (v0.x). Correções de segurança vão apenas para a versão mais recente.

## Reportando uma vulnerabilidade

Use o [Security Advisories](../../security/advisories/new) do GitHub — **não** abra uma issue pública.

Descreva o impacto, o caminho de reprodução e a versão (`vacuon version`). A resposta vem em até 7 dias.

## Superfície de risco deste projeto

O Vacuon faz três coisas que merecem atenção de quem audita o código:

### 1. Abre o volume em modo bruto (`\\.\C:`)

`Vacuon.Native/Interop/VolumeDevice.cs` abre o dispositivo com `GENERIC_READ` e **apenas leitura** — nunca `GENERIC_WRITE`. O handle serve para ler a MFT e nada mais. Exige privilégio de Administrador; sem ele, o app cai para a travessia pela API do Windows.

### 2. Lê chaves do registro associadas a persistência de malware

`Vacuon.Core/Security/RegistryPersistenceScanner.cs` abre todas as chaves com `writable: false`. O módulo **não altera, não apaga e não desabilita** nada — ele lista e explica. Qualquer PR que introduza escrita no registro precisa passar pelo modelo de reversibilidade descrito no PRD.

### 3. (A partir do M4) apaga arquivos

Ainda não implementado. Quando for: quarentena reversível com manifesto é o caminho padrão, exclusão permanente é um segundo passo explícito, e a lista de proteção absoluta (PRD §9.2) não tem flag que a contorne.

## O que o Vacuon deliberadamente NÃO faz

- Não envia nada para lugar nenhum. Sem servidor, sem conta, sem telemetria, sem verificação de atualização automática.
- Não executa nada que encontra. Autoruns suspeitos são exibidos como texto, nunca invocados.
- Não hidrata placeholders de nuvem. Ler um arquivo do OneDrive o baixaria; o scanner checa `FILE_ATTRIBUTE_RECALL_ON_*` e passa longe.
- Não é antivírus. Não há base de assinaturas nem quarentena de ameaça. As heurísticas apontam padrões, com o motivo sempre visível, para o usuário decidir.

## Falsos positivos

Um falso positivo no módulo de segurança é tratado como **defeito**, não como ruído aceitável. Se o Vacuon marcar algo legítimo, abra uma issue com a linha completa do relatório — ela vira caso de teste.
