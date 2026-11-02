# DarkDesync — auditoria curta das variantes FIFO (2026-11-02)

Escopo: somente este repositório, base `a4f0b408a94f5685a034b3fc4113ac1ee8146fe7`, PR #10. Revisão básica do diff, sem auditoria genérica de outros projetos.

## Matriz revisada

| Área | Resultado / prova |
|---|---|
| UI | Exatamente seis opções; um pulso 0–800 ms, padrão 200; um atalho; nenhum painel de perda/retenção novo. Smoke WPF testa árvore e bindings. |
| Freeze | Bloco funcional de retenção original intacto; mesma fila, prazo e STOP por saturação. |
| Novos modos | Burst 80 ms; slots 10/60 ms; rampa 0–120/160 ms; shaping 16 KiB/s por bytes IP; adaptive 90 ms ou 32 pacotes. Teste determinístico exige seis assinaturas diferentes. |
| Configurações | Enums antigos não renumerados. Oito opções removidas/legadas migram na seleção para Freeze; arquivo original fica byte a byte intacto até Salvar. Campos e duração antigos preservados. |
| START / tecla / STOP | START arma sem efeito; uma borda de tecla dispara um pulso; repetições ignoradas; hook pass-through inalterado. Fim/cancelar libera FIFO e mantém armado; STOP drena e fecha. |
| FIFO / segurança | Somente dequeue da cabeça; sem drop/reorder/duplicação nos cinco caminhos. Prazo final prevalece sobre slots e shaping. Limite 512: novos modos cancelam pulso e fazem flush fail-open; adaptive libera antes. Falha de envio usa contabilização e safety STOP existentes. |
| Concorrência / CPU | Mesmo packet lock, sem novo worker ou lock; nenhum novo timer. Épocas existentes mantidas. Worker de 4 ms e turnos de 64 envios preservados; testes de captura/liberação/STOP concorrentes verificam término e entrega única. |
| Escopo | Nenhum filtro/PID/FLOW ampliado; GLOBAL continua literal outbound. Novos modos exigem pulso armado e outbound. Estrito revalida propriedade antes do efeito; inbound, fragmentos e tuplas não autorizadas passam sem efeito. |

## Evidência inicial e correção

Run controlado: https://github.com/darkgoed/darkdesync/actions/runs/41396957510

- Driver Windows isolado: 44/44 testes passaram, incluindo os seis efeitos, UDP FIFO, stream TCP exato de 8192 bytes e pass-through após pulso.
- Assinaturas determinísticas: Freeze só em 200 ms; Burst em 80/160/200; Slots em 60/120/180; Sawtooth em 35/70/175/178; shaping com prazos por bytes; adaptive em 110/200. Todos 32/32 FIFO, zero descarte contado.
- Métricas UDP reais são tempos de recebimento da aplicação, não prazos garantidos do kernel. Há variação de agendamento observável; os TRX contêm todas as amostras.
- Primeiro run: 197/198 regressões passaram. O teste antigo `OldPulseTimer_CannotStopOrRestoreNewStart` retomou perto de 800 ms após uma espera nominal de 180 ms e confundiu expiração legítima com epoch antiga. Corrigido somente o teste: relógio do efeito injetado, preservando o timer real e a verificação de geração. O caminho de produção do Freeze não foi alterado.
- Build cruzado local: aplicação, testes e WPF compilados; execução WPF/driver é exclusivamente Windows. Aviso xUnit2012 pré-existente em MinimalEffectsTests não bloqueante.

Não foi identificado P0/P1 no diff revisado. Isso não substitui os gates: merge exige checks do PR e dispatch com ambos os flags no HEAD atualizado; publicação exige checks e driver verdes novamente no commit final da main. Evidência final, contagens, SHA e checksum acompanham a release.

## Limites e rollback

Sem teste/garantia em Roblox Player ou anticheat. Sem descarte intencional não significa zero perda real: filas finitas do WinDivert, falhas de reinjeção e carga do sistema podem perder tráfego. Flush final não preserva o limite de vazão e pode ser executado tarde pelo scheduler; não existe garantia de tempo real.

Rollback: release `v2026.11.02-minimal-nonloss.1` preservada, fonte base acima. ZIP anterior baixado e conferido com checksum publicado: `6324dfe02e7189b02eafe86c855415c55ccec227bbeff460f594c4eb72504897`. Repositório deve permanecer privado; nenhuma release anterior será removida.
