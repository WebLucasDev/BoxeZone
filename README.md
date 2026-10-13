# Boxe Zone

Widget gratuito para Windows 10 e 11: arraste arquivos para o canto da tela e eles são enviados para o **Google Drive**, o **OneDrive** ou o **Dropbox**. Sem conta própria e sem servidor no meio do caminho: os arquivos vão do seu computador direto para o serviço que você escolheu.

*A free Windows 10/11 widget: drop files in a corner of the screen and they are uploaded to Google Drive, OneDrive or Dropbox, straight from your computer. Interface in Brazilian Portuguese.*

**Download:** https://weblucasdev.github.io/BoxeZone/ (instaladores x64 e ARM64 nas [Releases](https://github.com/WebLucasDev/BoxeZone/releases)).

## O que faz

- Um switch por serviço; ligado, ganha a sua área de drop no widget.
- Envio em partes, com progresso real; nomes repetidos não sobrescrevem arquivos.
- Fica por cima das outras janelas, recolhe numa faixa na borda da tela e abre e fecha com um atalho (Ctrl+Alt+D, configurável).
- Notificação ao concluir, com link para o arquivo enviado.
- Avisa quando há versão nova (pode ser desligado no instalador ou nas Configurações).

## Privacidade

O app não coleta dados. O acesso às contas fica só no seu computador, criptografado pelo Windows. Detalhes na [política de privacidade](https://weblucasdev.github.io/BoxeZone/privacidade/).

## Instalador

O instalador ainda não tem assinatura digital: na primeira vez, o Windows mostra "O Windows protegeu o computador" (clique em Mais informações e depois em Executar assim mesmo). Cada versão é compilada pelo GitHub Actions a partir deste repositório e publicada nas [Releases](https://github.com/WebLucasDev/BoxeZone/releases).

## Compilar

Requisitos: Windows 10/11, .NET 10 SDK e, para o instalador, Inno Setup 6.5.2+.

```powershell
dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64
.\app\installer\build.ps1 -Arch x64 -Version 1.0.0
```

O app precisa de credenciais próprias dos três serviços em `app/appsettings.local.json` (modelo em `app/appsettings.example.json`). Detalhes de arquitetura em `app/docs/arquitetura.md`.

## Estrutura

- `app/`: o app (C#, WinUI 3, .NET 10), o instalador (`app/installer/`) e a documentação (`app/docs/`).
- `website/`: a página do projeto e a política de privacidade.
- `.github/workflows/`: geração dos instaladores e publicação do site.

## Licença

[MIT](LICENSE). O app inclui bibliotecas de terceiros com as próprias licenças: .NET (MIT), Windows App SDK (licença da Microsoft para redistribuição), H.NotifyIcon (MIT), CommunityToolkit.Mvvm (MIT), Google APIs Client Library (Apache 2.0), MSAL.NET (MIT) e Dropbox.NET (MIT).
