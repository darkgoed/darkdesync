# DarkDesync

Um aplicativo WPF C#/.NET 8 para ensaios de rede em aplicações e serviços autorizados. Fonte canônica: `darkgoed/darkdesync`. `DarkDesync/` é o único aplicativo; `DarkDesync.Tests/` e `DarkDesync.UiTests/` são testes, não versões alternativas. Base inspecionada: commit `8fbb42c`; o `.csproj` não declara versão de produto. O driver incluído é WinDivert 2.2.2 x64, com licença e documentação originais.

## Windows x64

Instale o SDK .NET 8 e execute na raiz:

    dotnet build DarkDesync/DarkDesync.csproj -c Release
    dotnet test DarkDesync.Tests/DarkDesync.Tests.csproj -c Release --filter "Category!=Driver"
    dotnet run --project DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release
    dotnet publish DarkDesync/DarkDesync.csproj -c Release -r win-x64 --self-contained false -o artifacts/DarkDesync-win-x64

Execute `artifacts/DarkDesync-win-x64/DarkDesync.exe` como administrador para carregar o driver. O computador precisa do .NET 8 Desktop Runtime x64. A pasta publicada deve manter `WinDivert.dll` e `WinDivert64.sys` junto do executável. Não há injeção em jogos nem mecanismos para contornar anticheat.

## Perfil, pulso e ciclo

Selecione aplicação/janela, filtro e efeito em “Perfil do ciclo”. “Desativado” conserva a perda/retenção legada; os outros três efeitos substituem essa perda durante o pulso:

- Outbound Freeze: retém saída; entrada não é retida. Libera a fila na conclusão/cancelamento.
- Burst Loss: descarta a porcentagem configurada durante o pulso, nas direções permitidas pelo filtro.
- Asymmetric Loss: porcentagens independentes de entrada/saída. Selecione filtro entrada + saída para atuar nas duas direções.

Para os três efeitos, duração de 0 a 800 ms, padrão 150 ms; 0 não inicia efeito nem captura. Os presets legados conservam suas próprias durações. Não se garante precisão de tempo real no Windows.

“Pulso manual” inicia uma execução pelo botão. O atalho GLOBAL configurado (padrão Espaço) inicia o mesmo ciclo temporizado, com o mesmo perfil, alvo e filtro. Não existe ciclo periódico/automático. Uma tecla mantida não repete; novos acionamentos durante um ciclo são ignorados. O hook nunca consome a tecla: Espaço continua chegando à aplicação, inclusive para o pulo normal. A opção de foco limita o acionamento à janela selecionada. F6/F7/F8 não são atalhos próprios dos modos.

A retenção contínua, quando habilitada, volta a atuar após o pulso; Cancelar encerra o pulso e libera a fila atual, mas não desliga a captura contínua. “STOP”, carregar/padrões e fechar restauram o tráfego e encerram a captura. Salvar preserva perfil/efeito, duração, perdas, filtro e atalho em `%LOCALAPPDATA%/DarkDesync/settings.json`; o alvo não é persistido.

## Outbound legado GLOBAL — START/STOP contínuo

O Outbound legado usa o filtro WinDivert literal outbound, abrangendo TODOS os aplicativos e suas conexões de saída no Windows. O padrão por processo continua disponível e restrito a PID/FLOW. Para o comportamento legado, selecione “Outbound legado — todos os aplicativos (global)” e o efeito desejado. Pressione **START** para iniciar a captura e aplicar continuamente o efeito; o mesmo botão se torna **STOP** e encerra a captura quando pressionado. Sem checkbox de consentimento adicional e sem limite de 800 ms para esse modo. Escolher a opção não inicia captura automaticamente: somente START abre o handle.

Com efeito “Desativado”, a perda percentual configurada atua continuamente (100% por padrão). Outbound Freeze RETÉM os pacotes até STOP e libera os retidos; Burst Loss e Asymmetric Loss descartam pacotes segundo os percentuais configurados, respeitando a direção do filtro. O estado ativo do motor é mantido até STOP, fechamento ou falha. O atalho Espaço não altera o START/STOP desse modo: evita acionamentos involuntários durante o pulo. Os modos estritos por processo conservam seus pulsos temporizados e atalhos. Configurações de seleção de modo podem ser salvas, mas captura não é iniciada automaticamente ao abrir/carregar.

Como é um filtro GLOBAL, pode afetar navegador, chamadas e outros serviços enquanto estiver ativo. Os recursos internos de parada segura, fechamento do WinDivert e liberação da fila permanecem para não deixar tráfego retido após STOP. Nenhum teste em Roblox Player foi realizado, nem há garantia de efeito específico na partida.

## Escopo estrito e limites de segurança (PADRÃO)

Captura exige PID vivo e tuplas completas IPv4. TCP usa conexões estabelecidas da tabela de proprietários do Windows. UDP usa eventos WinDivert FLOW filtrados no driver por `processId == PID and protocol == 17 and not ipv6`, somente metadados em SNIFF/RECV_ONLY, nunca captura global de pacotes. O filtro NETWORK contém protocolo, IPs e portas de cada fluxo observado; mesmo um filtro personalizado `true` não amplia esse escopo. A propriedade é revalidada por pacote antes de qualquer efeito. Loopback e uma porta isolada não autorizam tráfego.

