# BoxeZone: arquitetura e armadilhas

Detalhes de implementação que não cabem no `CLAUDE.md`. Ler a seção correspondente antes de mexer em janelas, autenticação, upload, preferências ou notificações: quase tudo aqui veio de um bug real.

## Janelas

### Estados (`WindowDockService`)

1. **Normal**: widget de 300 px (Configurações: 440 px), altura acompanha o conteúdo. Primeira abertura a 16 px do canto inferior direito da `DisplayArea.WorkArea`.
2. **Docked** ("Encostada" no protótipo): faixa de 12 px × 104 px na borda direita, na cor de destaque, com um traço escuro no centro. Hover alarga para 16 px; clique volta ao Normal.
   - **DockedExpanded**: um arquivo arrastado sobre a faixa (`DragEnter`) abre um painel de 200 px só com as áreas de drop dos provedores ligados (`ProviderRow` com `IsCompact`). Recolhe 300 ms depois de o cursor sair, exceto durante envio; depois de um "Enviado", espera 1,5 s.
3. **Hidden**: `AppWindow.Hide()`, processo segue rodando; volta pela bandeja, pela hotkey ou abrindo o `.exe` de novo.

Transições:
- "–" ou clique na aba → Docked. "X" e Alt+F4 → Hidden (o `Closing` é cancelado). Só "Sair" na bandeja encerra.
- Arrastar o widget pelo corpo só move: fica onde foi solto, ajustado para dentro da área de trabalho do monitor sob o centro (ou do mais próximo). Não recolhe nem esconde.
- Hotkey: em Normal esconde; em qualquer outro estado traz para o Normal.
- "Recolher após o envio": Docked 1,5 s depois do "Enviado", se nada mais estiver enviando.
- Hidden ou Docked sempre voltam para a `WidgetPage` (Configurações só existe no widget aberto).

Ao crescer (outra página, provedor ligado), o widget cresce para longe das bordas mais próximas: na metade de baixo mantém a base, na metade direita mantém a lateral direita.

### Janela sem borda

- `OverlappedPresenter.SetBorderAndTitleBar(true, false)` com `IsResizable`, `IsMaximizable` e `IsMinimizable` em `false`: mantém cantos arredondados, sombra e borda de 1 px do Windows 11. Os botões "–" e "X" são do XAML (44×36 px; "X" vira `#C42B1C` no hover). `IsShownInSwitchers = false` tira da barra de tarefas e do Alt+Tab.
- `AppWindow` trabalha em pixels físicos (`GetDpiForWindow / 96`).
- **Tamanho**: sem barra de título, a área XAML cobre também a legenda escondida, então `ResizeClient` deixa a janela maior que o conteúdo. `WindowExtensions` mede uma vez por escala a diferença entre a janela e `XamlRoot.Size` e calcula o tamanho por ela.
- **Posição**: pela moldura visível (`DWMWA_EXTENDED_FRAME_BOUNDS`); o retângulo da janela inclui bordas invisíveis de ~8 px. As bordas só podem ser medidas com a janela visível, então ficam em cache.
- **Mover e redimensionar juntos** (`SetVisibleBounds` → `AppWindow.MoveAndResize`). Em dois passos, a janela invade por um instante o monitor vizinho e o Windows a reescala para a escala dele.
- **Largura mínima**: Windows e WinUI impõem ~70 px a janelas com borda. `PreferredMinimumWidth` e `WM_GETMINMAXINFO` não bastam; a subclasse `Win32.RemoveMinimumSize` restaura o tamanho pedido em `WM_WINDOWPOSCHANGING`. Sem ela a aba (23 px) e a faixa (12 px) não ficam finas.
- **Crescer com o conteúdo**: o conteúdo fica dentro de um `Canvas`, que mede sem o tamanho atual da janela como limite. Sem isso, a janela nunca cresce.

### Aba lateral (`TabWindow`)

