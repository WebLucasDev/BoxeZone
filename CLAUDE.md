# BoxeZone

App nativo para Windows 11: uma janelinha flutuante no canto da tela onde o usuário arrasta arquivos e eles são enviados direto para o **Google Drive**, **OneDrive** ou **Dropbox**. Cada provedor tem um switch; quando ligado, ganha a sua própria área de drop e a janela cresce.

Idioma da interface: **português (pt-BR)**. Código, nomes de classes, métodos e commits: **inglês**.

## Stack

- **WinUI 3** (Windows App SDK) + **C# / .NET 10** (`net10.0-windows10.0.19041.0`)
- **Unpackaged**, distribuído como `.exe` comum. O projeto nasceu do template empacotado; o `.csproj` deve ter:
  - `<WindowsPackageType>None</WindowsPackageType>`
  - `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` (roda sem instalar o runtime do Windows App SDK)
  - `<DefaultLanguage>pt-BR</DefaultLanguage>` (os recursos só existem em pt-BR)
  - `<PublishTrimmed>false</PublishTrimmed>` também no Release (os SDKs dos provedores usam reflexão)
  - O `Package.appxmanifest` e o perfil `MsixPackage` do `launchSettings.json` não são usados.
- **MVVM** com `CommunityToolkit.Mvvm`: `ObservableObject`, `[RelayCommand]` e `[ObservableProperty]` em **propriedades parciais** (`public partial string Status { get; set; }`), não em campos.
- Ícone de bandeja: `H.NotifyIcon.WinUI`
- Provedores:
  - Google Drive: `Google.Apis.Drive.v3`
  - OneDrive: `Microsoft.Graph` + `Microsoft.Identity.Client` (MSAL)
  - Dropbox: `Dropbox.Api`
- Não usar Electron, Tauri, WebView ou WPF.

Pacotes NuGet permitidos: `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools` e os listados acima. Qualquer outro, perguntar antes.

## Comandos

```powershell
dotnet restore
dotnet build -c Debug -p:Platform=x64
dotnet run -c Debug --property:Platform=x64
```

## Estrutura

Os arquivos do projeto ficam na raiz do repositório. Namespace raiz: `BoxeZone`.

```
BoxeZone.csproj / BoxeZone.slnx
App.xaml(.cs)                # startup: instância única, janela, ícone de bandeja, hotkey; cores em ThemeDictionaries
app.manifest
appsettings.example.json     # modelo das credenciais, campos vazios (commitado)
appsettings.local.json       # credenciais reais (no .gitignore)
Assets/
Strings/pt-BR/Resources.resw # todas as strings de UI
docs/                        # protótipos HTML: fonte da verdade visual
Views/
  MainWindow.xaml(.cs)       # janela sem borda; hospeda um Frame e os estados Docked (faixa e painel compacto)
  WidgetPage.xaml(.cs)       # cabeçalho, aba lateral, linhas de provedor, áreas de drop, rodapé
  SettingsPage.xaml(.cs)     # contas, comportamento, atalho e tema
  Controls/
    ProviderRow.xaml(.cs)    # uma linha de provedor + a área de drop dele
ViewModels/
  MainViewModel.cs
  ProviderViewModel.cs       # estado de um provedor: ligado, conectado, upload em andamento
  SettingsViewModel.cs
Services/
  Storage/
    IStorageProvider.cs
    GoogleDriveProvider.cs
    OneDriveProvider.cs
    DropboxProvider.cs
  WindowDockService.cs       # estados Normal / Docked / Hidden, snap na borda
  HotkeyService.cs           # RegisterHotKey via P/Invoke
  SettingsService.cs         # preferências em JSON em %LocalAppData%\BoxeZone
  TokenStore.cs              # tokens criptografados por usuário do Windows
Helpers/
  Win32.cs                   # P/Invoke (GetCursorPos, RegisterHotKey, SetWindowSubclass etc.)
```

Configurações abre **na mesma janela**: o `Frame` navega de `WidgetPage` para `SettingsPage`, a janela cresce de 300 para 440 px de largura, e "Voltar" retorna ao widget.

## Arquitetura

Toda integração passa por uma interface única. A UI nunca chama SDK de provedor diretamente.