UDP exige também socket IPv4 de proprietário único; qualquer binding com a mesma porta (outro PID, outro IP, socket duplicado ou IPv6/dual-stack) torna o fluxo inelegível. Essa tabela serve para detectar ambiguidade, nunca como prova positiva de fluxo. Eventos FLOW_DELETED retiram a tupla/identidade do socket; falha ou excesso de metadados invalida a observação. IPv6, multicast, fragmentos e tráfego sem prova segura NÃO são alterados. IPv6 permanece fora do escopo: não há implementação/validação suficiente para autorizar efeitos. Sem conexão elegível, captura é bloqueada, nunca ampliada. Limites: 64 tuplas capturadas e 256 entradas observadas; acima disso bloqueia. Revalidação por pacote prioriza isolamento, não alto throughput.

Ative observação antes de entrar na experiência: selecionar o PID arma FLOW sem abrir NETWORK. Em seguida, habilite captura e entre/reconecte o serviço autorizado. Sem tupla segura, captura fica aguardando; quando um novo fluxo UDP verificável aparece, ele entra automaticamente no filtro, inclusive se já havia apenas TCP:443 capturado. FLOW não recupera eventos anteriores à abertura do observador: UDP antigo pode exigir sair e entrar/reconectar. Não há promessa de detectar o canal de um jogo pelo TCP/HTTPS.

Descoberta/deleção FLOW e reconciliação periódica revogam tuplas sem prova válida e atualizam o filtro automaticamente. Parar/reiniciar NETWORK ou trocar filtro no mesmo PID preserva observação/ledger. Trocar/limpar PID, encerrar processo, carregar/padrões ou fechar revoga a observação. O diagnóstico distingue sockets UDP, fluxos observados/elegíveis, tuplas TCP/UDP escopadas, motivo de UDP ausente e contadores capturados/descartados/retidos por protocolo. Filtro customizado pode excluir protocolos; tupla listada não prova pacotes correspondentes: confira contadores.

WinDivert não permite trocar filtro nativamente no mesmo handle: a publicação do escopo é serializada, mas não há troca kernel sem intervalo. O handle antigo é drenado com envio ainda disponível, a fila é liberada e ele é fechado ANTES do novo ser aberto, sem handles sobrepostos/duplo efeito. No intervalo o tráfego passa normalmente, sem efeito; um pulso em curso pode ter retenção abreviada pela liberação. Nenhuma captura ampla é usada para preencher esse intervalo. Freeze retém no máximo 512 pacotes; excedentes passam sem retenção. Perdas já aplicadas não podem ser desfeitas. Use somente em experiências/serviços autorizados.

## Verificação e download

GitHub Actions “Windows validation” compila, executa regressões e smoke WPF, e publica UMA pasta `DarkDesync-win-x64`. Driver real é opt-in em `workflow_dispatch` com `driver_tests=true`; só usa sockets TCP/UDP loopback próprios e falha sem driver. Além dos três efeitos de saída e isolamento entre PIDs, há cenário de TCP:443 inicial, UDP posterior promovido automaticamente, troca de filtro com tráfego, restart sem perder ledger, fechamento de UDP mantendo TCP/PID e encerramento do PID. Entrada é coberta sinteticamente: loopback WinDivert é reportado como saída. Em PowerShell elevado:

    $env:DARKDESYNC_DRIVER_TESTS='1'
    dotnet build DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release
    dotnet test DarkDesync.Tests/DarkDesync.Tests.csproj -c Release --filter "Category=Driver"

Os testes do driver GLOBAL no Actions exigem habilitação da suíte `legacy_global_tests=true` junto de `driver_tests=true` no Actions. Afetam a saída inteira da VM efêmera durante os testes, inclusive o teste START/STOP contínuo acima de 800 ms; o tráfego gerado para asserção é só local. Não execute em host compartilhado/produção. Localmente, apenas em VM Windows isolada, consentimento adicional é `$env:DARKDESYNC_GLOBAL_TESTS='1'`; sem ele, esses casos são ignorados. A suíte compara UDP sem fluxo no estrito/global, exige filtro literal outbound, testa START/STOP contínuos e os pulsos antigos como regressão, Freeze versus drop, ausência de interceptação após STOP, Cancelar, erro, Dispose e watchdog antigo.

Roblox Player real NÃO foi executado/testado. Não há evidência de funcionamento em Roblox, jogos ou anticheat; os testes são serviços locais autorizados. O hook pass-through e Espaço não foram modificados. Valide manualmente pulo/atalho físico no ambiente autorizado; os testes automatizados de tecla simulam transições, não uma sessão física de jogo. Captura iniciada não é evidência de funcionamento: no estrito, confira “NENHUM UDP INCLUÍDO”, totais TCP/UDP, eventos de hotkey e duração solicitada/medida.

Fonte limpa (autenticação GitHub necessária por ser privado):

    gh repo clone darkgoed/darkdesync -- --branch main
    git -C darkdesync archive --format=zip --output=../darkdesync-source.zip HEAD

`main` é a versão canônica; releases ficam em https://github.com/darkgoed/darkdesync/releases/latest. O histórico permanece intacto; `git archive`/Download ZIP não inclui `bin`, `obj`, `TestResults` ou builds antigos. Linux pode compilar com `-p:EnableWindowsTargeting=true`, mas execução WPF/driver exige Windows.
