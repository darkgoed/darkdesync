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

A retenção contínua, quando habilitada, volta a atuar após o pulso; Cancelar encerra o pulso e libera a fila atual, mas não desliga a captura contínua. “Parar Captura”, carregar/padrões e fechar restauram o tráfego e encerram a captura. Salvar preserva perfil/efeito, duração, perdas, filtro e atalho em `%LOCALAPPDATA%/DarkDesync/settings.json`; o alvo não é persistido.

## Escopo e limites de segurança

Captura exige PID vivo e tuplas completas IPv4. TCP usa conexões estabelecidas da tabela de proprietários do Windows. UDP usa eventos WinDivert FLOW filtrados no driver por `processId == PID and protocol == 17 and not ipv6`, somente metadados em SNIFF/RECV_ONLY, nunca captura global de pacotes. O filtro NETWORK contém protocolo, IPs e portas de cada fluxo observado; mesmo um filtro personalizado `true` não amplia esse escopo. A propriedade é revalidada por pacote antes de qualquer efeito. Loopback e uma porta isolada não autorizam tráfego.

UDP exige também socket IPv4 de proprietário único; qualquer binding com a mesma porta (outro PID, outro IP, socket duplicado ou IPv6/dual-stack) torna o fluxo inelegível. Essa tabela serve para detectar ambiguidade, nunca como prova positiva de fluxo. Eventos FLOW_DELETED retiram a tupla/identidade do socket; falha ou excesso de metadados invalida a observação. IPv6, multicast, fragmentos e tráfego sem prova segura NÃO são alterados. IPv6 permanece fora do escopo: não há implementação/validação suficiente para autorizar efeitos. Sem conexão elegível, captura é bloqueada, nunca ampliada. Limites: 64 tuplas capturadas e 256 entradas observadas; acima disso bloqueia. Revalidação por pacote prioriza isolamento, não alto throughput.

Para UDP sem TCP: primeiro clique em Iniciar Captura para armar a observação do PID. Sem tupla conhecida, nenhum handle de pacotes é aberto. Gere um NOVO fluxo na aplicação (reconecte o serviço de teste) e inicie novamente. Os datagramas de descoberta passam intactos. FLOW não recupera eventos anteriores à abertura do observador: um fluxo antigo pode precisar ser recriado. Se o driver/observador falhar, UDP permanece bloqueado. Cancelar, trocar alvo/filtro, carregar configurações e fechar também encerram uma observação pendente.

O conjunto de conexões é fixado ao iniciar captura. Conecte primeiro o serviço de teste; reinicie captura para incluir conexões novas. Alterar alvo/filtro para a captura e exige reinício. Freeze retém no máximo 512 pacotes; ao saturar, o excedente passa sem retenção. Encerramento libera pacotes retidos; perdas já aplicadas não podem ser desfeitas. Use somente em experiências/serviços autorizados.

## Verificação e download

GitHub Actions “Windows validation” compila, executa regressões e o smoke WPF, e publica UMA pasta `DarkDesync-win-x64` por execução, fora do Git. Testes do driver são separados, explicitamente habilitados em `workflow_dispatch` com `driver_tests=true`, usam somente sockets TCP/UDP loopback próprios e falham se o driver não carregar. O alvo UDP é um processo-filho de teste, sem TCP; o teste confere os três efeitos de saída, restauração, tráfego de outro PID intacto e recusa de bindings compartilhados. A direção de entrada é coberta por pacotes sintéticos: loopback WinDivert é reportado como saída. Para executar em PowerShell elevado:

    $env:DARKDESYNC_DRIVER_TESTS='1'
    dotnet build DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release
    dotnet test DarkDesync.Tests/DarkDesync.Tests.csproj -c Release --filter "Category=Driver"

Roblox Player real NÃO foi executado/testado. Não há evidência de funcionamento em Roblox, jogos ou anticheat; os testes são serviços locais autorizados. O hook e seu atalho global não foram modificados nesta evolução UDP. Valide manualmente pulo/atalho físico no seu ambiente autorizado; os testes automatizados de tecla simulam transições, não uma sessão física de jogo.

Fonte limpa (autenticação GitHub necessária por ser privado):

    gh repo clone darkgoed/darkdesync -- --branch main
    git -C darkdesync archive --format=zip --output=../darkdesync-source.zip HEAD

`main` é a versão canônica após o merge da PR #1; antes disso, a evolução está em `fix/darkdesync-consolidacao`. O histórico antigo permanece intacto; `git archive`/Download ZIP inclui somente a árvore atual, sem `bin`, `obj`, `TestResults` ou builds antigos. O build WPF pode ser compilado em Linux com `-p:EnableWindowsTargeting=true`, mas execução WPF/driver exige Windows.
