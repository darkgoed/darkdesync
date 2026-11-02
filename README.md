# DarkDesync

Um aplicativo WPF C#/.NET 8 para ensaios de rede em aplicações e serviços autorizados. Fonte canônica: `darkgoed/darkdesync`. `DarkDesync/` é o único aplicativo; `DarkDesync.Tests/` e `DarkDesync.UiTests/` são testes, não versões alternativas. O driver incluído é WinDivert 2.2.2 x64, com licença e documentação originais.

## Windows x64

Instale o SDK .NET 8 e execute na raiz:

    dotnet build DarkDesync/DarkDesync.csproj -c Release
    dotnet test DarkDesync.Tests/DarkDesync.Tests.csproj -c Release --filter "Category!=Driver"
    dotnet run --project DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release
    dotnet publish DarkDesync/DarkDesync.csproj -c Release -r win-x64 --self-contained false -o artifacts/DarkDesync-win-x64

Execute `artifacts/DarkDesync-win-x64/DarkDesync.exe` como administrador para carregar o driver. O computador precisa do .NET 8 Desktop Runtime x64. A pasta publicada deve manter `WinDivert.dll` e `WinDivert64.sys` junto do executável. Não há injeção em jogos nem mecanismos para contornar anticheat.

## Perfil, pulso e ciclo

Selecione alvo/filtro, efeito, duração e atalho. A interface oferece nove perfis sem descarte intencional ou duplicação. Os quatro aprovados conservam seus caminhos funcionais:

- Outbound Freeze: retém saída; entrada não é retida. Libera a fila na conclusão/cancelamento.
- Bandwidth Shaping: agenda serialização a 16 KiB/s, contabilizada por bytes IP, sem descarte. A resolução do worker agrupa pacotes com prazos próximos; o flush final não está sujeito ao limite de vazão.

- Dual Token Bucket: cada envio exige bytes nos DOIS buckets: média 8 KiB/s (capacidade 2048 bytes, crédito inicial 1024) e pico 32 KiB/s (capacidade 1536 bytes, crédito inicial zero). Débito no momento real de liberação; tokens não crescem indefinidamente.
- Flow-Aware Shaping: filas e buckets independentes por protocolo/IPs/portas IPv4 TCP/UDP; 8 KiB/s por fluxo, crédito inicial zero, limite 1536 bytes e seleção round-robin. FIFO por fluxo; NÃO há ordenação global entre conexões nem limite agregado equivalente ao Bandwidth Shaping.

Cinco mecanismos novos em scheduler separado. Quatro exigem PID estrito; Packet Size-Aware também permite GLOBAL outbound com consentimento separado, explícito e não persistido:

| Perfil | Padrão conservador | Ordem |
| --- | --- | --- |
| Inbound Bandwidth Shaping | Recebidos a 16 KiB/s agregados por bytes IP; crédito inicial zero, burst máximo 1536 bytes. Outbound sem efeito. | FIFO por fluxo, seleção round-robin |
| Correlated Inbound Jitter | Recebidos: AR(1) com coeficiente 0,85; inovação ±12 ms a cada época de 20 ms; centro 60 ms, limites 20–100 ms, seed 731. Não é delay independente por pacote. | FIFO por fluxo; deadlines monotônicos |
| Packet Size-Aware Shaping | UDP outbound: classes por bytes IP ≤256 / ≤768 / >768; serviço 32/16/8 KiB/s e intervalos mínimos 4/12/24 ms. Não infere conteúdo pelo tamanho. | FIFO por fluxo; sem bloqueio entre filas |
| Asymmetric Duplex Shaping | Inbound contínuo 8 KiB/s; outbound 32 KiB/s, aberto 20 de cada 60 ms. Orçamentos independentes e prova de PID nas duas direções. | FIFO por fluxo/direção; encerramento atômico |
| Controlled Packet Reordering — EXPERIMENTAL | Só UDP loopback próprio; opt-in explícito não persistido. Troca pares adjacentes, deslocamento máximo de um datagrama; singleton sai após 20 ms. Nunca TCP nem suas sequências. | Pares intencionalmente fora de ordem; STOP cancela trocas pendentes e libera retidos antes de retornar |

A duração é única: 0–800 ms, padrão 200 ms; 0 desativa. O worker verifica liberação a cada aproximadamente 4 ms, em turnos de até 64 envios, sem garantia de tempo real. O flush final/cancelamento/STOP não obedece à vazão: liberação segura prevalece. Nos novos modos: 512 pacotes, 786432 bytes IP, 128 fluxos históricos por pulso e 1536 bytes por pacote. Saturação/oversize/perda de prova cancela e drena antes do pacote atual. Timer e deadline nos caminhos de captura/release limitam retenção ao pulso; STOP usa flush síncrono sob o mesmo lock para ambas as direções, sem tarefas de pacotes sobreviventes. Retirar o opt-in também cancela e drena. O contador de pares reordenados mede tentativas intencionais, separado das falhas de envio/perdas reais.

