# Smoke WPF (Windows, STA)

Na raiz, com SDK .NET 8 x64:

    dotnet run --project DarkDesync.UiTests/DarkDesync.UiTests.csproj -c Release

Abre a MainWindow real e verifica árvore visual, bindings, seletor único de efeito, duração 0–800 ms, perdas, atalho e persistência em JSON temporário isolado. Não inicia captura nem carrega o driver; instala/desinstala o hook normal da janela, sem emitir teclas sintéticas. O pulso de duração zero é verificado como no-op. Erros de binding ou asserções encerram com código 1.

O GitHub Actions executa este smoke separadamente dos testes WinDivert. Não é evidência de pulo físico, compatibilidade com jogos/anticheat ou execução do driver.
