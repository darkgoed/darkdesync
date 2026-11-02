# DarkDesync

Aplicativo Windows WPF C#/.NET 8 para ensaios de rede autorizados. Repositório privado canônico: darkgoed/darkdesync. Um único aplicativo em DarkDesync/; os outros dois projetos são testes. WinDivert 2.2.2 x64 incluído, com licenças originais.

## Uso atual — Outbound GLOBAL

O filtro é literalmente `outbound`: TODOS os aplicativos do Windows, sem PID ou seleção oculta de protocolo. O aviso fixo permanece visível.

1. Escolha um dos quatro favoritos ou sete técnicas e a duração: 0–800 ms, padrão 200 ms; 0 desativa.
2. START abre captura como administrador em ARMADO/pass-through, SEM efeito.
3. Espaço ou Pulso manual aplica UM pulso; ao terminar volta a ARMADO sem fechar o handle. Tecla mantida não repete; não existe ciclo automático. O hook não consome a tecla.
4. STOP cancela e libera pendências com handle ainda válido, encerra workers/captura e retorna a PARADO. Carregar/padrões e fechar também encerram captura. Sem START, Espaço não abre captura.

A avaliação pessoal “250 ms” só ordena favoritos: nunca redefine duração padrão ou salva.

### Estilos e pílulas de avaliação pessoal

O catálogo de dados `effect-assessments.json`, ao lado do EXE, guarda observações subjetivas por efeito e duração exata, não métricas objetivas. A 800 ms: Dual Token Bucket e Outbound Freeze = Agressivo/vermelho; Flow-Aware e Bandwidth = Equilibrado/âmbar. Freeze a 250 ms = Legit/verde; a 800 ms exibe “injogável nos testes pessoais”, sem bloquear seleção. Combinações sem dados ficam cinza “Não avaliado”. Pílulas aparecem no dropdown e no efeito selecionado, com texto e contraste de pelo menos 4,5:1. A legenda é “Avaliação pessoal por duração”, não classificação global de um modo.

Há exatamente DOIS estilos primários: Legit = 250 ms e Agressivo = 800 ms. Equilibrado é só uma avaliação de efeito, nunca um terceiro preset. Campo/slider são somente leitura com preset fixo; marque explicitamente “Teste manual” para editar. Configuração antiga/default mantém seus ms (padrão 200 ms) em Teste manual, sem escolher 250 ou 800 silenciosamente. Legado Balanced continua parseável mas migra em memória para manual, conservando tempo/efeito e arquivo original. Salvar é explícito.

Trocar estilo/tempo não troca efeito nem interrompe/reinicia/estende o pulso em andamento: só afeta o PRÓXIMO pulso. START apenas arma, STOP continua cancelando e drenando imediatamente. Avaliação e duração configurada representam o próximo pulso, não uma mutação do pulso ativo. Preset não atribui avaliação nem promete benefício em jogo.

O painel compacto não instancia card CAPTURA, botão de copiar diagnóstico, expander de diagnóstico ou lista de log técnico. Telemetria, log limitado e snapshot sanitizado continuam internos. Apenas um indicador pequeno PARADO/ARMADO/PULSO ATIVO/AGUARDANDO, aviso GLOBAL discreto e linha curta de erro START (15 segundos, sem stack/paths/tuplas) permanecem visíveis. Erro de efeito salvo indisponível fica até escolha explícita. Layout validado em 900×560 e 1000×640; Espaço não ativa localmente botões/radios, evitando STOP/duplo pulso acidental; Tab/Enter/setas mantêm navegação.

Para atualizar avaliações sem recompilar, edite `effect-assessments.json` com app fechado. Cada registro possui Mode (nome do enum estável), DurationMs (0–800), Category (Unrated/Legit/Balanced/Aggressive), Note (até 240 caracteres sem controles) e PreferredDurationMs opcional. Preferência é metadado, nunca aplicada automaticamente. Arquivo é limitado a 64 KiB/256 registros, sem pares duplicados, enums desconhecidos ou notas com controles. JSON inválido/ausente usa o catálogo embarcado; não regrava o arquivo. Cores vêm de paleta fixa acessível, não de markup/URLs do JSON.

