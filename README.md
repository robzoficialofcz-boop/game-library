# Game Library

Launcher de jogos para Windows, desenvolvido em **C# + WPF (.NET 8)**. O objetivo é reunir jogos instalados em uma biblioteca local, com uma interface escura e sem exigir cadastro ou login.

## Recursos

- Adicionar jogos manualmente selecionando arquivos `.exe`.
- Abrir jogos pelo launcher.
- Pesquisar jogos pelo nome.
- Remover jogos da biblioteca sem apagar os arquivos originais.
- Registrar tempo de jogo do processo iniciado.
- Exibir estatísticas básicas da biblioteca.
- Buscar capas pela SteamGridDB (integração opcional).

## Privacidade e funcionamento local

- O Game Library não possui sistema de conta, cadastro, login ou autenticação.
- Não exige conta Steam, Epic Games ou Microsoft para abrir o aplicativo.
- A biblioteca e as estatísticas são armazenadas localmente em `%LOCALAPPDATA%\\GameLibrary`.
- As capas baixadas ficam em `%LOCALAPPDATA%\\GameLibrary\\Covers`.
- A busca online de capas é opcional. Sem configurar a integração, o restante do launcher continua funcionando.

## Requisitos

- Windows 10 ou Windows 11.
- .NET 8 SDK.
- Visual Studio 2022 com a carga de trabalho **Desenvolvimento para desktop com .NET**, ou terminal com o SDK instalado.

## Executar pelo terminal

Na pasta que contém `GameLibrary.csproj`:

```powershell
dotnet restore
dotnet run
```

Para compilar:

```powershell
dotnet build -c Release
```

## Capas automáticas (opcional)

A busca de capas usa a API da [SteamGridDB](https://www.steamgriddb.com/). Para habilitar:

1. Obtenha uma chave de API na SteamGridDB.
2. Defina a variável de ambiente do Windows:

```powershell
[Environment]::SetEnvironmentVariable(
    "STEAMGRIDDB_API_KEY",
    "SUA_CHAVE_API",
    "User"
)
```

3. Feche e abra novamente o Game Library.

A chave é opcional e não é uma conta do Game Library. Não publique sua chave em commits, issues ou prints. Se ela for exposta, revogue-a no serviço.

## Estrutura

```text
GameLibrary/
├── Models/
│   └── Game.cs
├── Services/
│   ├── CoverService.cs
│   ├── GameLauncherService.cs
│   └── GameStorageService.cs
├── App.xaml
├── App.xaml.cs
├── MainWindow.xaml
├── MainWindow.xaml.cs
└── GameLibrary.csproj
```

## Limitações conhecidas

- O contador acompanha o processo executável iniciado pelo launcher. Alguns jogos iniciam um processo-filho e encerram o processo inicial; nesses casos, o tempo pode não refletir a sessão completa.
- A busca de capas depende de internet e de uma chave SteamGridDB válida.
- Não há sincronização em nuvem: os dados permanecem no computador.
- Esta é uma versão inicial; não é um produto final nem um instalador.

## Contribuições

Sugestões e relatos de bugs podem ser enviados pela aba **Issues** do GitHub. Ao abrir uma issue, informe a versão do Windows, os passos para reproduzir e a mensagem de erro, se houver.

## Licença

Nenhuma licença foi incluída nesta versão. Até que uma licença seja escolhida e adicionada, todos os direitos permanecem reservados ao autor. Não reutilize ou redistribua o código como se estivesse sob uma licença open source.
