# DarkDesync — auditoria focada: quatro efeitos de shaping (2026-11-02)

Escopo: branch `fix/four-shaping-effects-20261102`, base `f0bc0867cbe50e58f2a845f46c1773f837c5a4cb`. Sem alterações em HCC/outros projetos, visibilidade do repositório, driver nativo ou mecanismo de publicação. Esta nota registra revisão de código; resultados Windows e hashes finais devem ser lidos na evidência anexada à release, não inferidos desta nota.

## Implementação e invariantes

- Outbound Freeze e Bandwidth Shaping mantêm seus corpos de processamento/agendamento aprovados; padrão 200 ms e serialização de 16 KiB/s preservados. Os quatro novos usam um scheduler separado, debitando bytes IP no envio real e limitando crédito após atraso do worker.
- Hold-and-Drain: retenção de 40% do pulso, seguida de orçamento 32 KiB/s. Rate-Step: integração temporal de 4 → 32 KiB/s na metade do pulso. Dual Token Bucket: média 8 KiB/s e pico 32 KiB/s, capacidades/crédito inicial distintos. Flow-Aware: tupla protocolo/IPs/portas IPv4, FIFO e orçamento 8 KiB/s independentes, seleção round-robin. Não são simples mudanças de atraso individual.
- START usa a preparação existente, sem efeito. Atalho continua pelo hook pass-through existente, um keydown físico por pulso, sem autorepeat. Testes automatizados simulam transições; não se alega teste de teclado físico ou de jogo.
- Deadline monotônico e geração do timer continuam existentes. Expiração, cancelamento, troca de efeito, fechamento/reconfiguração de epoch e STOP drenam também o novo scheduler sob o mesmo lock. Nunca há retenção para completar uma taxa depois do deadline. O flush é exceção explícita aos limites de banda.
- Limites novos: 512 pacotes, 1536 bytes IP por pacote, 128 identidades por pulso. Fila e histórico de identidades têm limites independentes. Pacote maior que burst, saturação ou Flow-Aware sem identificação segura cancelam/drenam o pulso (fail-open), sem descarte deliberado. Identidades ociosas não são recicladas dentro do pulso para evitar recriar créditos. A enumeração de liberação e o worker são limitados; nenhum busy-wait novo.
- FIFO global nos três novos algoritmos agregados; FIFO somente por fluxo no Flow-Aware, inclusive no flush. Não existe promessa de ordenação entre fluxos. Todos os tokens contabilizam tamanho IP completo, não payload/contagem.
- GLOBAL continua filtro literal `outbound`. Direção é revalidada antes do scheduler. No estrito, a prova existente de PID/tupla e binding UDP exclusivo vem ANTES dos efeitos; fallback não autoriza tráfego desconhecido. Flow-Aware GLOBAL não tenta inventar fluxo para IPv6/fragmentos/extensões/protocolos desconhecidos: cancela/drena e passa. O modo estrito mantém esses pacotes fora do efeito.
- Falha de reinjeção mantém contabilidade e STOP de segurança existentes. Drop intencional zero não é garantia de ausência de perda no WinDivert/SO/rede.
- UI conserva layout e um controle de pulso, hotkey e START/STOP. Somente seis opções; quatro removidas e três anteriores não reaparecem. Enum numérico legado preservado. Settings antigos migram para Freeze em memória sem gravar ao carregar.

## Evidências exigidas antes da publicação

- `ShapingEffectsTests`: fases, integração na transição, envelopes média/pico por bytes, cap de crédito tardio, regressão de relógio, identidade/fairness/FIFO por fluxo, limites de fila/fluxos, START, expiração/cancelamento/STOP/restart, rejeição de escopo e concorrência. Assinaturas temporais determinísticas distintas dos seis modos.
- Regressões existentes: epochs, hotkey, configurações, filtros, isolamento, pipeline de descarte/atraso legado. Configuração e WPF atualizados para seis opções e migração dos quatro efeitos removidos.
- Driver real isolado: pulsos 200 ms, UDP FIFO e perfil temporal com limites largos, TCP exato em blocos próprios abaixo do burst; sparse flow separado de busy flow. Casos de driver nunca são executados neste host Linux. Tráfego gerado somente loopback próprio no Windows Actions.
- PR CI e `workflow_dispatch driver_tests=true legacy_global_tests=true` no SHA final; só então merge. CI de main e despacho driver no merge SHA. Artifact x64 exclusivamente do run main verde, executável/DLL/SYS e `SOURCE_COMMIT.txt`, SHA256 e TRX anexados à nova Latest. Release anterior preservada.

Revisão básica focada não encontrou motivo para ampliar escopo ou alterar os caminhos aprovados. Qualquer falha funcional/segurança nos gates impede merge/release até correção. Sem Roblox Player, vantagem em jogos, compatibilidade ou bypass de anticheat.