No PID estrito, os novos filtros usam apenas tuplas completas comprovadas, com `not impostor`. Uma restrição customizada só reduz esse conjunto. WinDivert reporta loopback como outbound: a direção lógica recebida é comprovada pela tupla reversa do PID, mantendo endereço nativo na reinjeção. Se ambos os sockets loopback pertencem ao alvo, direção ambígua passa livre. IPv6, fragmentos, cabeçalhos inválidos e binding compartilhado passam sem efeito. Reinjeção no mesmo handle não é recapturada; impostors de outros injetores são excluídos. GLOBAL Packet Size-Aware usa consentimento em vez de prova PID: qualquer UDP IPv4 outbound válido é elegível, não apenas loopback; TCP passa livre. Cabeçalhos não identificáveis/oversize/saturação cancelam e drenam o pulso (fail-open).

### Compatibilidade de filtro e direção (patch .3)

O filtro escolhido nunca é trocado ao selecionar PID, efeito ou START, nem salvo implicitamente.

| Efeito | Outbound GLOBAL | Saída do alvo (PID) | Entrada + saída / Conexões verificadas (PID) |
| --- | --- | --- | --- |
| Quatro favoritos | Caminhos anteriores preservados | Preservado | Preservado; efeitos continuam somente saída |
| Packet Size-Aware | UDP IPv4, consentimento GLOBAL separado obrigatório | UDP IPv4 com tupla exata | UDP IPv4 somente saída |
| Controlled Packet Reordering | Incompatível, nunca autorizado | UDP loopback próprio + opt-in experimental | Mesmo limite, somente saída |
| Inbound Bandwidth / Correlated Inbound Jitter | Incompatível | Incompatível | Somente entrada |
| Asymmetric Duplex | Incompatível | Incompatível | Duas direções |

Incompatibilidade aparece no status visível e bloqueia START, sem alterar seleção ou capturar. No modo personalizado, a expressão é intersectada com a direção do efeito e as tuplas PID; uma expressão que exclui a direção necessária não produzirá efeito. O opt-in experimental e o consentimento GLOBAL são independentes e não são salvos; mudar filtro/efeito ou carregar/padrões revoga o consentimento GLOBAL. Não há variante outbound disfarçada de efeito inbound.

Fixtures UDP/TCP determinísticas validam direção, FIFO, clock/seed, cancelamento e limites; o driver Windows dos novos perfis gera somente UDP loopback próprio com PID filho. Isso NÃO comprova inbound nativo de interface física, redes adversas nem jogos de terceiros. WinDivert/Windows pode perder tráfego por fila do kernel, fechamento, falhas de envio ou condições adversas. Não se promete zero perda real. Reordering não garante efeitos Roblox; não há testes/autorização em jogos de terceiros nem instruções para vantagem em partidas públicas.

START apenas arma a captura, SEM efeito, tanto no GLOBAL quanto no estrito. Só depois de START, “Pulso manual” ou o atalho configurado (padrão Espaço) inicia UM ciclo temporizado. Espaço com o motor parado não abre captura. Não existe ciclo periódico/automático. Uma tecla mantida não repete; novos acionamentos durante um ciclo são ignorados. O hook nunca consome a tecla: Espaço continua chegando à aplicação, inclusive para o pulo normal. A opção de foco limita o acionamento à janela selecionada. F6/F7/F8 não são atalhos próprios dos modos.

Ao terminar/cancelar, libera as filas e volta a ARMADO, sem efeito. Reordering cancela trocas ainda pendentes no flush. Flags antigas de throttle contínuo e Drop100 não selecionam efeitos ocultos. Trocar entre os quatro aprovados preserva a captura armada. Trocar de/para um dos cinco novos, filtro ou alvo exige novo START para instalar o escopo correto. “STOP”, carregar/padrões e fechar cancelam, liberam filas e encerram captura. Salvar preserva efeito, duração, filtro e atalho em `%LOCALAPPDATA%/DarkDesync/settings.json`; alvo e opt-in experimental não são persistidos.

Configurações v1 continuam legíveis, com os números do enum preservados e os novos acrescentados. Hold-and-Drain Shaping e Rate-Step Shaping saíram da seleção; eles e todos os perfis anteriormente removidos migram em memória para Outbound Freeze com aviso. Duração e habilitação são respeitadas, captura fica parada e arquivo original NÃO é regravado ao carregar. Campos antigos continuam serializáveis; apenas Salvar confirma a seleção. APIs legadas internas permanecem para compatibilidade/testes, não são opções do combo. Os quatro favoritos mantêm compatibilidade de configurações.

## Outbound legado GLOBAL — START arma, Espaço dispara o efeito