- No protótipo a aba fica **fora** do container (`left: -15px`); uma janela nativa é retangular, então a aba é uma janela própria: 15 px visíveis + 8 px escondidos atrás do widget.
- O widget é "possuído" pela aba (`GWLP_HWNDPARENT`), o que o mantém sempre por cima dela e esconde os cantos direitos da aba.
- Trocar o dono **tira o topmost do widget**: ele some atrás de outros apps e só a aba fica visível. `Win32.SetTopmost` é reaplicado ao definir o dono e a cada show (`WindowDockService.ApplyTopmost`).
- A aba tem `WS_EX_NOACTIVATE` para não tirar o foco do widget; precisa de um `Activate()` uma vez para o XAML carregar.
- A aba só recolhe (não arrasta). O clique age no `PointerPressed`, como a faixa: com captura do ponteiro e ação no `PointerReleased`, o clique não chegava a recolher.
- **A borda invisível do widget cobre a aba**: os 8 px de borda de redimensionamento à esquerda da moldura visível ficam por cima dos últimos 8 px visíveis da aba (o widget está acima dela), e esses cliques iam para o widget. `HTTRANSPARENT` não resolve: o Windows entrega o clique à janela de topo da aba, não à janela filha onde o WinUI trata a entrada. A solução: `Win32.ReportInvisibleFrameClicks` faz o widget responder `HTBORDER` fora da moldura e, no `WM_NCLBUTTONDOWN`, avisar o ponto; o `WindowDockService` recolhe se ele cair na aba.

### Arrastar o widget

- Começa num `PointerPressed` que nenhum controle tratou: `Root.AddHandler(PointerPressedEvent, …, handledEventsToo: false)` na `MainWindow`. Botões, switches, links e caixas de texto marcam o evento como tratado e continuam funcionando; texto, cabeçalho e espaços vazios arrastam. Só com mouse e só no modo Widget (faixa e painel compacto não arrastam).
- `WindowDrag` só começa a mover depois do limite de arrasto do sistema (`SM_CXDRAG`/`SM_CYDRAG`), para um clique não mexer a janela.
- A janela anda sob o cursor e o WinUI deixa de mandar `PointerMoved`, então o arrasto lê `GetCursorPos` e o estado do botão (`GetAsyncKeyState`, respeitando botões trocados) a cada quadro em `CompositionTarget.Rendering`. A aba acompanha a cada passo (`PlaceTab`).
- Durante o arrasto, `UpdatePlacement` é ignorado: atravessar para um monitor de outra escala redimensiona a janela no meio do arrasto.

### Faixa Docked

- Ao alargar no hover, a janela muda de tamanho sob o cursor e o WinUI manda um `PointerExited` falso, e depois dele pode não mandar nenhum quando o cursor sai de verdade (a faixa ficava larga). Quem decide é a posição real do cursor: conferida no `PointerExited` e num timer de 50 ms enquanto a faixa está larga.
- Para testar hover com scripts: `SetCursorPos` não gera eventos de ponteiro no WinUI; usar `mouse_event`/`SendInput` com movimento.
- O clique usa `PointerPressed`, não `Tapped` (o redimensionamento atrapalhava o toque).

### Sempre no topo

- Widget, faixa e painel compacto ficam **sempre** por cima dos outros apps; não há mais preferência (o switch "Sempre no topo" saiu: desligado, o widget sumia atrás do navegador e só a aba ficava visível).
- `ApplyTopmost` liga `OverlappedPresenter.IsAlwaysOnTop` junto com `SetTopmost`: os dois precisam concordar, senão o WinUI reaplica o valor do presenter ao ativar a janela.

### Foco

- Na ativação, o WinUI dá foco de teclado ao primeiro controle (com retângulo de foco). As páginas têm `IsTabStop="True"` sem visual de foco e se focam ao carregar; o Tab continua chegando aos controles. Os botões de legenda não são tab stops, como os nativos.

### Hotkey e instância única

- `RegisterHotKey` exige receber `WM_HOTKEY`; o WinUI controla o `WndProc`, então o `HotkeyService` usa `SetWindowSubclass`. O atalho fica registrado na **janela da aba**, que é criada antes do widget (o widget depende das preferências, que dependem do atalho).
- Se outro app já usa o atalho, o registro falha e a tela de Configurações avisa; a bandeja continua funcionando.
- `Program.Main` próprio (`DISABLE_XAML_GENERATED_MAIN`): `AppInstance.FindOrRegisterForKey("BoxeZone")`. Uma segunda abertura chama `AllowSetForegroundWindow`, redireciona a ativação e encerra; a instância que roda volta ao Normal.