| Efeito selecionável | Algoritmo |
| --- | --- |
| 1º Dual Token Bucket | Caminho anterior: média 8 KiB/s E pico 32 KiB/s, débito na liberação. |
| 2º Flow-Aware Shaping | Caminho anterior: buckets independentes de 8 KiB/s por fluxo IPv4 TCP/UDP, FIFO por fluxo e round-robin. |
| 3º Outbound Freeze | Caminho anterior: retém saída até fim/cancelamento, libera FIFO. |
| Bandwidth Shaping | Caminho anterior: serialização agregada a 16 KiB/s por bytes IP. |
| Packet Duplication | UDP IPv4/IPv6, até 1400 bytes IP, uma cópia extra por datagrama; máximo 16 cópias/32 KiB por pulso. TCP/impostor passam. |
| ECN Congestion Marking | TCP/UDP IPv4/IPv6 até 4096 bytes IP: só ECT(0)/ECT(1) vira CE; Not-ECT/CE passam sem mudança. |
| DSCP Traffic Marking | TCP/UDP IPv4/IPv6 até 4096 bytes IP: DSCP configurável 0–63, padrão 8; preserva ECN. Valor congelado no início do pulso. |
| MTU / Fragmentation · laboratório local | Somente sondas UDP IPv4 nos próprios sockets loopback, DF solicitado ligado/desligado, MTU lido da interface; relatório de entrega exata. Não é fragmentação GLOBAL. |
| TCP ACK Pacing | Só ACK puro TCP IPv4/IPv6, FIFO por fluxo: primeiro alvo 8 ms e espaçamento 12 ms, TTL 64 ms limitado ao pulso; round-robin até 128 fluxos/512 pacotes/768 KiB. |
| Inter-Packet Gap Equalization | FIFO por fluxo, intervalo EMA de chegadas limitado 6–24 ms (inicial 8); release usa último envio real, não catch-up de burst, salvo TTL 96 ms/fim/STOP. Independe de bytes por segundo. |
| Deadline-Aware Scheduling | EDF entre cabeças FIFO: deadline idade 16 ms para IP <=256 bytes / 48 ms para maiores; FIFO pode herdar deadline anterior. Reserva de serviço pelo backlog antecipa até 24 ms, não taxa constante ou atraso fixo. |

Gap/Deadline suportam TCP/UDP IPv4/IPv6 simples até 4096 bytes IP; não alteram bytes, checksum ou endereço. Limites 128 fluxos, 512 pacotes, 768 KiB; overload cancela/drena fail-open. Tempo é alvo sob relógio monotônico, não garantia hard real-time do Windows. Deadlines/TTL/fim/STOP priorizam entrega e podem comprimir pacing; nunca aguardam pacote futuro para liberar fila.

UDMUX Channel-Aware: não verificável nesta entrega (não há esquema/capturas controladas provando canais externos); tamanho/porta/UDP não são prova. Multipath e NAT Rebinding indisponíveis: exigem respectivamente duas rotas reais comprovadas com receptor próprio e cliente+gateway/NAT+servidor externo próprios comprovando mudança de mapeamento. IfIdx/SubIfIdx outbound são ignorados pelo WinDivert; trocar esses campos não cria multipath, trocar porta não prova NAT WAN. Nenhum dos três aparece como opção acionável, apenas aviso separado de indisponibilidade. Não houve alteração persistente de roteamento/NAT/firewall/interfaces, nem experimentos Roblox cancelados.

Sete técnicas novas ficam “Não avaliado”, em todos os tempos sem observações do proprietário. Fragmentos IPv4, extensões IPv6, ICMP e formatos incompatíveis passam sem modificação. ECN/DSCP preservam payload/flow label e recalculam checksums IPv4/TCP/UDP. ECN não prova resposta do endpoint; DSCP não obriga prioridade e pode ser removido pela rede. Duplicação pode prejudicar aplicações UDP; nunca fabrica ACK TCP. ACK piggyback/data/controle drena ACKs pendentes do mesmo fluxo antes de passar; sobrecarga cancela/drena o pulso (fail-open). STOP/fim podem comprimir intervalos para priorizar entrega segura; Windows não garante deadlines em tempo real.

O laboratório MTU não altera interface/registro/rotas/firewall/IPv6 PMTUD. Tamanhos de payload 128/1472/4000/60000 bytes, máximo oito sondas por pulso, canceláveis; relatório pode ser parcial em pulso curto/STOP. O maior IP entregue é um limite inferior local, não PMTU WAN. Loopback não demonstra fragmentação/reassemblagem: nenhuma alegação de fragmentação é feita nem fragmentos globais são emitidos.