O Outbound legado usa o filtro WinDivert literal `outbound`, abrangendo TODOS os aplicativos e suas conexões de saída no Windows. O padrão por processo continua disponível e restrito a PID/FLOW. Selecione “Outbound legado — todos os aplicativos (global)” e o efeito desejado. START arma sem efeito; Espaço dispara o pulso (padrão 200 ms); ao terminar volta a passar pacotes SEM fechar/reabrir o handle. STOP encerra. START não tem limite de tempo, mesmo com pulso em 0 ms. Escolher a opção ou pressionar Espaço com motor parado não inicia captura.

Outbound Freeze conserva seu caminho de retenção original: retém a saída até terminar/cancelar, com liberação FIFO. O limite continua em 512 pacotes; saturação causa STOP de segurança e liberação, não descarte silencioso. Bandwidth Shaping mantém o caminho aprovado de 16 KiB/s. Nos quatro novos modos, o limite é 512 pacotes de até 1536 bytes IP (768 KiB) e 128 identidades de fluxo por pulso. Saturação, datagrama maior que a capacidade do bucket ou fluxo não identificável no Flow-Aware cancelam o pulso e drenam as filas (FIFO por fluxo, fail-open), mantendo captura armada sem efeito. No GLOBAL, Flow-Aware só identifica IPv4 TCP/UDP não fragmentado com portas válidas; IPv6, fragmentos e cabeçalhos não identificáveis provocam esse fallback, nunca retenção sem limite. No estrito, tráfego sem propriedade comprovada passa sem efeito ANTES de acessar o shaper e nunca é autorizado pelo fallback. Falha de reinjeção é contabilizada como perda e encerra captura por segurança. Não se promete zero perda real: além da fila da aplicação, a fila limitada do WinDivert (8192 pacotes/4 MiB/1000 ms solicitados) pode perder sob carga; não existe confirmação de entrega end-to-end por pacote IP. Driver, sistema e carga podem causar falhas.

O diagnóstico detalhado e eventos de erro continuam em expansores; a opção do filtro explicita o alcance global sem blocos repetidos de instrução na tela.

### Captura da API não é tráfego inbound

`WinDivertRecv` entrega pacotes **interceptados pelo filtro**, não necessariamente recebidos do servidor. A direção está em `WINDIVERT_ADDRESS.Outbound`. GLOBAL abre literalmente `outbound` e aplica efeitos somente quando `addr.Outbound` é verdadeiro. A reinjeção usa o endereço/direção original. Não há detecção ou bypass de anticheat.

Como é um filtro GLOBAL, pode afetar navegador, chamadas e outros serviços enquanto estiver ativo. Os recursos internos de parada segura, fechamento do WinDivert e liberação da fila permanecem para não deixar tráfego retido após STOP. Nenhum teste em Roblox Player foi realizado, nem há garantia de efeito específico na partida.

## Escopo estrito e limites de segurança (PADRÃO)

Captura exige PID vivo e tuplas completas IPv4. TCP usa conexões estabelecidas da tabela de proprietários do Windows. UDP usa eventos WinDivert FLOW filtrados no driver por `processId == PID and protocol == 17 and not ipv6`, somente metadados em SNIFF/RECV_ONLY, nunca captura global de pacotes. O filtro NETWORK contém protocolo, IPs e portas de cada fluxo observado; mesmo um filtro personalizado `true` não amplia esse escopo. A propriedade é revalidada por pacote antes de qualquer efeito. Loopback e uma porta isolada não autorizam tráfego.

UDP exige também socket IPv4 de proprietário único; qualquer binding com a mesma porta (outro PID, outro IP, socket duplicado ou IPv6/dual-stack) torna o fluxo inelegível. Essa tabela serve para detectar ambiguidade, nunca como prova positiva de fluxo. Eventos FLOW_DELETED retiram a tupla/identidade do socket; falha ou excesso de metadados invalida a observação. IPv6, multicast, fragmentos e tráfego sem prova segura NÃO são alterados. IPv6 permanece fora do escopo: não há implementação/validação suficiente para autorizar efeitos. Sem conexão elegível, captura é bloqueada, nunca ampliada. Limites: 64 tuplas capturadas e 256 entradas observadas; acima disso bloqueia. Revalidação por pacote prioriza isolamento, não alto throughput.

Ative observação antes de entrar na experiência: selecionar o PID arma FLOW sem abrir NETWORK. Em seguida, habilite captura e entre/reconecte o serviço autorizado. Sem tupla segura, captura fica aguardando; quando um novo fluxo UDP verificável aparece, ele entra automaticamente no filtro, inclusive se já havia apenas TCP:443 capturado. FLOW não recupera eventos anteriores à abertura do observador: UDP antigo pode exigir sair e entrar/reconectar. Não há promessa de detectar o canal de um jogo pelo TCP/HTTPS.

