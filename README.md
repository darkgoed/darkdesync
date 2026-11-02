# DarkDesync

Aplicativo Windows WPF C#/.NET 8 para ensaios de rede autorizados. Repositório privado canônico: darkgoed/darkdesync. Um único aplicativo em DarkDesync/; os outros dois projetos são testes. WinDivert 2.2.2 x64 incluído, com licenças originais.

## Uso atual — Outbound GLOBAL

O filtro é literalmente `outbound`: TODOS os aplicativos do Windows, sem PID ou seleção oculta de protocolo. O aviso fixo permanece visível.

1. Escolha um dos 12 efeitos e a duração: 0–800 ms, padrão 200 ms; 0 desativa.
2. START abre captura como administrador em ARMADO/pass-through, SEM efeito.
3. Espaço ou Pulso manual aplica UM pulso; ao terminar volta a ARMADO sem fechar o handle. Tecla mantida não repete; não existe ciclo automático. O hook não consome a tecla.
4. STOP cancela e libera pendências com handle ainda válido, encerra workers/captura e retorna a PARADO. Carregar/padrões e fechar também encerram captura. Sem START, Espaço não abre captura.

A avaliação pessoal “250 ms” só ordena favoritos: nunca redefine duração padrão ou salva.

| Efeito selecionável | Algoritmo |
| --- | --- |
| 1º Dual Token Bucket | Caminho anterior: média 8 KiB/s E pico 32 KiB/s, débito na liberação. |
| 2º Flow-Aware Shaping | Caminho anterior: buckets independentes de 8 KiB/s por fluxo IPv4 TCP/UDP, FIFO por fluxo e round-robin. |
| 3º Outbound Freeze | Caminho anterior: retém saída até fim/cancelamento, libera FIFO. |
| Bandwidth Shaping | Caminho anterior: serialização agregada a 16 KiB/s por bytes IP. |
| Time-Slot Bursting | Slots de 40 ms, janela de 8 ms, no máximo 4 pacotes E 3072 bytes por slot; sem acumular crédito. |
| Sawtooth Pacing | Rampa agregada de 4096 a quase 32768 B/s, época de 120 ms com reset; não é token bucket constante. |
| Correlated Outbound Jitter | AR(1) por fluxo/época de 20 ms, coeficiente 0,85; centro 45 ms, amostra 5–100 ms, deadlines FIFO. |
| Two-Phase Drain | Hold no primeiro terço; depois dreno graduado de 8192 a 32768 B/s conforme progresso, sem catch-up burst entre turnos. |
| Heavy-Tail Jitter | Pareto xm=4 ms, alpha=1,4, capped em 140 ms e no fim do pulso; FIFO por fluxo. |
| Adaptive Queue Shaping | Taxa agregada 4096 + 2048 × EMA(ocupação em pacotes), alpha=0,25, teto 65536 B/s; taxa alvo observável, round-robin. |
| Gap-Based Reordering | Só UDP IPv4 no mesmo fluxo; gap/lookahead de 1 pacote, cabeça isolada expira em 24 ms. TCP passa sem efeito. |
| Gilbert–Elliott Burst Loss | Perda intencional Markov por fluxo IPv4 TCP/UDP: P(G→B)=0,08; P(B→G)=0,18; P(drop em G)=0,01; P(drop em B)=0,85. TCP pode retransmitir. Somente pulso explícito. |

Nos oito novos modos, IPv6 (incluindo extension headers), ICMP, fragmentos, impostors e cabeçalhos não identificáveis passam inalterados. O filtro GLOBAL não é reduzido para UDP ou IPv4. Os favoritos mantêm regras anteriores, inclusive fallback fail-open do Flow-Aware para fluxo não identificável. Não se promete reordenação TCP ou ordenação global entre fluxos.

Clock monotônico Stopwatch e Random com seed fixável em testes, escolhida por pulso na produção. Novos schedulers: 512 pacotes, 786432 bytes IP, 128 fluxos históricos por pulso, até 1536 bytes por pacote. Saturação/oversize cancela e drena ANTES de passar o pacote atual; captura continua ARMADA sem efeito. Worker verifica aproximadamente a cada 4 ms, em turnos de até 64 envios; Windows não é tempo real. Timer/deadline limitam efeito a 800 ms. STOP/fim do pulso priorizam liberação segura sobre vazão: pendências no limite podem sair juntas.

Payload, checksum e WINDIVERT_ADDRESS original não são alterados. Filas compartilham packet lock e handle epoch do motor; sem task/timer por pacote. STOP drena antes de fechar; nada pendente é reaproveitado no restart. Contadores distinguem retenção, liberação agendada, pares reordenados, perda intencional GE e falhas de reinjeção. Falha de envio dispara parada de segurança. Perdas reais de kernel/driver/rede não podem ser desfeitas; não se promete zero perda real.

## Configurações e legado

Somente os 12 acima são selecionáveis. Packet Size-Aware, inbound, duplex e reordering antigo NÃO estão no dropdown. Não existe checkbox de consentimento por efeito. Enum append-only: IDs 0–22 preservados, novos IDs 23–30. Parser e APIs/schedulers legados permanecem para leitura/regressão, não como opções escondidas da UI.

Efeito salvo removido deixa seleção indisponível: START exige escolha explícita, sem overwrite automático. Duração e campos antigos são conservados; só Salvar grava `%LOCALAPPDATA%/DarkDesync/settings.json`. Captura efetiva da UI é sempre Outbound GLOBAL, explicitada na tela; carregar filtro/PID antigo nunca inicia captura. O caminho estrito PID/FLOW continua interno para compatibilidade/testes e exige tuplas IPv4 comprovadas, com revogação por pacote. Não existe ampliação do escopo por fallback.

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

Releases: https://github.com/darkgoed/darkdesync/releases/latest (autenticação necessária). Pacote único win-x64, SOURCE_COMMIT e SHA256; .6 permanece rollback. O pacote publicado vem do run verde do SHA final de main.

Não foi testado o PC do usuário, teclado físico, interface física/internet adversa, Roblox Player ou anticheat. UI Automation simula seleção e eventos hotkey/WPF, não comprova efeito em jogo. Não há injeção em jogos, bypass, exploit garantido ou promessa de vantagem em partidas públicas.
