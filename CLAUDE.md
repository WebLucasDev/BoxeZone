# BoxeZone

App nativo para Windows 11: uma janelinha flutuante no canto da tela onde o usuário arrasta arquivos e eles são enviados para o **Google Drive**, **OneDrive** ou **Dropbox**. Cada provedor tem um switch; ligado, ganha a sua área de drop e a janela cresce.

Interface em **pt-BR**. Código, nomes, comentários e commits em **inglês**.

O repositório tem duas partes:

| Pasta | O que é |
|---|---|
| `app/` | O app C# (WinUI 3). Os caminhos das seções abaixo são relativos a `app/`. |
| `website/` | Página do projeto e política de privacidade (HTML/CSS puros), publicada em https://weblucasdev.github.io/BoxeZone/. Ver "Site" abaixo. |

| Documento | Quando ler |
|---|---|
| `app/docs/arquitetura.md` | Antes de mexer em janelas, aba/faixa, upload, autenticação, preferências, notificações ou estilos: estados, decisões e as armadilhas que já custaram bugs. |
| `app/docs/proximos-passos.md` | Roadmap, ideias e limitações conhecidas. Atualizar ao começar ou terminar um item. |
| `app/docs/*.dc.html` | Protótipo visual (fonte da verdade). Ver "Visual" abaixo. |

## Stack