### Animações

- Trocas de modo e de página: fade de 180 ms só no conteúdo (`ContentFrame`, faixa ou painel). O fundo não anima, senão o tema escuro pisca o Acrylic mais claro. O tamanho da janela muda na hora: animar o tamanho de uma janela nativa fica travado.
- Área de drop: fade + 4 px de deslize (180 ms) ao aparecer (`SetImplicitShowAnimation`).

## Provedores e upload

```csharp
public interface IStorageProvider
{
    string Id { get; }            // "gdrive" | "onedrive" | "dropbox"
    string DisplayName { get; }
    bool IsConnected { get; }
    string? AccountEmail { get; }
    Task RestoreAsync(CancellationToken ct);     // sessão salva, nunca abre o navegador
    Task ConnectAsync(CancellationToken ct);     // tenta a sessão salva, depois OAuth no navegador
    Task DisconnectAsync();
    Task<string> UploadAsync(StorageFile file, string targetFolder,
                             IProgress<double> progress, CancellationToken ct); // link do arquivo
}
```

- Falhas saem como `StorageProviderException` com `StorageErrorKind` (NotConfigured, AccessDenied, SessionExpired, Offline, QuotaExceeded, Unknown). Cada provedor traduz as exceções do seu SDK; a UI só conhece o tipo e mostra a mensagem do `.resw`. `SessionExpired` também apaga a sessão local.
- Ligar o switch de um provedor não conectado inicia o login ("Conectando…", área de drop escondida, limite de 2 min; desligar cancela). Nas Configurações, durante o login, "Conectar" vira "Cancelar" (comando com `AllowConcurrentExecutions`, senão o próprio login que ele iniciou o deixa desabilitado). Falhou ou foi cancelado: o switch desliga e o status mostra o motivo.
- Fechar a aba do navegador **não avisa o app**: nenhuma requisição chega ao endereço de retorno (loopback). Por isso o limite de tempo e o cancelar. Cancelar libera o endereço: Google e OneDrive usam uma porta nova a cada login; o Dropbox reusa `localhost:53682` (o `HttpListener` é parado no cancelamento). Os endereços ficam no `http.sys`, não no processo: `netsh http show servicestate view=requestq` para conferir.
- Falha no envio: "Não foi possível enviar · nome", o motivo e "Tentar de novo", que reenvia a partir do arquivo que falhou (reconectando antes, se preciso). Um provedor com falha não afeta os outros.
- Todo envio é em partes, para ter progresso real: Drive resumable upload (8 MB), Graph upload session (10 MiB, múltiplo de 320 KiB; arquivo vazio vai num PUT simples) e Dropbox upload session (8 MB; o envio simples não informa progresso e para em 150 MB).
- Nome repetido não sobrescreve: Graph `conflictBehavior=rename`, Dropbox `WriteMode.Add` + `autorename`.
- `Progress<T>` criado na UI devolve o progresso na thread da UI.
- Drop: `GetStorageItemsAsync` com `GetDeferral()`; pastas soltas são ignoradas. O drop não funciona se o app rodar como administrador.

### Google Drive

- Credencial "Desktop app" (client ID + secret), escopo `drive.file`: o app só enxerga o que ele mesmo criou. Por isso a pasta de destino é um caminho digitado (o app cria a pasta), não um navegador do Drive.
- `GoogleWebAuthorizationBroker` + `LocalServerCodeReceiver` (loopback). E-mail via `about.get` (`fields=user`).
- Tokens pelo `GoogleTokenDataStore` (sobre o `TokenStore`); nunca `FileDataStore`, que grava JSON em texto puro.
- Pasta: procura cada segmento sob `root` (`mimeType = folder and name = … and 'pai' in parents`) e cria os que faltam; cache por caminho, limpo em qualquer falha.

### OneDrive