Descoberta/deleção FLOW e reconciliação periódica revogam tuplas sem prova válida e atualizam o filtro automaticamente. Parar/reiniciar NETWORK ou trocar filtro no mesmo PID preserva observação/ledger. Trocar/limpar PID, encerrar processo, carregar/padrões ou fechar revoga a observação. O diagnóstico distingue sockets UDP, fluxos observados/elegíveis, tuplas TCP/UDP escopadas, motivo de UDP ausente e contadores capturados/descartados/retidos por protocolo. Filtro customizado pode excluir protocolos; tupla listada não prova pacotes correspondentes: confira contadores.

WinDivert não permite trocar filtro nativamente no mesmo handle: a publicação do escopo é serializada, mas não há troca kernel sem intervalo. O handle antigo é drenado com envio ainda disponível, a fila é liberada e ele é fechado ANTES do novo ser aberto, sem handles sobrepostos/duplo efeito. No intervalo o tráfego passa normalmente, sem efeito; um pulso em curso pode ter retenção abreviada pela liberação. Nenhuma captura ampla é usada para preencher esse intervalo. Freeze retém no máximo 512 pacotes; saturação causa parada de segurança. Falha de reinjeção interrompe a captura e é contabilizada como perda; fechar/flush não consegue recuperar pacotes que o driver recusou enviar. Perdas já aplicadas não podem ser desfeitas. Use somente em experiências/serviços autorizados.

## Verificação e download

Auditoria deste patch: [seleção outbound e compatibilidade](audit/OUTBOUND_SELECTION_PATCH_20261102.md). Auditorias anteriores ficam preservadas. TRX e evidência do commit publicado acompanham a release. GLOBAL permite os quatro favoritos e Packet Size-Aware com consentimento separado, não os modos inbound/duplex/reordering.

GitHub Actions “Windows validation” compila, executa regressões e smoke WPF, e publica UMA pasta `DarkDesync-win-x64`. Driver real é opt-in em `workflow_dispatch` com `driver_tests=true`; só gera sockets TCP/UDP loopback próprios e falha sem driver. A suíte cobre os nove perfis, APIs legadas e isolamento entre PIDs, TCP:443 inicial, UDP posterior, troca de filtro, restart, fechamento de UDP e encerramento do PID. Entrada nativa é coberta sinteticamente; entrada lógica loopback usa tupla reversa comprovada, sem representar uma interface física. Em PowerShell elevado:

    $env:DARKDESYNC_DRIVER_TESTS='1'
    dotnet build DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release
    dotnet test DarkDesync.Tests/DarkDesync.Tests.csproj -c Release --filter "Category=Driver"

Os testes do driver GLOBAL no Actions exigem `legacy_global_tests=true` junto de `driver_tests=true`. Afetam a saída inteira da VM efêmera durante os pulsos; o tráfego gerado para asserção é só local. Não execute em host compartilhado/produção. Localmente, apenas em VM Windows isolada, a habilitação adicional é `$env:DARKDESYNC_GLOBAL_TESTS='1'`; sem ela, esses casos são ignorados. A suíte exige filtro literal outbound, START sem efeito apesar de Drop100/throttle, dois pulsos por HotkeyPressed simulado de cada efeito, retorno temporizado ao pass-through com os mesmos workers, STOP fechando o handle, Freeze/FIFO, isolamento de PID e regressões das APIs antigas de pulso.

Roblox Player real NÃO foi executado/testado. Não há evidência de funcionamento em Roblox, jogos ou anticheat; os testes são serviços locais autorizados. O hook continua pass-through. Os testes automatizados de tecla simulam transições e a assinatura HotkeyPressed da UI, não uma sessão física de jogo nem pulo real. Captura iniciada não é evidência de funcionamento em jogo: no estrito, confira “NENHUM UDP INCLUÍDO”, totais TCP/UDP, estado ARMADO/PULSO e eventos de hotkey.

Fonte limpa (autenticação GitHub necessária por ser privado):

    gh repo clone darkgoed/darkdesync -- --branch main
    git -C darkdesync archive --format=zip --output=../darkdesync-source.zip HEAD

`main` é a versão canônica; releases ficam em https://github.com/darkgoed/darkdesync/releases/latest. O repositório é privado: release publicada não implica download público sem autenticação. Cada novo pacote de auditoria deve vir do run verde de `main`, conter `SOURCE_COMMIT.txt` e ter SHA256 publicado. Consulte [auditoria e matriz de evidências](audit/DARKDESYNC_AUDIT.md). O histórico permanece intacto; `git archive`/Download ZIP não inclui `bin`, `obj`, `TestResults` ou builds antigos. Linux pode compilar com `-p:EnableWindowsTargeting=true`, mas execução WPF/driver exige Windows.