- **WinUI 3** (Windows App SDK) + **C# / .NET 10** (`net10.0-windows10.0.19041.0`).
- **Unpackaged e self-contained** (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`): `.exe` comum, sem instalar runtime. `DefaultLanguage=pt-BR`; `PublishTrimmed=false` (os SDKs dos provedores usam reflexão).
- **MVVM** com `CommunityToolkit.Mvvm`: `[ObservableProperty]` em **propriedades parciais** (`public partial string Status { get; set; }`), não em campos.
- Pacotes permitidos: `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`, `CommunityToolkit.Mvvm`, `H.NotifyIcon.WinUI` (bandeja), `Google.Apis.Drive.v3`, `Microsoft.Identity.Client` (MSAL; o Graph é chamado por `HttpClient`, sem o SDK `Microsoft.Graph`) e `Dropbox.Api`. Qualquer outro: perguntar antes.
- Não usar Electron, Tauri, WebView ou WPF.

## Comandos

Da raiz do repositório (`app/` tem o `.csproj` e o `.slnx`, então o projeto vai explícito):

```powershell
dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64
dotnet run --project app/BoxeZone.csproj -c Debug --property:Platform=x64
dotnet publish app/BoxeZone.csproj -c Release -p:Platform=x64   # app\bin\Release\...\win-x64\publish\ (~300 MB)
```

Um BoxeZone aberto trava o `.exe` e o build falha ao copiá-lo: fechar pelo "Sair" da bandeja (ou encerrar o processo) antes de compilar.

## Estrutura

Projeto em `app/`, namespace `BoxeZone`.

```
Program.cs                 # Main próprio: instância única, depois o Main do XAML
App.xaml(.cs)              # monta serviços e janelas; cores (ThemeDictionaries) e estilos
Strings/pt-BR/Resources.resw
Assets/                    # BoxeZone.ico (exe/bandeja), BoxeZone.png (notificações)
appsettings.example.json   # modelo; as credenciais reais ficam em appsettings.local.json (git-ignored)
Views/
  MainWindow               # janela do widget: Frame (WidgetPage/SettingsPage), faixa e painel compacto
  TabWindow                # aba lateral, numa janela própria
  WidgetPage, SettingsPage
  Controls/                # ProviderRow, AccountCard, TabHandle
ViewModels/                # MainViewModel, ProviderViewModel, SettingsViewModel
Services/
  WindowDockService        # estados Normal/Docked/Hidden e posição das janelas
  HotkeyService, SettingsService, TokenStore, NotificationService, StartupService, CrashLog, AppCredentials
  Storage/                 # IStorageProvider + Google Drive, OneDrive, Dropbox, SignInPage
Helpers/                   # Win32 (P/Invoke), WindowExtensions, BindConverters
```

## Regras de arquitetura

- Toda integração passa por `IStorageProvider`; a UI nunca chama SDK de provedor. Falhas saem como `StorageProviderException` (com `StorageErrorKind`) e a UI mostra a mensagem do `.resw`.
- Um provedor com falha não pode derrubar os outros: erros ficam na área de drop dele.
- Uploads sempre `async`, em partes (progresso real), com `IProgress<double>` e `CancellationToken`.
- OAuth de app desktop (navegador do sistema + loopback) nos três provedores. Tokens só no `TokenStore` (criptografados por usuário do Windows): nunca em texto puro, nunca em log.
- Preferências em `%LocalAppData%\BoxeZone\settings.json` (`ApplicationData.Current` não funciona sem pacote).
- Strings de UI em `Strings/pt-BR/Resources.resw` (`x:Uid` no XAML, `ResourceLoader` no código), nunca fixas.

## Armadilhas (detalhes em `app/docs/arquitetura.md`)

- **Tamanho e posição de janela** só por `WindowExtensions` (moldura visível, mover + redimensionar juntos, conteúdo num `Canvas`).
- **Topmost**: o widget é possuído pela aba e isso tira o topmost; ele é reaplicado a cada show (`ApplyTopmost`).
- **Largura mínima** do Windows (~70 px) é removida por subclasse (`WM_WINDOWPOSCHANGING`); sem isso aba e faixa engordam.
- **Arrasto do widget** (pelo corpo; a aba só recolhe) lê o cursor por `GetCursorPos` a cada quadro; o WinUI não manda `PointerMoved` quando o cursor sai da janela.
- **Notificações**: `AppNotificationManager` não funciona self-contained; usar `Windows.UI.Notifications` com o AUMID `BoxeZone.App`.
- **Tema** se aplica no `Window.Content`, senão o Acrylic não acompanha.
- **Lightweight styling** com o mesmo valor nos dois temas vai no dicionário principal do `App.xaml`, não nos `ThemeDictionaries`.
- Exceção no `OnLaunched` derruba o app em silêncio: ver `%LocalAppData%\BoxeZone\crash.log`.

## Visual

O protótipo em `app/docs/` é a **fonte da verdade visual**: antes de criar ou alterar uma tela, ler o arquivo dela e reproduzir medidas, cores, espaçamentos, textos e estados.

| Arquivo | Tela |
|---|---|
| `app/docs/widget.dc.html` | Widget: cabeçalho, aba, linhas de provedor, áreas de drop, upload, rodapé |
| `app/docs/configuracoes.dc.html` | Configurações |
| `app/docs/estados-janela.dc.html` | Normal, Docked, Hidden e o painel compacto (esboço; nos detalhes do widget vale o `widget.dc.html`) |

- São HTML de protótipo (dependem de um `support.js` ausente): ler como texto. Valores exatos nos `style="…"` e no `<style>` do `<helmet>`; `<sc-for>`/`<sc-if>`/`{{…}}` equivalem a `ItemsRepeater`, `Visibility` e `x:Bind`; o `<script>` final mostra estados e lógica. O que fica fora do widget é cenário.
- Não copiar HTML/CSS/JS. Sem equivalente nativo: usar o controle WinUI mais próximo e avisar.
- O protótipo só tem o tema escuro: os valores dele vão no `ThemeDictionary` Dark; o Light usa os recursos equivalentes do WinUI. Cores sempre como recursos, nunca fixas nos controles.
- Destaque `#4CC2FF`. Fundo `DesktopAcrylicBackdrop`. Fonte padrão do WinUI (Segoe UI Variable). Referências: EarTrumpet, Dropover, Fluent do Windows 11.
- Pouco texto, nada de emoji. Controles nativos antes de controles próprios.

## Site (`website/`)

- HTML e CSS puros, sem framework nem build; mesma paleta e fonte do app. Publicado pelo workflow `.github/workflows/pages.yml` a cada push na `main` que mude `website/`.
- Endereço base `/BoxeZone/`: links relativos; o `404.html` usa caminhos absolutos com `/BoxeZone/`.
- A política de privacidade (`website/privacidade/`) descreve o que o app faz. Mudou permissões dos provedores, dados guardados ou o login no app: atualizar a política e a data dela no mesmo trabalho.

## Ao trabalhar neste projeto

- Mudanças pequenas e compiláveis; `dotnet build` sem avisos antes de concluir.
- Mudou UI ou comportamento de janela: rodar o app e conferir (contra o protótipo, nos dois temas, e com o widget por cima de outros apps).
- Trabalho em branch; merge e push na `main` só depois de o usuário testar.
- Não commitar credenciais, tokens nem `appsettings.local.json`.
- Registrar no `app/docs/arquitetura.md` toda armadilha nova que custar um bug; manter o `CLAUDE.md` curto.
