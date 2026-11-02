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

No escopo estrito, selecione aplicação/janela, filtro e efeito em “Perfil do ciclo”. “Desativado” conserva o perfil legado estrito; os efeitos explícitos substituem essa perda durante o pulso. No GLOBAL, “Desativado” é pass-through, sem perda nem throttle implícitos:

- Outbound Freeze: retém saída; entrada não é retida. Libera a fila na conclusão/cancelamento.
- Burst Loss: descarta a porcentagem configurada durante o pulso, nas direções permitidas pelo filtro.
- Asymmetric Loss: porcentagens independentes de entrada/saída. Selecione filtro entrada + saída para atuar nas duas direções.
- Atraso (ms): retenção por pacote de 0–3000 ms, independente da duração do pulso estrito.
- Drop (%): descarte explícito de 0–100%; 100% é queda total, não Lag.

Para efeitos diagnósticos no escopo estrito, duração do pulso de 0 a 800 ms, padrão 150 ms; 0 não inicia efeito nem captura. Os presets legados conservam suas próprias durações. Não se garante precisão de tempo real no Windows.

START apenas arma a captura, SEM efeito, tanto no GLOBAL quanto no estrito. Só depois de START, “Pulso manual” ou o atalho configurado (padrão Espaço) inicia UM ciclo temporizado. Espaço com o motor parado não abre captura. Não existe ciclo periódico/automático. Uma tecla mantida não repete; novos acionamentos durante um ciclo são ignorados. O hook nunca consome a tecla: Espaço continua chegando à aplicação, inclusive para o pulo normal. A opção de foco limita o acionamento à janela selecionada. F6/F7/F8 não são atalhos próprios dos modos.

Ao terminar/cancelar o pulso, libera a fila FIFO e volta ao estado ARMADO, sem efeito, com o mesmo handle de captura. Flags antigas de throttle contínuo e Drop100 não atuam fora do pulso. Trocar efeito cancela o pulso e preserva a captura armada; trocar filtro/alvo exige novo START. “STOP”, carregar/padrões e fechar cancelam o pulso, liberam a fila e encerram a captura. Salvar preserva perfil/efeito, duração, perdas, filtro e atalho em `%LOCALAPPDATA%/DarkDesync/settings.json`; o alvo não é persistido.

## Outbound legado GLOBAL — START arma, Espaço dispara o efeito

O Outbound legado usa o filtro WinDivert literal outbound, abrangendo TODOS os aplicativos e suas conexões de saída no Windows. O padrão por processo continua disponível e restrito a PID/FLOW. Selecione “Outbound legado — todos os aplicativos (global)” e o efeito desejado. Pressione START para ARMAR: cada pacote interceptado é reinjetado sem efeito. O botão permanece STOP até a parada explícita. Depois de START, Espaço dispara o efeito escolhido pela duração do pulso (0–800 ms, padrão 150 ms). Ao terminar, libera a fila e volta a passar pacotes SEM fechar/reabrir o handle. Sem checkbox de consentimento adicional. START não tem limite de tempo, mesmo com pulso em 0 ms. Escolher a opção ou pressionar Espaço com motor parado não inicia captura.

Com efeito “Desativado”, os pacotes sempre passam sem efeito GLOBAL, inclusive no Espaço, mesmo com configurações antigas de perda 100%, CycleDropEnabled ou throttle. Para descartar durante um pulso, escolha explicitamente “Drop (%)”, “Burst Loss” ou “Asymmetric Loss”. Outbound Freeze RETÉM somente durante o pulso e libera FIFO ao terminar/cancelar ou em STOP/parada de segurança. Asymmetric Loss GLOBAL atua somente na saída; perda de entrada só é aplicável no filtro estrito bidirecional. Espaço nunca liga/desliga a captura. Configurações podem ser salvas, mas captura não inicia automaticamente ao abrir/carregar. Carregar GLOBAL “Desativado” não reativa perda implícita.