Por decisão posterior do proprietário (#26), TODOS os oito modos da #21 saíram do seletor: Time-Slot, Sawtooth, Correlated Outbound Jitter, Two-Phase, Heavy-Tail, Adaptive Queue, Gap e Gilbert–Elliott. Em particular GE, que travou o jogo segundo relato, não pode ser selecionado/acionado pela UI nem por settings antigos. Não serão reintroduzidos sem pedido explícito. Suas identidades/parser e testes internos permanecem para compatibilidade/regressão; não são opções escondidas ou presets.

Os quatro favoritos mantêm algoritmos, suporte de protocolos e fail-open anteriores, inclusive Flow-Aware para fluxo não identificável. O filtro GLOBAL não é reduzido para UDP/IPv4. Timer/deadline limitam pulso a 800 ms; STOP/fim priorizam liberação segura sobre pacing, e Windows não é tempo real. Não há promessa de ordenação global entre fluxos ou de efeito em jogo.

Os quatro favoritos, duplicação e ACK pacing preservam payload/checksums/endereço capturado. Marcação ECN/DSCP altera somente Traffic Class e checksums necessários, com flags de validade correspondentes; direção/interface/timestamp são preservados. Filas compartilham packet lock e handle epoch do motor; sem task/timer por pacote. STOP drena antes de fechar; nada pendente é reaproveitado no restart. Telemetria interna distingue retenção/liberação/falhas; falha de envio dispara parada de segurança. Não se promete zero perda real de kernel/driver/rede.

## Configurações e legado

Somente os quatro favoritos + sete técnicas acima são selecionáveis. Os oito rejeitados, Packet Size-Aware, inbound, duplex e reordering antigo NÃO estão no dropdown. Não existe checkbox de consentimento por efeito. Enum append-only: todos IDs 0–35 preservados, novos schedulers 36–37. Parser e APIs/schedulers legados permanecem internos para leitura/regressão, inacessíveis pelos controles de produção.

Efeito salvo removido deixa seleção indisponível: START, pulso e Salvar ficam bloqueados até escolha explícita de uma opção suportada. Carregar não regrava nem substitui por favorito silenciosamente; tempo/campos/enums antigos permanecem. Após escolha válida, Salvar grava só a seleção válida em `%LOCALAPPDATA%/DarkDesync/settings.json`. Captura efetiva da UI é sempre Outbound GLOBAL, sem PID; legado estrito PID/FLOW permanece interno aos testes e exige tuplas comprovadas.

## Windows x64 — build e teste

Instale SDK .NET 8; o pacote framework-dependent requer .NET 8 Desktop Runtime x64. Execute DarkDesync.exe como administrador, mantendo WinDivert.dll e WinDivert64.sys junto do EXE.

    dotnet build DarkDesync/DarkDesync.csproj -c Release
    dotnet test DarkDesync.Tests/DarkDesync.Tests.csproj -c Release --filter "Category!=Driver"
    dotnet run --project DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release
    dotnet publish DarkDesync/DarkDesync.csproj -c Release -r win-x64 --self-contained false -o artifacts/DarkDesync-win-x64

Linux compila com `-p:EnableWindowsTargeting=true`, mas não executa WPF/driver nativo. CI `.github/workflows/windows.yml` compila Windows, audita dependências, roda regressões e WPF e verifica EXE/DLL/SYS/SOURCE_COMMIT. Sua semântica não foi reduzida para esta entrega.

Driver real: dispatch `driver_tests=true legacy_global_tests=true` SOMENTE em VM Windows isolada/elevada. GLOBAL afeta toda saída da VM: nunca rode em host compartilhado/produção. Tráfego gerado para asserções é só TCP/UDP loopback próprio, incluindo processos filhos; nada é enviado a terceiros. PowerShell elevado na VM isolada:

    $env:DARKDESYNC_DRIVER_TESTS='1'
    $env:DARKDESYNC_GLOBAL_TESTS='1'
    dotnet build DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release
    dotnet test DarkDesync.Tests/DarkDesync.Tests.csproj -c Release --filter "Category=Driver"
    dotnet run --project DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release -- --driver-ui

Auditoria, riscos, escopo inspecionado/não inspecionado, evidências e rollback: [issue #21](audit/OUTBOUND_EIGHT_EFFECTS_20261102.md). Auditorias anteriores em audit/ são históricas, não especificação da UI atual.

Histórico: .7 entregou os oito schedulers e .8 adicionou avaliações. A especificação atual de painel compacto e dois presets corrige .8 conforme [issues #25/#23](audit/COMPACT_PANEL_20261102.md). Screenshots reais do desktop Windows e manifests de dimensões/estado acompanham a release; não são mockups. Os gates originais de driver/CI permanecem.

Releases: https://github.com/darkgoed/darkdesync/releases/latest (autenticação necessária). Pacote único win-x64, SOURCE_COMMIT e SHA256; .8 (e anteriores) permanecem rollback. O pacote publicado vem do run verde do SHA final de main.

Não foi testado o PC do usuário, teclado físico, interface física/internet adversa, Roblox Player ou anticheat. UI Automation simula seleção e eventos hotkey/WPF, não comprova efeito em jogo. Não há injeção em jogos, bypass, exploit garantido ou promessa de vantagem em partidas públicas.
