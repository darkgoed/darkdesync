# Adendo da issue #21 — avaliações pessoais e estilos

Solicitação original do proprietário: https://github.com/darkgoed/darkdesync/issues/21#issuecomment-6577500323 (publicada durante a execução, encontrada na releitura antes do fechamento). A .7 já havia sido publicada com os oito schedulers e gates completos; a issue permaneceu aberta para o complemento. Nenhuma reescrita da .7/.6, nenhum agente/conta adicional e nenhum projeto externo alterado. Branch dedicada parte da main 1b1fb928a4f3828618f7fbdbd15e2f3346315469.

## Mudanças inspecionadas

Catálogo central EffectExperienceCatalog por (DiagnosticMode, duração exata), PulseStyle independente do enum legado DiagnosticMode, bindings VM, settings versão 1 retrocompatíveis, converter/proxy WPF para pílulas em dropdown Popup e pílula selecionada. Nenhum scheduler/driver/algoritmo/filtro de produção alterado. Oito efeitos reais e gates anteriores continuam intactos. Inspeção por agente principal: correção, segurança, persistência, UI/copy e limites (Ponytail FULL). Não é revisão independente; Jev script/Semgrep ausentes, como já declarado na auditoria principal.

Tabela fornecida: 800 ms Dual Token Bucket e Freeze Agressivo/vermelho; Flow-Aware e Bandwidth Equilibrado/âmbar. Freeze 250 ms Legítimo/verde, 800 ms com aviso “injogável nos testes pessoais” sem bloqueio. Oito novos e qualquer combinação sem dados: Não testado/cinza. Preset de tempo não atribui classificação. Texto distingue avaliação subjetiva de medição e nunca promete benefício/anticheat.

Legítimo fixa 250 ms, Agressivo fixa 800 ms; Equilibrado é ajustável e mantém tempo atual (nenhum 500 ms inventado); Personalizado mantém editor/slider. Editar ms de estilo fixo passa explicitamente para Personalizado. Nenhuma troca de efeito ao trocar estilo. Padrão permanece 200 ms/Personalizado. Alteração de tempo/estilo cancela pulso atual, não inicia captura e não dispara novo pulso; captura ARMADA continua até STOP. Aviso GLOBAL e pulso explícito permanecem.

Settings PulseStyle novo opcional, nenhum novo version bump ou auto-save. Arquivos antigos conservam ms como Personalizado, modos removidos continuam sem seleção automática e não são sobrescritos. Salvar explícito persiste estilo e tempo. Carregar/Padrões param captura, atualizam controles/rótulos e nunca escrevem. JSON editado externamente com estilo fixo e tempo conflitante conserva o tempo como Personalizado, não impõe 250/800 silenciosamente. Avaliações são metadados canônicos, não cópias persistidas que poderiam divergir.

## Verificação e riscos

Testes novos: tabela exata sem propagar rating a tempos vizinhos; oito novos não testados em todas durações; estilos sem mudar efeito/captura; ajuste manual; persistência de quatro estilos; defaults e load somente leitura; legado sem estilo/removido; cancelamento seguro; contraste WCAG >=4,5:1 calculado para texto normal de todas pílulas. UI WPF real verifica cores/render/texto, campo e slider atualizados, estilo fixo/ajustável, aviso Freeze, abrir/realizar os 12 itens do popup e pílula correta por linha, salvar/carregar/padrões sem escrita automática nem captura. Este teste roda tanto no smoke WPF quanto na suíte WPF nativa, antes de testar START/Space/STOP dos 12 efeitos com GLOBAL literal outbound.

Build cross-target Linux não comprova execução WPF. Publicação do complemento exige Windows PR exata, dispatch driver_tests=true legacy_global_tests=true, merge gates verdes, repetição em SHA final da main e pacote/ZIP/SOURCE_COMMIT/checksums com leitura de volta da release. Evidências finais, SHA e runs em RELEASE_EVIDENCE.json e notas da próxima release; não se considera completo só por existir código.

Não testados: PC/teclado físico do usuário, jogo/anticheat, rede física ou deficiência visual real; contraste calculado e texto não substituem teste com usuários. Classificações são as observações fornecidas pelo proprietário, não evidência de gameplay reproduzida pelo agente. Chaves/cores são centralizadas para futuras avaliações, sem adivinhar valores.

Rollback .6 e .7 permanecem publicados e sem alteração de assets. A .7 foi baixada/read-back com ZIP SHA256 ea4547f79032bc7ad6221602344809362ed1875108b91b901810e508294182f7. Parar/fechar versão nova antes de abrir versão anterior em pasta separada; backup de settings antes de Salvar. Versões anteriores ignoram campo PulseStyle desconhecido; preservar arquivo antes de salvar com versão antiga.