Ao selecionar o Outbound legado com o efeito ainda “Desativado”, a interface passa automaticamente para Atraso (ms) (padrão 150 ms), mas isso NÃO aplica efeito antes do Espaço. Um efeito escolhido explicitamente é preservado. Atraso por pacote (0–3000 ms) e Duração do pulso (0–800 ms) são controles independentes, ambos visíveis no GLOBAL. Cada pacote tem prazo contado desde sua captura dentro do pulso; ao terminar o pulso, todos os retidos são liberados mesmo se o atraso configurado for maior. Alterar o atraso recalcula a fila pendente desde a captura original: 3000→50 ms libera os que já esperaram 50 ms, sem ultrapassagens; 0 ms libera a fila. O espaçamento de throttle não é somado ao Lag. Fila limitada a 512: no Lag/throttle, o mais antigo é liberado para abrir espaço, preservando FIFO, com possível atraso menor sob saturação. No Freeze, exceder o limite causa STOP de segurança e liberação, nunca descarte silencioso. Atraso real depende do agendamento do Windows; não equivale necessariamente à latência exibida pelo Roblox.

### Captura da API não é tráfego inbound

`WinDivertRecv` entrega pacotes **interceptados pelo filtro**, não necessariamente recebidos do servidor. A direção está em `WINDIVERT_ADDRESS.Outbound`. GLOBAL abre literalmente `outbound` e aplica efeitos somente quando `addr.Outbound` é verdadeiro; não transforma uma chamada Recv em tráfego inbound. A reinjeção usa o endereço/direção original. Clumsy também usa WinDivert: a comparação correta é Lag de saída versus Atraso de saída, e não Lag versus Freeze indefinido ou Drop 100%. A semelhança da API não prova equivalência de desempenho. Não há detecção ou bypass de anticheat.

Como é um filtro GLOBAL, pode afetar navegador, chamadas e outros serviços enquanto estiver ativo. Os recursos internos de parada segura, fechamento do WinDivert e liberação da fila permanecem para não deixar tráfego retido após STOP. Nenhum teste em Roblox Player foi realizado, nem há garantia de efeito específico na partida.

## Escopo estrito e limites de segurança (PADRÃO)

Captura exige PID vivo e tuplas completas IPv4. TCP usa conexões estabelecidas da tabela de proprietários do Windows. UDP usa eventos WinDivert FLOW filtrados no driver por `processId == PID and protocol == 17 and not ipv6`, somente metadados em SNIFF/RECV_ONLY, nunca captura global de pacotes. O filtro NETWORK contém protocolo, IPs e portas de cada fluxo observado; mesmo um filtro personalizado `true` não amplia esse escopo. A propriedade é revalidada por pacote antes de qualquer efeito. Loopback e uma porta isolada não autorizam tráfego.

UDP exige também socket IPv4 de proprietário único; qualquer binding com a mesma porta (outro PID, outro IP, socket duplicado ou IPv6/dual-stack) torna o fluxo inelegível. Essa tabela serve para detectar ambiguidade, nunca como prova positiva de fluxo. Eventos FLOW_DELETED retiram a tupla/identidade do socket; falha ou excesso de metadados invalida a observação. IPv6, multicast, fragmentos e tráfego sem prova segura NÃO são alterados. IPv6 permanece fora do escopo: não há implementação/validação suficiente para autorizar efeitos. Sem conexão elegível, captura é bloqueada, nunca ampliada. Limites: 64 tuplas capturadas e 256 entradas observadas; acima disso bloqueia. Revalidação por pacote prioriza isolamento, não alto throughput.

Ative observação antes de entrar na experiência: selecionar o PID arma FLOW sem abrir NETWORK. Em seguida, habilite captura e entre/reconecte o serviço autorizado. Sem tupla segura, captura fica aguardando; quando um novo fluxo UDP verificável aparece, ele entra automaticamente no filtro, inclusive se já havia apenas TCP:443 capturado. FLOW não recupera eventos anteriores à abertura do observador: UDP antigo pode exigir sair e entrar/reconectar. Não há promessa de detectar o canal de um jogo pelo TCP/HTTPS.

