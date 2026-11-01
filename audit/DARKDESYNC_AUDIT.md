# DarkDesync — auditoria 2026-11-01

## Escopo e limites

Somente `/home/deploy/darkdesync`, host autorizado `arthurmuller`, GitHub `darkgoed/darkdesync`. Base `470d34f7f43977f2d581ea07c229ae4aaf7a3b8f`, branch `audit/fix-darkdesync-20261101`. Sem clones, agentes adicionais, HCC, outros projetos, tráfego de terceiros ou bypass de anticheat. Este host é Linux sem dotnet no PATH; execução WPF/WinDivert é feita exclusivamente nos runners Windows isolados.

Roblox Player REAL não foi executado. Não há conclusão de compatibilidade com Roblox/anticheat, nem equivalência de desempenho com clumsy. A semântica de captura foi conferida na documentação WinDivert 2.2.2 vendorizada (`doc/WinDivert.html`, seções WINDIVERT_ADDRESS, WinDivertRecv e WinDivertSend): Recv lê pacotes interceptados; Outbound determina direção. GLOBAL abre o filtro literal `outbound`. Strict permanece IPv4/PID/tupla completa e não amplia filtro se não houver identidade comprovada.

## Bugs concretos e correções

| ID | Prioridade | Evidência na base | Correção / regressão |
| --- | --- | --- | --- |
| A01 | P1 | Latest `global-delay-ms` aponta a `b8f73f1a7d725f2e779f7f02843dc9ff4a0abe07`, anterior ao PR #6; confirmado por `gh release view` | Novo pacote deve vir do run verde do merge main, não de release/artefato antigo |
| A02 | P1 | GLOBAL com DiagnosticMode.None chegava ao branch legado, com drop 100% padrão; flags de perfil/throttle interferiam no significado de Desativado | None GLOBAL passa sem efeitos; novo PacketDrop/“Drop (%)” explícito; persistência e flags testadas |
| A03 | P1 | GlobalDelayMs alterava somente valor para novos pacotes; fila FIFO mantinha cabeçalho antigo de 3000ms diante de prazo novo de 50ms | Slider recalcula todos os prazos pendentes desde captura original, mantendo FIFO; 0 libera fila |
| A04 | P1 | TryReleaseNext somava ThrottleReleaseDelayMs inclusive no Lag, produzindo espaçamento indevido e backlog | Lag não soma espaçamento; teste com throttle 200ms e pacotes vencendo juntos |
| A05 | P1 | Fila cheia passava o pacote novo antes dos 512 retidos: reordenação em Lag/throttle; Freeze excedente passava mantendo retenção indefinida | Lag/throttle libera o mais antigo antecipadamente; Freeze faz STOP de segurança + flush FIFO |
| A06 | P1 | CancelCycle de controles de perfil/throttle forçava Idle durante GLOBAL contínuo sem fechar o handle; efeito deixava de atuar apesar de START | Controles legados não cancelam GLOBAL; efeito/filtro ainda param e fecham com segurança; regressão WPF |
| A07 | P2 | Aplicar settings definia efeito antes do filtro; selecionar GLOBAL podia converter None em Lag dependendo do modo anterior | Restaura filtro primeiro, depois efeito explícito; carregar repetidamente é estável e nunca inicia captura |
| A08 | P2 | Branch global de defesa para addr.Outbound=false também liberava a fila de saída | Inbound inesperado passa isoladamente sem antecipar a fila outbound; direção real inbound é sintética porque loopback é reportado outbound |
| A09 | P1 | Falha de reinjeção encerrava apenas GLOBAL; erro global podia ser ocultado por PublishGlobalStatus | Falha encerra também strict; motivo preservado; falhas contam perda, nunca sucesso fictício |
| A10 | P2 | STOP não limpava flags transitórias IsDropActive/IsThrottleActive; safety-stop assíncrono podia atingir geração posterior | STOP limpa flags; callback compara geração antes de encerrar captura |
| A11 | P2 | LatencyDelay aparecia na UI estrita mas não era mapeado em ExecuteTriggerCycle | Pulso estrito mapeia Lag explicitamente; continua sujeito a duração 0–800ms e identidade por tupla |
| A12 | P2 | Release loop podia executar sem pausa enquanto sempre houvesse pacote vencido; exceção era apenas logada | Lotes máximos de 64 + espera cancelável de 4ms; exceção solicita STOP de segurança |
| A13 | P2 | API ActiveDiagnosticMode não liberava a fila do efeito anterior (Freeze→Lag mantinha prazo infinito) | Troca atômica sob packet lock libera a fila anterior uma vez e reseta o relógio de ordering |

## Matriz de cobertura