- Só MSAL (`Microsoft.Identity.Client`) + `HttpClient`. O SDK `Microsoft.Graph` ficou de fora de propósito: duas chamadas não justificam dezenas de MB.
- Cliente público, autoridade `common` (contas pessoais e corporativas), redirect `http://localhost`, navegador do sistema. Escopo `https://graph.microsoft.com/Files.ReadWrite`. E-mail via `IAccount.Username`.
- Cache de tokens do MSAL serializado no `TokenStore` (`SetBeforeAccessAsync`/`SetAfterAccessAsync`, base64).
- `createUploadSession` em `/me/drive/root:/{caminho}:` cria as pastas que faltam. A URL da sessão já é autenticada (sem header). Em falha, a sessão é cancelada (DELETE, melhor esforço). "Abrir" usa o `webUrl`.
- "Desconectar" só apaga a conta do cache local; o MSAL não revoga o token no servidor.

### Dropbox

- PKCE (`PKCEOAuthFlow`), só App key. Um `HttpListener` em `http://localhost:53682/` recebe o retorno; esse endereço precisa estar cadastrado **exatamente** assim em Redirect URIs, com "Allow public clients" ligado. O `state` é conferido.
- Permissões `files.content.write` e `account_info.read`. Sem `sharing.write`, "Abrir" leva à pasta no dropbox.com com o arquivo em pré-visualização.
- Guarda só o refresh token no `TokenStore`; o `DropboxClient` renova sozinho.

### Tokens e credenciais

- `TokenStore`: `DataProtectionProvider("LOCAL=user")` (DPAPI pelo WinRT), um arquivo por chave em `%LocalAppData%\BoxeZone\tokens`. Não usar `ProtectedData` (exige outro NuGet) nem `PasswordVault` (limite de tamanho; o cache do MSAL não cabe).
- Tokens nunca em texto puro, nunca em log.
- `appsettings.local.json` (git-ignored) é copiado para a pasta do `.exe` e lido por `AppCredentials` (`JsonDocument`). Provedor sem valores = `NotConfigured`.
- `SignInPage`: página mostrada no navegador ao fim do login dos três provedores (logo, uma frase, claro/escuro pelo navegador).

## Preferências (`SettingsService`)

- `%LocalAppData%\BoxeZone\settings.json` (System.Text.Json, camelCase, enums como texto). Grava 500 ms depois da última mudança, via arquivo temporário; "Sair" grava na hora. Arquivo ilegível volta aos padrões. `ApplicationData.Current` não funciona sem pacote.
- Ao abrir, um provedor só volta ligado se a sessão salva reconectar; senão o switch fica desligado (sem abrir o navegador sozinho) e a preferência salva não é apagada.
- Posição e estado: voltam só se ainda estiverem em algum monitor; recolhida abre recolhida; Hidden não é restaurado (só o `--startup` abre escondido).
- Pasta de destino: editada no próprio cartão ("Alterar" vira caixa de texto; Enter salva, Esc cancela). `\` vira `/`, espaços nas pontas saem; `<>:"|?*` e a raiz pura são recusados.
- Atalho: "Alterar" libera o atalho atual e captura a próxima combinação (Ctrl, Alt, Shift ou Win + tecla; Esc cancela). Se outro app já usa, volta o anterior e a tela avisa.
- Tema: aplicado ao `Window.Content` de cada janela; o Acrylic segue o tema desse elemento. Num elemento interno o fundo continua no tema do Windows.

## Integração com o Windows

