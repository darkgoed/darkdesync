# Verificação UI segura (Windows / WPF / STA)

Executar da raiz do repositório, sem fechar a aplicação já aberta:

```bash
dotnet build DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release -p:OutputPath=bin/UiVerification/net8.0-windows/
dotnet DarkDesync.UiTests/bin/UiVerification/net8.0-windows/DarkDesync.UiTests.dll --render
```

Este harness é isolado da suíte de integração. Não chama WinDivertOpen, não instala hooks, não inicia ciclos e não mostra janelas. A construção do ViewModel usa o motor real parado, com guardas de código-fonte antes da construção. A sincronização de EnsureEngineRunning é exercitada com uma sentinela privada temporária `IsRunning`, restaurada imediatamente: isso evita o caminho nativo `Start`.

O XAML de produção é carregado após remover apenas `x:Class` e handlers de chrome. A árvore real é medida e renderizada por `RenderTargetBitmap` em STA; bindings de controles, comandos, logs e jitter two-way são verificados. A imagem resultante é `DarkDesync/visual-verification.png`. Nenhuma chamada ao construtor de MainWindow/Loaded é feita.

O diretório de saída separado evita sobrescrever o EXE aberto pelo usuário. Não use `dotnet run -p:OutputPath=...`: o comando run pode procurar o executável no caminho convencional em vez do caminho sobrescrito. Use build + execução da DLL explicitamente.