```csharp
public interface IStorageProvider
{
    string Id { get; }            // "gdrive" | "onedrive" | "dropbox"
    string DisplayName { get; }
    bool IsConnected { get; }
    string? AccountEmail { get; } // exibido em Configurações ("Conectado · e-mail")
    Task ConnectAsync(CancellationToken ct);     // fluxo OAuth no navegador
    Task DisconnectAsync();
    Task<string> UploadAsync(StorageFile file, string targetFolder,
                             IProgress<double> progress, CancellationToken ct); // retorna link do arquivo
}
```

- Uploads são sempre `async`, com `IProgress<double>` para a barra de progresso e `CancellationToken`.
- Arquivos grandes usam o upload em partes/retomável de cada provedor (Drive: resumable upload; Graph: upload session; Dropbox: upload session acima de 150 MB).
- Atualizações de UI vindas de outra thread passam por `DispatcherQueue.TryEnqueue`.
- Um provedor com falha não pode derrubar os outros: erros são capturados por upload e exibidos na própria área de drop.
- Arquivos chegam por drag and drop: `AllowDrop="True"`, `DragOver` com `AcceptedOperation = Copy`, `Drop` lê `e.DataView.GetStorageItemsAsync()`. O drop não funciona se o app rodar como administrador.

## Autenticação

- Todos usam OAuth de **app desktop** (navegador do sistema + redirect em loopback):
  - **Google**: credencial "Desktop app" (client ID + client secret), escopo `drive.file`. Esse escopo só enxerga o que o próprio app criou, então "Alterar" a pasta de destino é um caminho digitado (o app cria a pasta), não um navegador do Drive. E-mail da conta via `about.get` (`fields=user`).
  - **Microsoft**: registro no Entra ID, cliente público, redirect `http://localhost`, navegador do sistema (`WithUseEmbeddedWebView(false)`), escopo `Files.ReadWrite`. E-mail via `IAccount.Username`.
  - **Dropbox**: OAuth com PKCE (só App key, sem secret), permissões `files.content.write` e `account_info.read`. Sem `sharing.write`, o "Abrir" leva à pasta no Dropbox web em vez de criar link compartilhado.
- Tokens ficam no `TokenStore`, criptografados por usuário do Windows com `Windows.Security.Cryptography.DataProtection.DataProtectionProvider("LOCAL=user")`, em arquivos em `%LocalAppData%\BoxeZone`. Não usar `ProtectedData` (exige outro NuGet) nem `PasswordVault` (limite de tamanho, o cache do MSAL não cabe).
  - Google: implementar `IDataStore` sobre o `TokenStore`; nunca usar `FileDataStore` (grava JSON em texto puro).
  - MSAL: serializar o token cache pelos callbacks `SetBeforeAccess`/`SetAfterAccess`.
- Tokens nunca em texto puro, nunca em log.
- Client IDs e secrets ficam em `appsettings.local.json`, que deve estar no `.gitignore`. Manter `appsettings.example.json` com os campos vazios.

## Janela e comportamento

Três estados, controlados pelo `WindowDockService`:

1. **Normal**: widget de 300 px de largura (altura acompanha o conteúdo), a 16 px do canto inferior direito da `DisplayArea.WorkArea`, sempre no topo.
2. **Docked** ("Encostada" no protótipo): faixa de 10–12 px de largura e ~104–120 px de altura colada na borda direita, na cor de destaque com brilho suave e um traço vertical no centro.
   - Mouse em cima: a faixa alarga para 16 px. Clique: volta ao Normal.
   - Arquivo arrastado sobre ela (`DragEnter`): abre um painel compacto (~200 px, colado na borda) só com as áreas de drop dos provedores ligados, destacando a zona sob o cursor. Recolhe quando o arrasto sai, exceto durante upload.
3. **Hidden** ("Escondida"): janela oculta (`AppWindow.Hide()`), processo continua rodando; volta pelo ícone da bandeja ou pela hotkey.

Transições:
- Botão "–" (dica "Minimizar") → Docked. Botão "X" (dica "Fechar") → Hidden, **não** encerra.
- Aba lateral esquerda (15×80 px) é a alça de arrastar: mesma cor do container em repouso, cor de destaque enquanto pressionada. Soltar perto da borda → Docked; além da borda → Hidden.
- "Recolher após o envio" ligado → Docked quando o upload termina.
- Encerrar o app só pelo menu do ícone da bandeja ("Sair").