- **Iniciar com o Windows**: valor `BoxeZone` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` com `"<exe>" --startup`, regravado a cada início (o caminho segue o `.exe`). `StartupTask` só existe para apps empacotados. Com `--startup` o app abre só na bandeja.
- **Notificações**: o `AppNotificationManager` do Windows App SDK **não funciona** self-contained sem pacote (`Register` falha com 0x8007007E e derrubava o app). Usa-se `Windows.UI.Notifications` com o AppUserModelID `BoxeZone.App`, registrado em `HKCU\Software\Classes\AppUserModelId\BoxeZone.App` (nome + `Assets\BoxeZone.png`). Uma notificação por lote ("Enviado para o …", com "Abrir") e uma por falha. Cliques só funcionam com o app aberto.
- **Bandeja** (`H.NotifyIcon.WinUI`): `ForceCreate(enablesEfficiencyMode: false)`; o padrão liga o Modo de eficiência, que deixaria os uploads lentos com a janela escondida.
- **Falhas**: exceção no `OnLaunched` derruba o app sem mensagem (só `0xC000027B` no log de eventos); `CrashLog` grava tipo, mensagem e pilha em `%LocalAppData%\BoxeZone\crash.log`.

## Recursos e estilos

- Strings: `x:Uid` no XAML, `ResourceLoader` no código. `Window.Title` não aceita `x:Uid` (Window não é DependencyObject).
- `DefaultLanguage` é `pt-BR` no `.csproj`, porque os recursos só existem em pt-BR (o padrão do template é `en-US`).
- Cores em `ThemeDictionaries` (Dark com os valores do protótipo, Light com os recursos equivalentes do WinUI). Sobrescritas de controles nativos com valor igual nos dois temas (como `ToggleSwitchFillOn`) vão no dicionário principal do `App.xaml`: dentro dos `ThemeDictionaries` a do `ToggleSwitch` não teve efeito.
- Nome de `VisualState` não pode repetir nome de evento do controle (`DragOver` gerava CS0108); usar `DragTarget`.
- `x:Bind` converte `bool` em `Visibility`, mas não o inverso: usar `BindConverters`. Função estática no próprio code-behind é chamada como de instância pelo `x:Bind` e não compila.
- **Foco e o `ScrollViewer` raiz**: um clique em algo que não recebe foco (texto, área vazia) leva o foco ao `ScrollViewer` raiz do WinUI, **acima** da página; daí em diante o `PreviewKeyDown` da página não vê as teclas. A captura do atalho (Configurações) cancela no `LosingFocus` da página qualquer mudança de foco para fora dela enquanto captura.
- Seletor de tema: `RadioButton` nativo com o estilo `SegmentRadioButtonStyle` (o WinUI não tem controle segmentado sem pacote extra).
- Logo do app: luva escura no quadrado `#4CC2FF`, em duas versões. `Assets/BoxeZone.svg` é a completa (listras no cano, brilho); `Assets/BoxeZone-small.svg` é a simplificada para tamanhos minúsculos (sem listras nem brilho, vinco do polegar mais grosso), porque abaixo de ~24 px os detalhes viram um borrão cinza. O `BoxeZone.ico` usa a simplificada em 16 e 20 px e a completa em 24, 32, 40, 48, 64 (BMP de 32 bits) e 256 px (PNG); o `System.Drawing.Icon` da bandeja lê esse formato. O `BoxeZone.png` (256 px, notificações) é a completa. Os PNGs saem do Edge headless (`--default-background-color=00000000`; ele não tira print de janela minúscula, como 16 × 16, então os tamanhos vão juntos numa página e são recortados). O cabeçalho do widget redesenha a versão simplificada no XAML (`WidgetPage.xaml`): mudou o SVG, mudar lá também.
- Logos dos provedores: SVGs oficiais em `Assets/Providers/`, carregados com `SvgImageSource` por `ms-appx:///` (funciona sem pacote, porque os arquivos são `Content` copiados ao lado do `.exe`). O `SvgImageSource` usa o SVG do Direct2D, que não desenha `<mask>`, folhas de estilo (`<style>`, `class`) nem texto: no SVG baixado, a máscara vira `<clipPath>` e os estilos viram atributos (`fill`, `fill-opacity`). O `viewBox` é recortado no contorno do logo para ele ocupar os 18 px. Diretrizes de marca: não recolorir nem distorcer os logos.

## Testes manuais automatizados (UI Automation)

O app não tem testes automatizados no repositório. Nas etapas, o comportamento foi conferido com scripts PowerShell que usam UI Automation, `SendInput`/`mouse_event`, capturas de tela e o estilo das janelas (`GetWindowLong`). Cuidados:
- PowerShell 5.1 lê `.ps1` sem BOM como ANSI: salvar em UTF-8 **com BOM** quando houver acentos ("Configurações").
- Chamar `SetProcessDpiAwarenessContext(-4)` no script; senão as coordenadas saem erradas em monitores com escala diferente de 100%.
- A janela da aba também é do processo: identificar o widget pela maior janela visível.
- Arquivos do repositório estão em CRLF: substituições de várias linhas com `perl`/`sed` falham; usar o editor.
