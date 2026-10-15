# Boxe Zone

Widget gratuito para Windows 10 e 11: solte arquivos ou pastas no canto da tela e eles são enviados para o **Google Drive**, o **OneDrive**, o **Dropbox** ou o **MEGA**, sem abrir o navegador. Sem cadastro e sem servidor no meio do caminho: os arquivos vão do seu computador direto para o serviço que você escolheu.

*A free Windows 10/11 widget: drop files or folders in a corner of the screen and they are uploaded to Google Drive, OneDrive, Dropbox or MEGA, straight from your computer. Interface in Portuguese and English.*

**Download:** https://weblucasdev.github.io/BoxeZone/ (instaladores x64 e ARM64 nas [Releases](https://github.com/WebLucasDev/BoxeZone/releases)).

## O que faz

- Um switch por serviço; ligado, ganha a sua área de drop no widget. O login é no navegador (Google, Microsoft, Dropbox) ou numa janela do app (MEGA, que não oferece login pelo navegador; a senha nunca é guardada).
- Envia arquivos e pastas inteiras, com progresso real ("3 de 12") e "Cancelar"; nomes repetidos não sobrescrevem nada.
- Continua sozinho de onde parou se a internet cair ou o app fechar (no MEGA, o arquivo interrompido recomeça do zero).
- "Copiar link" depois do envio, no widget e na notificação do Windows.
- Histórico dos últimos 50 envios, com "Abrir" e "Copiar link".
- Pasta de destino escolhida navegando pelas pastas da conta ("Procurar…").
- Fica por cima das outras janelas, recolhe numa faixa na borda da tela e abre e fecha com um atalho (Ctrl+Alt+D, configurável).
- Português e inglês, tema claro e escuro.
- Atualização automática: baixa a versão nova das Releases, confere o SHA256 e instala quando você clica em "Atualizar" (pode ser desligada no instalador ou nas Configurações).

## Privacidade

O app não coleta dados. O acesso às contas fica só no seu computador, criptografado pelo Windows. Detalhes na [política de privacidade](https://weblucasdev.github.io/BoxeZone/privacidade/).

## Instalador

O instalador não tem assinatura digital. Na primeira vez, o navegador e o Windows podem avisar que não o reconhecem:

- No Edge, se o download disser que o arquivo "normalmente não é baixado": **⋯** → **Manter**, depois a seta ao lado de **Excluir** → **Manter mesmo assim**.
- Se aparecer "O Windows protegeu o computador": **Mais informações** → **Executar assim mesmo**.

O [site](https://weblucasdev.github.io/BoxeZone/#instalador) mostra esses passos numa animação. Cada versão é compilada pelo GitHub Actions a partir deste repositório e publicada nas [Releases](https://github.com/WebLucasDev/BoxeZone/releases), com as somas SHA256 em `SHA256SUMS.txt`.

## Compilar

Requisitos: Windows 10/11, .NET 10 SDK e, para o instalador, Inno Setup 6.5.2+.

```powershell
dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64
.\app\installer\build.ps1 -Arch x64 -Version 1.0.0
```

O app precisa de credenciais próprias do Google, da Microsoft e do Dropbox em `app/appsettings.local.json` (o MEGA não pede) (modelo em `app/appsettings.example.json`). Detalhes de arquitetura em `app/docs/arquitetura.md`.

## Estrutura

- `app/`: o app (C#, WinUI 3, .NET 10), o instalador (`app/installer/`) e a documentação (`app/docs/`).
- `website/`: a página do projeto e a política de privacidade.
- `.github/workflows/`: geração dos instaladores e publicação do site.

## Licença

[MIT](LICENSE). O app inclui bibliotecas de terceiros com as próprias licenças: .NET (MIT), Windows App SDK (licença da Microsoft para redistribuição), H.NotifyIcon (MIT), CommunityToolkit.Mvvm (MIT), Google APIs Client Library (Apache 2.0), MSAL.NET (MIT), Dropbox.NET (MIT), MegaApiClient (MIT) e Newtonsoft.Json (MIT).