Implementação nativa:
- Sem barra de título: `OverlappedPresenter.SetBorderAndTitleBar(true, false)` com `IsResizable`, `IsMaximizable` e `IsMinimizable` em `false`. Isso mantém os cantos arredondados, a sombra e a borda de 1 px do Windows 11. Os botões "–" e "X" são desenhados no XAML (44×36 px; "X" fica `#C42B1C` no hover).
- `AppWindow.IsShownInSwitchers = false` (fora da barra de tarefas e do Alt+Tab).
- `AppWindow` trabalha em pixels físicos: multiplicar tamanhos pela escala (`GetDpiForWindow / 96` ou `XamlRoot.RasterizationScale`).
- Sem barra de título, a área XAML também cobre a legenda escondida, então `ResizeClient` deixa a janela maior que o conteúdo: redimensionar pela diferença entre o tamanho desejado e `XamlRoot.Size`. Posicionar pela moldura visível (`DWMWA_EXTENDED_FRAME_BOUNDS`), porque o retângulo da janela inclui bordas invisíveis de ~8 px (ver `MainWindow.FitToContent`).
- O conteúdo da janela fica dentro de um `Canvas`: assim ele é medido sem o tamanho atual da janela como limite e pode pedir mais altura. Sem isso, a janela nunca cresce.
- No protótipo a aba lateral fica **fora** do container (`left: -15px`). Uma janela nativa é retangular e o Acrylic preenche tudo, então isso não sai de graça. Decidir na etapa 3 (aba dentro da janela, área transparente ou região de janela) e avisar antes de implementar.
- Hotkey padrão `Ctrl+Alt+D` alterna Normal/Hidden; configurável. `RegisterHotKey` exige receber `WM_HOTKEY`: fazer subclass da janela com `SetWindowSubclass`, porque o WinUI controla o `WndProc`.
- Instância única (`AppInstance.FindOrRegisterForKey`): abrir o `.exe` de novo traz a janela existente para o Normal.
- Animações de transição curtas (150–250 ms).

## Diretrizes visuais

O protótipo aprovado está em `docs/` e é a **fonte da verdade visual**. Antes de criar ou alterar qualquer tela, leia o arquivo correspondente e reproduza medidas, cores, espaçamentos, textos e estados:

| Arquivo | Tela |
|---|---|
| `docs/widget.dc.html` | Widget principal: cabeçalho, aba lateral, linhas de provedor, áreas de drop, upload, rodapé, estado recolhido |
| `docs/configuracoes.dc.html` | Tela de Configurações |
| `docs/estados-janela.dc.html` | Os três estados da janela (Normal, Docked, Hidden) e o painel compacto ao arrastar |

Como ler esses arquivos (são protótipos HTML, não código para portar):
- Eles dependem de um `support.js` que não está no repositório. Ler como texto, não abrir no navegador.
- Os `style="…"` inline e o bloco `<style>` em `<helmet>` têm os valores exatos (px, cores, raios, sombras). Converter para XAML: `px` → unidades efetivas do WinUI, `rgba(...)` → `Color` com alfa, `backdrop-filter` → `DesktopAcrylicBackdrop`.
- `<sc-for>`, `<sc-if>` e `{{...}}` são a sintaxe de template do protótipo: equivalem a `ItemsRepeater`/`ListView`, `Visibility` com binding e `{x:Bind}`.
- O `<script>` com `class Component` no fim mostra os estados e a lógica de interação (switches, progresso do upload, recolher/expandir); usar como referência para os ViewModels.
- O que fica fora do widget é só cenário do protótipo: fundo com manchas, título "POC · BoxeZone", "Clique na faixa para expandir", "Rodando na bandeja · clique para reabrir".
- `estados-janela.dc.html` é um esboço simplificado. Em detalhes do widget (por exemplo, ele mostra só os destinos ativos), vale o `widget.dc.html`, que lista os três provedores com switch.
- Não copiar HTML, CSS ou JS para o projeto. Se algo do protótipo não tiver equivalente nativo no WinUI, escolher o controle nativo mais próximo e avisar.

Referência de estilo geral: EarTrumpet, Dropover e Fluent do Windows 11.

