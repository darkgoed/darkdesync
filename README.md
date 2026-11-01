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

Captura exige PID vivo e conexões TCP/IPv4 estabelecidas identificadas pela tabela de proprietários do Windows. O filtro externo contém os endereços e portas completos de cada conexão; mesmo um filtro personalizado `true` não amplia esse escopo. A propriedade é revalidada por pacote antes de qualquer efeito. Loopback e uma porta isolada não autorizam tráfego.

UDP, IPv6, fragmentos e tráfego sem identificação segura NÃO são alterados. Se não há conexão elegível, a captura é bloqueada com erro explícito, sem fallback global. Esta versão não serve para alvos exclusivamente UDP/IPv6. Limite de 64 conexões por sessão; acima disso, bloqueia em vez de truncar. A revalidação por pacote prioriza isolamento, não alto throughput.

O conjunto de conexões é fixado ao iniciar captura. Conecte primeiro o serviço de teste; reinicie captura para incluir conexões novas. Alterar alvo/filtro para a captura e exige reinício. Freeze retém no máximo 512 pacotes; ao saturar, o excedente passa sem retenção. Encerramento libera pacotes retidos; perdas já aplicadas não podem ser desfeitas. Use somente em experiências/serviços autorizados.

## Verificação e download

GitHub Actions “Windows validation” compila, executa regressões e o smoke WPF, e publica UMA pasta `DarkDesync-win-x64` por execução, fora do Git. Testes do driver são separados, explicitamente habilitados em `workflow_dispatch` com `driver_tests=true`, usam somente sockets TCP loopback próprios e falham se o driver não carregar. Para executá-los localmente em PowerShell elevado:

    $env:DARKDESYNC_DRIVER_TESTS='1'
    dotnet test DarkDesync.Tests/DarkDesync.Tests.csproj -c Release --filter "Category=Driver"

Os testes de hook simulam transições de tecla, não uma sessão física de jogo. Valide manualmente o pulo/atalho físico no seu ambiente autorizado; testes automatizados não atestam compatibilidade com anticheat.

Fonte limpa (autenticação GitHub necessária por ser privado):

    gh repo clone darkgoed/darkdesync -- --branch fix/darkdesync-consolidacao
    git -C darkdesync archive --format=zip --output=../darkdesync-source.zip HEAD

Após merge, use `main`. O histórico antigo permanece intacto; `git archive`/Download ZIP inclui somente a árvore atual, sem `bin`, `obj`, `TestResults` ou builds antigos. O build WPF pode ser compilado em Linux com `-p:EnableWindowsTargeting=true`, mas execução WPF/driver exige Windows.