Descoberta/deleção FLOW e reconciliação periódica revogam tuplas sem prova válida e atualizam o filtro automaticamente. Parar/reiniciar NETWORK ou trocar filtro no mesmo PID preserva observação/ledger. Trocar/limpar PID, encerrar processo, carregar/padrões ou fechar revoga a observação. O diagnóstico distingue sockets UDP, fluxos observados/elegíveis, tuplas TCP/UDP escopadas, motivo de UDP ausente e contadores capturados/descartados/retidos por protocolo. Filtro customizado pode excluir protocolos; tupla listada não prova pacotes correspondentes: confira contadores.

WinDivert não permite trocar filtro nativamente no mesmo handle: a publicação do escopo é serializada, mas não há troca kernel sem intervalo. O handle antigo é drenado com envio ainda disponível, a fila é liberada e ele é fechado ANTES do novo ser aberto, sem handles sobrepostos/duplo efeito. No intervalo o tráfego passa normalmente, sem efeito; um pulso em curso pode ter retenção abreviada pela liberação. Nenhuma captura ampla é usada para preencher esse intervalo. Freeze retém no máximo 512 pacotes; saturação causa parada de segurança. Falha de reinjeção interrompe a captura e é contabilizada como perda; fechar/flush não consegue recuperar pacotes que o driver recusou enviar. Perdas já aplicadas não podem ser desfeitas. Use somente em experiências/serviços autorizados.

## Verificação e download

GitHub Actions “Windows validation” compila, executa regressões e smoke WPF, e publica UMA pasta `DarkDesync-win-x64`. Driver real é opt-in em `workflow_dispatch` com `driver_tests=true`; só usa sockets TCP/UDP loopback próprios e falha sem driver. Além dos três efeitos de saída e isolamento entre PIDs, há cenário de TCP:443 inicial, UDP posterior promovido automaticamente, troca de filtro com tráfego, restart sem perder ledger, fechamento de UDP mantendo TCP/PID e encerramento do PID. Entrada é coberta sinteticamente: loopback WinDivert é reportado como saída. Em PowerShell elevado:

    $env:DARKDESYNC_DRIVER_TESTS='1'
    dotnet build DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release
    dotnet test DarkDesync.Tests/DarkDesync.Tests.csproj -c Release --filter "Category=Driver"

Os testes do driver GLOBAL no Actions exigem `legacy_global_tests=true` junto de `driver_tests=true`. Afetam a saída inteira da VM efêmera durante os pulsos; o tráfego gerado para asserção é só local. Não execute em host compartilhado/produção. Localmente, apenas em VM Windows isolada, a habilitação adicional é `$env:DARKDESYNC_GLOBAL_TESTS='1'`; sem ela, esses casos são ignorados. A suíte exige filtro literal outbound, START sem efeito apesar de Drop100/throttle, dois pulsos por HotkeyPressed simulado de cada efeito, retorno temporizado ao pass-through com os mesmos workers, STOP fechando o handle, Freeze/FIFO, isolamento de PID e regressões das APIs antigas de pulso.

Roblox Player real NÃO foi executado/testado. Não há evidência de funcionamento em Roblox, jogos ou anticheat; os testes são serviços locais autorizados. O hook continua pass-through. Os testes automatizados de tecla simulam transições e a assinatura HotkeyPressed da UI, não uma sessão física de jogo nem pulo real. Captura iniciada não é evidência de funcionamento em jogo: no estrito, confira “NENHUM UDP INCLUÍDO”, totais TCP/UDP, estado ARMADO/PULSO e eventos de hotkey.

Fonte limpa (autenticação GitHub necessária por ser privado):

    gh repo clone darkgoed/darkdesync -- --branch main
    git -C darkdesync archive --format=zip --output=../darkdesync-source.zip HEAD

`main` é a versão canônica; releases ficam em https://github.com/darkgoed/darkdesync/releases/latest. O repositório é privado: release publicada não implica download público sem autenticação. Cada novo pacote de auditoria deve vir do run verde de `main`, conter `SOURCE_COMMIT.txt` e ter SHA256 publicado. Consulte [auditoria e matriz de evidências](audit/DARKDESYNC_AUDIT.md). O histórico permanece intacto; `git archive`/Download ZIP não inclui `bin`, `obj`, `TestResults` ou builds antigos. Linux pode compilar com `-p:EnableWindowsTargeting=true`, mas execução WPF/driver exige Windows.