| Área | Cobertura / evidência | Não coberto / ressalva |
| --- | --- | --- |
| Lag 0/50/150/3000ms, deadline, FIFO, slider baixa/sobe/zero, espaçamento | AuditRegressionTests com relógio monotônico injetado; testes legados; driver UDP real 3000→50/0, ordem e STOP | Sem garantia hard real-time sob carga do Windows |
| Overflow 512 e ordem | Testes determinísticos Lag/throttle com 513 pacotes; Freeze falha aberto, fecha e libera uma vez | Sem benchmark de saturação WAN/Roblox |
| Freeze, Drop, Burst/Asymmetric, Disabled e flags | Testes determinísticos + driver local; enum PacketDrop separado de None | Não garante que jogos tolerem retenção/perda |
| START contínuo >800ms / STOP / modo / efeito / falha / Dispose | Driver global existente e novos cenários; WPF usa estado modelado explicitamente sem abrir driver | Teclado físico e jogo não testados |
| WPF slider, visibilidade, binding, START/STOP, settings | Smoke Windows/STA ampliado, erros de binding causam falha | Não é teste manual de todas as resoluções/DPI |
| Strict TCP/UDP, PID dinâmico, FLOW, filtros, ambiguidade, fragmentos | Suítes existentes de isolamento, filtros e driver; novos efeitos não autorizam UDP/tupla/fragmento desconhecidos | Strict IPv6 não implementado; falha fechado |
| Locks/shutdown | Revisão de lifecycle→packet→handle; captura não pede lifecycle sincronicamente; shutdown de recv, join, drain, flush e close antes de nova época; regressões existentes | Não é prova formal de ausência de deadlock nem stress de horas |
| CPU / fairness | Release loop sem busy-spin, lotes de 64/4ms; suíte de regressão de desempenho existente | Sem comparação quantitativa com clumsy ou benchmark de CPU em Roblox |
| Segurança / dependências | Revisão de escopo/filtro e gate NuGet de vulnerabilidades; driver preservado, sem alteração de acesso/infra | Não é pentest nem certificação do driver |
| Release / backup / rollback | Base e release anterior preservadas; ZIP deve conter exe/dll/sys, SOURCE_COMMIT e SHA256; validação de main e driver antes da publicação | Repo PRIVATE: publicação GitHub não torna download público; visibilidade não será alterada |

## Contrato de fila e falhas

Atraso nominal = captura + slider atual; alteração ao vivo afeta também pendentes. FIFO é mantido na fila/época, inclusive na saturação; não é garantia de ordem total com tráfego novo que passa fora do handle durante turnover/STOP. Saturação Lag/throttle pode encurtar o atraso do pacote mais antigo, sem drop intencional. Freeze não permite crescimento ilimitado: ao atingir limite de retenção, para e tenta reinjetar tudo. Pacotes recusados pelo envio não podem ser recuperados por close/flush; são contados como perda e a captura é parada. Contadores por protocolo de drops dentro do processamento não substituem o total de perdas: falhas de envio assíncrono da fila entram no total. STOP limpa flags transitórias, drena o driver e a fila antes de fechar o handle. O ciclo estrito pode abreviar retenção ao terminar/trocar escopo; GLOBAL contínuo não depende do timer de 800ms.

## Execução e release

Histórico real antes do candidato final: run `41289816874` executou driver com sucesso, mas unit detectou expectativa antiga de overflow Freeze (modelo não representava captura ativa). Teste passou a exigir STOP real modelado e flush, não pass-through indefinido. Run `41289969036` teve 128/128 unit e 23/23 driver, sem skips; WPF detectou modelo de teste incompleto (IsCaptureRequested/UI running não definidos). O modelo WPF foi corrigido explicitamente; isso não representa teste nativo da UI em Roblox. Nenhum desses runs vermelhos é elegível para release.

Validação real do código candidato `ef13e24527602b891c88c8b2bde1b2e68a5ca14d`: [workflow_dispatch 41290149611](https://github.com/darkgoed/darkdesync/actions/runs/41290149611), `driver_tests=true`, `legacy_global_tests=true`, ambos os jobs **success**. TRX baixados e parseados: **129/129** unit/regression, **23/23** driver, **zero falhas e zero skips**. Smoke WPF/STA, build, NuGet security gate, publish e verificação do payload também verdes. No UDP real do runner: ordem 0..15 preservada; redução 3000→50ms concluiu em 68ms e 3000→0ms em 1ms nesse run (medições locais pontuais, não benchmark/garantia). Snapshot dos resultados e nomes dos casos em [evidence/validation-ef13e24.json](evidence/validation-ef13e24.json), extraído da API/artefatos, não inventado.

PR: https://github.com/darkgoed/darkdesync/pull/7. A revisão final é feita nesta mesma sessão, sem segundo agente: mudanças limitadas à fila, semântica de efeitos, lifecycle, WPF/settings, testes, CI e documentação; preservadas identidade estrita, chamadas nativas, hotkey pass-through e binários WinDivert. Sem P0/P1 conhecido remanescente no escopo corrigido; limites não exercitados estão explicitados na matriz. `git diff --check` limpo; nenhum bin/obj/TestResults rastreado. O gate NuGet não acusou dependências vulneráveis; não foi executado Semgrep/pentest, portanto não há alegação de cobertura desses scanners.

Gate de publicação: repetir CI e driver no exato merge main; baixar exclusivamente `DarkDesync-win-x64` do run verde de main, validar exe/dll/sys e `SOURCE_COMMIT.txt`, criar ZIP/SHA256 e tag única nesse merge. Os IDs finais de main, SHA do merge e SHA256 do ZIP ficam nas notas da release e no relatório final da missão (não são adivinhados antecipadamente neste documento). Qualquer gate vermelho impede merge/release. A release anterior permanece disponível para rollback; o novo pacote não pode reutilizar o artefato de `b8f73f1`.

Rollback: manter `global-delay-ms` e base Git intactos; se necessário, retirar a nova marca Latest e selecionar a release anterior (que tem limitações conhecidas), ou criar revert revisado e passar os mesmos gates. Sem alterações de dados, migrações, serviços ou hosts externos. Nunca executar os testes GLOBAL nesta máquina compartilhada; somente VM Windows efêmera do Actions.
