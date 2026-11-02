# Issue #21 — oito efeitos Outbound (02/11/2026)

## Escopo e inspeção (Ponytail FULL, revisão pelo agente principal)

Base limpa verificada: main 53c9c6a. Branch feat/outbound-eight-effects-20261102, PR https://github.com/darkgoed/darkdesync/pull/22, issue https://github.com/darkgoed/darkdesync/issues/21. Um agente principal, sem delegação/conta alternativa; nenhum projeto externo modificado.

Inspecionados: DiagnosticMode/ModeCatalog e usos em settings/VM; PacketEngine captura, autorização, packet/lifecycle/handle locks, timer/deadline, capture epoch, STOP/flush/send e safety-stop; PulseShaper e ScopedPulseScheduler; controller; WPF XAML/bindings/seleção; testes de regressão, nativos e UI; workflow Windows; manifestos dos três projetos e pacote WinDivert. Revisão do diff completo focada em correção, limites, ordem, regressão, segurança e custo de execução. Sem mudança de workflow para pular/reduzir gates.

Não inspecionados integralmente: implementação interna do driver WinDivert de terceiros, Windows kernel/WFP, código-fonte .NET/WPF e dependências, serviços externos, HCC e outros projetos. Não há auditoria independente por outro agente; esta é revisão completa pelo agente principal, não parecer de terceiro.

## Implementação e invariantes

12 selecionáveis: quatro favoritos existentes mais oito novos. Packet Size-Aware fora da UI, consentimento específico removido. Aviso Outbound GLOBAL permanece. Enum 0–22 preservado; novos IDs 23–30. Parser legado e testes PacketSizeAware mantidos. Nenhum arquivo de settings é salvo por load/migração; modo removido exige seleção explícita, mantém arquivo e bloqueia START. Duração padrão 200 ms e salva preservadas; ranking pessoal 250 ms não força duração.

OutboundPulseScheduler implementa algoritmos distintos, detalhados no README. Slot impõe janela/cadência/bytes/pacotes; sawtooth rampa e reset; AR(1) por fluxo/época com FIFO; hold/dreno progressivo; Pareto alpha 1,4 capped; taxa proporcional à EMA da ocupação; gap UDP intra-flow de um sucessor com TTL; GE com estado Good/Bad e probabilidades distintas de transição/descarte. Enqueue/Dequeue reais decidem retenção, reinjeção ou não-envio intencional no PacketEngine. Não são rótulos/contadores simulando efeito.

Clock Stopwatch monotônico, seed fixável, sem timer/task por pacote. Até 512 pacotes / 786432 bytes / 128 fluxos / 1536 bytes por pacote. RR com no máximo 128 visitas por dequeue, worker existente com turnos até 64 envios. Mesmo packet lock do receive/release/flush, antes do handle lock. STOP drena enquanto handle ainda pode enviar, cancela geração/timer, une workers, fecha handle; restart não usa filas antigas. Saturação cancela pulso e drena antes do pacote atual (fail-open). Payload/checksum/endereço nativo copiados sem mutação.

Suporte honesto: oito novos atuam em IPv4 TCP/UDP válido não fragmentado; Gap apenas UDP, TCP passa livre. IPv6/extensões, ICMP, fragmentos, impostors e cabeçalhos não identificáveis passam sem efeito. Filtro permanece outbound, não exclui protocolos escondido. GE causa perda intencional inclusive TCP e pode causar retransmissão, avisado na UI. Só há pulso explícito, 0–800 ms, START apenas arma. Deadline/STOP têm prioridade sobre pacing: backlog final pode sair junto, Windows não é tempo real e fila kernel/driver pode perder. Contadores de GE e falha de envio distintos.

## Validação reproduzível e gates

Build cross-target Linux executado de verdade; WPF/WinDivert não executados no Linux. Windows workflow existente compila WPF e testa em windows-2022, sem alterar semântica. Dispatch habilita explicitamente driver_tests=true e legacy_global_tests=true na VM efêmera isolada. Testes nativos geram apenas sockets loopback próprios, inclusive sink em processo filho que reporta payload exato via stdout (sem eco duplamente afetado).

Testes acrescentados: seeds reproduzíveis e oito assinaturas distintas; caps/cadência de slot; rampa/reset sawtooth; hold e dreno sem catch-up; AR(1) por época e correlação estatística; Pareto com cauda/quantis/TTL; EMA/ocupação observável e fairness; gap exato e singleton com timeout, sem TCP reorder; GE com correlação estatística e counters; 0/800 ms, direção, TCP/UDP e pass-through IPv6/ICMP/fragmentos; overflow/limite de fluxos; payload e endereço preservados; STOP/restart/dispose, corrida capture/release/STOP sem duplicação ou stale packet. Regressões dos favoritos e PacketSizeAware continuam na suíte.

WPF UIA seleciona cada efeito, START sem PID, ARMADO, hotkey subscriber/Space roteado e Pulso manual, PULSO ATIVO, retorno temporizado e STOP. Verifica aviso GLOBAL, settings antigos, inexistência do checkbox, falhas Win32 2/5/87/577 por seam de erro explícito (não falsos handles de sucesso). Teste adicional usa token Windows LUA restrito para negar WinDivertOpen sem administrador, não apenas mock. Fluxo TCP real durante pulso Gap deve passar.

Primeira rodada detectou expectativas de testes antigas (9 em vez de 17 modos no harness legado) e sender override substituído pelo helper de restart. Corrigidas as expectativas/helper, não removidas as asserções de entrega. Evidências posteriores e gates finais são registrados na release e no comentário de publicação da issue. Nada é considerado entregue antes de PR e main verdes, driver e WPF, pacote e read-back da release.

## Riscos e limitações

Não testados: PC do usuário, teclado físico, interface física de rede, tráfego de terceiros, carga adversa longa, Roblox/anticheat. Evento hotkey simulado não é prova de pulo físico/jogo. Sem promessa de exploit/efeito em jogo, zero perda ou precisão hard-real-time. Escopo GLOBAL intencionalmente afeta qualquer app durante pulso; usar só laboratório autorizado. Counters de reordenação são decisões intencionais, não garantia de ordem end-to-end. ZIP framework-dependent requer .NET 8 Desktop Runtime x64/admin e DLL/SYS lado a lado.

Jev advisory: script scripts/jev-gate.py não existe neste repo e busca nominal no diretório Hermes não o encontrou; não inventar selo Jev aprovado. Gates reais usados: revisão de diff, build, regressões, auditoria de dependências CI, driver e WPF. Semgrep não disponível no PATH inicial; ausência não é scan aprovado.

## Referências e rollback

https://man7.org/linux/man-pages/man8/tc-netem.8.html (slot, Pareto, correlation, gap, gemodel) obtido com curl. https://reqrypt.org/windivert-doc.html: backend de extração indisponível e DNS do host falhou; documentação WinDivert vendorizada no repositório permanece a referência offline para Recv/Send/endereço/reinjeção.

Rollback: preservar release/tag v1.0.0-audit.6 e seu ZIP (não sobrescrever/remover). Não há migração irreversível de dados ou modificação de settings por load. Parar/fechar a versão nova e extrair .6 em pasta separada, preservando backup de settings antes de qualquer Salvar. Reverter código exige PR normal e mesmos gates, sem rewrite de main/branch protection. Publicar .7 (ou próximo disponível) só do SHA final de main verde, com SOURCE_COMMIT e SHA256 do ZIP; guardar TRX e evidência UI junto da release. Fechar issue somente após publicação e leitura de volta do asset/tag/SHA.
