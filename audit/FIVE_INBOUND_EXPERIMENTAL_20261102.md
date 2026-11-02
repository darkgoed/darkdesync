# Cinco mecanismos inbound/experimental — 2026-11-02

## Escopo da mudança

Base: 48574561dbff138068f49a65e7d5e39936d36223. Repositório privado darkgoed/darkdesync; execução somente neste projeto, sem subagentes. Auditoria focada em filtros, identidade, timers, direções e flush. Evidência final com SHAs/runs/TRX acompanha a release, não confundir compilação Linux com execução Windows.

## Matriz de perfis

| UI (seleção única, nove) | Efeito e ordem | Escopo |
| --- | --- | --- |
| Outbound Freeze | Retenção até fim; FIFO original | Estrito ou GLOBAL original |
| Dual Token Bucket | Média+pico por bytes; FIFO original | Estrito ou GLOBAL original |
| Bandwidth Shaping | 16 KiB/s original | Estrito ou GLOBAL original |
| Flow-Aware Shaping | 8 KiB/s por fluxo, FIFO por fluxo original | Estrito ou GLOBAL original |
| Inbound Bandwidth Shaping | 16 KiB/s inbound agregado, FIFO por fluxo, RR | Tuplas IPv4 de PID comprovadas |
| Correlated Inbound Jitter | AR(1), épocas 20ms, seed 731, atraso 20–100ms, FIFO por fluxo | Mesma prova; só recebidos |
| Packet Size-Aware Shaping | Classes de bytes IP UDP, taxas/intervalos distintos, FIFO por fluxo | Mesma prova; só UDP outbound |
| Asymmetric Duplex Shaping | IN 8 KiB/s contínuo, OUT 32 KiB/s janelado 20/60ms; FIFO por fluxo/direção | Prova em ambas as direções; um handle e um ciclo |
| Controlled Packet Reordering — EXPERIMENTAL | Pares adjacentes UDP, deslocamento ≤1, singleton 20ms | Só loopback próprio + PID + opt-in explícito não persistido |

Hold-and-Drain e Rate-Step não são selecionáveis. Enum append-only, APIs antigas mantidas para compatibilidade. Configs de perfis removidos migram em memória para Freeze; abrir nunca regrava o arquivo. Os quatro favoritos mantêm identidade numérica e caminhos de shaping/release; PulseShaper original não foi alterado.

## Auditoria de segurança/ciclo

- START arma pass-through; Space pass-through sem autorepeat dispara só quando armado; padrão 200ms, máximo 800ms; zero desativa. Novos perfis exigem configurar escopo antes de START; mudança de/para novo perfil exige reiniciar captura. GLOBAL mantém filtro literal outbound e os quatro favoritos; novos perfis são bloqueados nesse caminho.
- NETWORK nunca amplia para inbound global: filtro exato por protocolo/IPs/portas vem de TCP estabelecido ou FLOW UDP do PID + binding exclusivo. Custom só intersecta. Novos incluem not impostor; mesmo handle não recaptura reinjeção. Metadados/native address são preservados.
- WinDivert loopback é nativamente outbound. Novos modos inferem direção lógica apenas pela propriedade da tupla completa em um dos sentidos; ambos pertencentes ao alvo é ambíguo → fail-open. Não alteram bits da direção real ao reinjetar. Direção não comprovada, PID morto, IPv6, fragmentos e binding compartilhado não autorizam efeito.
- Scheduler novo isolado; filas até 512 pacotes/786432 bytes IP/128 fluxos históricos/1536 bytes por pacote. Oversize/saturação/perda de prova cancela pulso e drena antes do pacote novo. Byte budget no release real; crédito acumulado limitado. Round-robin não é garantia de igualdade de vazão para tamanhos heterogêneos.
- Timer e deadline de captura/release; um lock de pacote serializa enqueue/dequeue/flush/STOP. Duplex não tem dois timers/handles independentes. Rotação de escopo cancela novos modos. STOP/exit descarrega retidos enquanto o handle ainda envia e fecha; nenhum timer de pacote sobrevive. Reordering cancela swaps pendentes no flush, libera predecessores restantes antes de STOP retornar, não faz replay/duplicação/drop deliberado.
- Pares UDP reordenados são tentativas intencionais, contados separadamente das falhas de envio. Falha WinDivertSend aciona STOP e conta perda; o flush não recupera envio recusado. Fila kernel também pode perder sob carga. Nenhuma promessa de zero perda em rede real.

## Verificação exigida para publicação

- Compilação local Linux com targeting Windows; execução de WPF e WinDivert somente Windows.
- Fixtures determinísticas de relógio/seed: bytes/classes, AR(1), FIFO, fairness entre filas, direções, pares limitados, timeout/cancel/STOP, saturação, perda de prova, consentimento e concorrência.
- Regressão dos quatro favoritos e migrações; smoke WPF: nove opções, controles/bindings e opt-in.
- PR Windows CI + dispatch driver_tests=true legacy_global_tests=true no SHA final da branch; main CI + dispatch no commit de merge. TRX e evidência final de execução são anexados à release.
- Pacote Windows x64 exclusivamente do artifact de main; SOURCE_COMMIT.txt e SHA256 conferidos. Release anterior preservada para rollback.

## Limites explícitos

Driver novo: somente sockets UDP loopback controlados e PID filho próprio. UDP/TCP nativo inbound e FIFO/direção são cobertos por fixtures sintéticas; entrada real de interface física não é validada pelo loopback. Testes existentes TCP/UDP legados continuam no gate. Não há testes em Roblox, jogos de terceiros, anticheat ou rede adversa. Reordering não garante efeitos Roblox. Não há drop intencional, alteração de sequências TCP, replay, duplicação ou bypass. Não usar o release como evidência de vantagem/funcionamento em partidas públicas.