- Fundo `DesktopAcrylicBackdrop`; tema segue o sistema (claro/escuro), com opção nas configurações.
- O protótipo só mostra o tema **escuro**. Os valores dele valem para o escuro; o claro usa os recursos de tema do WinUI equivalentes. Cores ficam em `ThemeDictionaries` (Dark/Light), nunca fixas nos controles.
- Sobrescrever recursos de controles nativos (lightweight styling, como `ToggleSwitchFillOn`) no dicionário principal do `App.xaml` quando o valor é igual nos dois temas: dentro dos `ThemeDictionaries` do App a sobrescrita do `ToggleSwitch` não teve efeito.
- Cor de destaque: `#4CC2FF`, definida como recurso. As alternativas `#60CDBA` e `#F7A35C` do protótipo são só opções de design, não uma configuração do usuário.
- Container: largura 300 px (widget) ou 440 px (Configurações), cantos de 8 px, fundo `rgba(32,34,40,.72)`, borda de 1 px `rgba(255,255,255,.12)`, sombra suave. Linhas e áreas de drop: cantos de 6 px.
- Fonte: Segoe UI Variable (padrão do WinUI). Título do cabeçalho 12 px semibold, nomes 13 px, status 11 px secundário (`#B3BCC8` no escuro).
- Cabeçalho (36 px): logo quadrado de 20 px na cor de destaque, "BoxeZone", botões "–" e "X".
- Linha de provedor: ícone de 28 px (no protótipo, a inicial "G", "O" ou "D" num quadrado), nome, status de uma linha ("Pronto", "Enviando…", "Desligado") e `ToggleSwitch` nativo, sem o texto On/Off (`OnContent`/`OffContent` vazios, `MinWidth` 0). Nenhum ligado: "Ative um destino para começar."
- Área de drop (altura mínima 64 px): borda tracejada, ícone de upload + "Solte arquivos aqui"; durante upload mostra nome do arquivo, porcentagem e barra fina (3 px); ao terminar, "Enviado · nome" com ação "Abrir".
- Rodapé: atalho em destaque ("Ctrl+Alt+D") + "abre e fecha", e o link "Configurações".
- Configurações: seções "Contas" (Conectar/Desconectar, pasta de destino com "Alterar"), "Comportamento" (quatro switches com descrição) e "Atalho e aparência" (atalho com "Alterar", tema Sistema/Claro/Escuro).
- Pouco texto, nada de emoji. Usar controles nativos do WinUI antes de criar controles próprios.

## Configurações persistidas

Provedores ligados, pasta de destino por provedor (padrão `/BoxeZone`), iniciar com o Windows, sempre no topo, recolher após envio, notificar ao concluir (notificação do Windows), hotkey, tema, última posição/estado da janela.

## Armadilhas do modo unpackaged

- `ApplicationData.Current` (e `LocalSettings`) falha sem pacote: usar arquivos em `%LocalAppData%\BoxeZone`.
- Iniciar com o Windows: chave `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` com o argumento `--startup`, que abre direto em Hidden ("Abre direto na bandeja"). `StartupTask` só existe para apps empacotados.
- Notificações: `AppNotificationManager` do Windows App SDK, registrado no startup.
- Strings: `x:Uid` no XAML e `ResourceLoader` (de `Microsoft.Windows.ApplicationModel.Resources`) no código.

## Ordem de desenvolvimento

0. Base do projeto: renomear para `BoxeZone` (`.csproj`, `.slnx`, namespace), migrar para .NET 10, configurar unpackaged conforme a seção Stack, `git init` + `.gitignore` (`bin/`, `obj/`, `.vs/`, `*.user`, `appsettings.local.json`), pastas, `Resources.resw` e `appsettings.example.json`.
1. Janela sem borda no canto, Acrylic, ícone de bandeja, "X" escondendo e "Sair" encerrando.
2. Lista de provedores com switches e áreas de drop expandindo (sem upload real).
3. `WindowDockService`: estados Normal/Docked/Hidden, aba lateral e painel compacto ao arrastar.
4. Hotkey global.
5. `IStorageProvider` + Google Drive ponta a ponta (auth, upload, progresso).
6. OneDrive e Dropbox.
7. Tela de configurações e persistência.
8. Notificações, iniciar com o Windows, polimento de animações.

## Ao trabalhar neste projeto

- Fazer mudanças pequenas e compiláveis; rodar `dotnet build` antes de concluir uma tarefa.
- Ao terminar uma etapa com UI, rodar o app e comparar com o protótipo.
- Não adicionar pacotes NuGet além dos listados sem perguntar.
- Não commitar credenciais, tokens nem `appsettings.local.json`.
- Strings de UI em pt-BR centralizadas em `Strings/pt-BR/Resources.resw`, não fixas no XAML.
