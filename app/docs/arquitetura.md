# Boxe Zone: arquitetura e armadilhas

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
    Task<UploadedItem> UploadAsync(UploadJob job, IProgress<double> progress,       // arquivo pelo caminho no disco;
                                   Action<string> saveResumeState, CancellationToken ct); // id, nome, link do dono
    Task<UploadedItem> CreateFolderAsync(string parentFolder, string name, CancellationToken ct); // "Nome (1)" se existir
    Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct);    // "qualquer um com o link"
}
```

- Falhas saem como `StorageProviderException` com `StorageErrorKind` (NotConfigured, AccessDenied, SessionExpired, MissingPermission, Offline, QuotaExceeded, Unknown). Cada provedor traduz as exceções do seu SDK; a UI só conhece o tipo e mostra a mensagem do `.resw`. `SessionExpired` também apaga a sessão local.
- Ligar o switch de um provedor não conectado inicia o login ("Conectando…", área de drop escondida, limite de 2 min; desligar cancela). Nas Configurações, durante o login, "Conectar" vira "Cancelar" (comando com `AllowConcurrentExecutions`, senão o próprio login que ele iniciou o deixa desabilitado). Falhou ou foi cancelado: o switch desliga e o status mostra o motivo.
- Fechar a aba do navegador **não avisa o app**: nenhuma requisição chega ao endereço de retorno (loopback). Por isso o limite de tempo e o cancelar. Cancelar libera o endereço: Google e OneDrive usam uma porta nova a cada login; o Dropbox reusa `localhost:53682` (o `HttpListener` é parado no cancelamento). Os endereços ficam no `http.sys`, não no processo: `netsh http show servicestate view=requestq` para conferir.
- Falha no envio: "Não foi possível enviar · nome", o motivo e "Tentar de novo", que continua a fila a partir do arquivo que falhou (reconectando antes, se preciso; um arquivo que sumiu do disco, "Arquivo não encontrado", fica de fora). Um provedor com falha não afeta os outros.
- **Pastas**: o drop passa os caminhos (itens sem caminho, virtuais, ficam de fora). `FolderScanner` percorre as pastas fora da thread da UI, pulando ocultos e de sistema (`desktop.ini`, `Thumbs.db`) e pontos de nova análise (links podem formar ciclos); pasta vazia não é criada. A pasta solta é criada antes dos arquivos dela com `CreateFolderAsync`, que renomeia como os arquivos ("Fotos (1)"): Google procura o nome e tenta "(n)"; OneDrive `POST …/children` com `conflictBehavior=rename` (o pai precisa existir: `EnsureFolderPathAsync` cria os segmentos com `fail` e ignora 409); Dropbox `CreateFolderV2Async(autorename)`. As subpastas saem do caminho de cada envio (os três criam o que falta). Uma pasta só: "Enviado · nome da pasta", "Abrir" abre a pasta e "Copiar link" dá o link da pasta; pastas e arquivos juntos: um link por pasta e por arquivo solto, na ordem do drop. MIME (Google) pelo registro do Windows (`HKCR\.ext\Content Type`).
- **Fila e retomada**: a leva é uma `UploadQueueState` (pastas soltas e arquivos, cada arquivo com status e `ResumeState`), guardada criptografada pelo `UploadQueueStore` no `TokenStore` (`upload-queue-{provedor}`: tem caminhos de arquivos e endereços de envio; o do OneDrive é pré-autenticado). Gravada ao montar ou crescer a leva, ao criar uma pasta, ao terminar cada arquivo e, durante um arquivo, no máximo a cada 2 s; apagada no fim, no "Cancelar" e no "Desconectar"; falha comum deixa a fila salva. Apagar passa pela mesma fila de espera das gravações (uma gravação atrasada recriaria uma leva já terminada, e o próximo início reenviaria tudo); arquivo ilegível (PC desligado no meio da gravação) só descarta a fila. Ao iniciar, ao religar o provedor e depois de conectar, a fila salva continua sozinha ("Retomando…" até o primeiro progresso). `ResumeState` por provedor: Google, a URI da sessão retomável (evento `UploadSessionData`; `ResumeAsync(uri)` num pedido novo pergunta ao Google quantos bytes chegaram; erro HTTP do Google na retomada = do zero); OneDrive, a `uploadUrl` (`GET` devolve `nextExpectedRanges`; 404/410/400 = do zero; a sessão só é apagada no "Cancelar"); Dropbox, `sessionId|offset` (`incorrect_offset` traz o `correct_offset`; `not_found`/`closed` = do zero; sessões duram 7 dias). Arquivo com tamanho ou data diferentes desde o drop recomeça do zero. Fechar entre o fim de um arquivo e a gravação da fila (até 2 s, pelo limite abaixo) pode reenviar esse arquivo como "nome (1)" (raro, aceito); o mesmo vale se a resposta da última parte se perder no OneDrive ou no Dropbox (o Google não tem esse risco).
  - **Limite de gravação**: a fila inteira é regravada no máximo a cada 2 s (uma pasta de 10 mil arquivos regravando a cada arquivo escreveria dezenas de GB); arquivos de dentro de pastas não guardam o item enviado (só o "Copiar link" de arquivos soltos precisa). **Exceção que custou um bug**: o primeiro ponto de retomada de cada arquivo é gravado na hora, porque o Google entrega o endereço da sessão uma vez só, no começo; com o limite, ele caía dentro dos 2 s da gravação anterior, não era salvo e a retomada recomeçava do zero.
  - **PC ligado sem rede** (início com o Windows, Wi-Fi ainda conectando): a reconexão silenciosa falha com exceção e o provedor ficaria desligado. `RestoreAsync` então tenta de novo com o `NetworkWaiter` (sem limite), liga o provedor e continua a fila salva. Sessão revogada volta sem exceção e não entra nesse laço.
  - **Falha numa retomada automática** (ao abrir): sem notificação, e a cópia salva é apagada (a fila fica na memória para o "Tentar de novo"). Sem isso, uma falha que não passa (sem espaço, nome recusado) voltaria a cada início, com notificação.
  - Apagar a fila no fim e no "Cancelar" não é aguardado: a mudança de estado precisa ser imediata, senão um drop nesse meio-tempo entrava numa leva que estava acabando e nunca era enviado. A ordem com gravações posteriores é garantida pelo semáforo do `UploadQueueStore`.
  - O `TokenStore` grava num arquivo temporário e troca (`File.Move` com `overwrite`): desligar o PC no meio da gravação não deixa um arquivo quebrado (vale também para as contas).
- **Sem internet**: `StorageErrorKind.Offline` no meio do envio não falha a leva: "Aguardando conexão…" no lugar da porcentagem e nova tentativa (com o `ResumeState` mais recente) quando o Windows avisa que a rede mudou ou após 15 s, 30 s e depois a cada 60 s, sem limite. O aviso some no primeiro progresso (não ao fim do arquivo: senão ficava até o arquivo terminar). Trocar o proxy não dispara `NetworkStatusChanged`; o temporizador cobre. Teste: apontar o proxy do usuário (`HKCU\…\Internet Settings`, `ProxyEnable`/`ProxyServer` + `InternetSetOption` 39/37) para `127.0.0.1:9` e restaurar no `finally`; o `HttpClient` do .NET percebe a troca com o app aberto.
- Leva de arquivos: um por vez, com "3 de 10" ao lado da porcentagem (2 ou mais arquivos). Barra e porcentagem são da leva inteira, pesadas pelo tamanho de cada arquivo (lido antes de começar; arquivo vazio pesa 1). Arquivos soltos durante um envio entram no fim da leva ("3 de 12"). No fim: "Enviado · 10 arquivos" e uma notificação só. No painel compacto (200 px), numa leva o contador ocupa o lugar do nome do arquivo.
- O `Progress<T>` **posta** os avisos: o último aviso de um arquivo pode chegar depois de o próximo começar. Cada aviso guarda o número do arquivo e o quanto já tinha sido enviado antes dele, e é ignorado se o arquivo atual já é outro (sem isso a porcentagem passava de 100%).
- "Cancelar" (ao lado da porcentagem) cancela o `CancellationToken` do lote: para o arquivo atual e descarta os seguintes; a área volta a "Solte arquivos aqui", sem notificação. Qualquer exceção depois do pedido conta como cancelamento (alguns SDKs não devolvem `OperationCanceledException`). Nada fica no destino: o upload em partes só vira arquivo no fim (o OneDrive ainda apaga a sessão; Drive e Dropbox deixam a sessão expirar).
- Todo envio é em partes, para ter progresso real: Drive resumable upload (8 MB), Graph upload session (10 MiB, múltiplo de 320 KiB; arquivo vazio vai num PUT simples) e Dropbox upload session (8 MB; o envio simples não informa progresso e para em 150 MB).
- Nome repetido não sobrescreve: Graph `conflictBehavior=rename`, Dropbox `WriteMode.Add` + `autorename`.
- **"Copiar link"** (estado "Enviado", ao lado do "Abrir"; no painel compacto sem o texto "Enviado · nome", que não cabe; uma falha tira os dois links e ocupa a linha por alguns segundos, em até duas linhas): só compartilha no clique. Cria um link por arquivo da leva e copia um por linha. Google: permissão `anyone`/`reader` (o `drive.file` permite nos arquivos que o app criou; sem escopo novo). OneDrive: `createLink` `view`/`anonymous`, e `organization` se a conta de trabalho recusar (403/400); o Graph devolve o link existente. Dropbox: escopo `sharing.write` (precisa estar ligado no App Console, senão todo login falha com `invalid_scope`); "link já existe" reaproveita o da lista. Sessão do Dropbox anterior ao escopo dá `AuthException` de escopo faltando: vira `MissingPermission` ("Reconecte o Dropbox nas Configurações…") **sem** desconectar, porque o resto continua funcionando. Desligar e ligar o switch não basta (a sessão continua válida, então não há login novo): é "Desconectar" e "Conectar" nas Configurações. Na notificação, o título diz que falhou e o corpo diz o motivo (sem internet, sessão expirada, reconectar…).
- Área de transferência pela Win32 (`OpenClipboard`/`SetClipboardData`), com a janela da aba como dona: a `Clipboard` do WinRT exige app em primeiro plano, e sem dona o `SetClipboardData` falha. Outro app segurando a área de transferência: 10 tentativas de 50 ms e depois a mensagem de falha.
- O `ShareLinkService` guarda na memória as últimas 20 levas. O botão "Copiar link" da notificação abre `boxezone:copy-link?batch=<id>`; o `Program.Main` redireciona para a instância aberta, e a URI chega no `ILaunchActivatedEventArgs.Arguments` (o comando registrado é `"BoxeZone.exe" "%1"`). Copia sem mostrar o widget e confirma com uma notificação silenciosa que some em 1 minuto; leva desconhecida (app reiniciado) abre o widget.
- Testar links sem login: o Google responde 401 a um arquivo não compartilhado e 200 ao compartilhado (`Invoke-WebRequest` basta). O OneDrive recusa scripts (403) e o Edge comum entra sozinho com a conta da Microsoft do Windows: usar Edge headless `--inprivate` com perfil novo e `--disable-features=msImplicitSignin`, e comparar com a raiz do OneDrive, que precisa mostrar "Entrar".
- `Progress<T>` criado na UI devolve o progresso na thread da UI.
- Drop: `GetStorageItemsAsync` com `GetDeferral()`; pastas soltas são ignoradas. O drop não funciona se o app rodar como administrador.

### Google Drive

- Credencial "Desktop app" (client ID + secret), escopo `drive.file`: o app só enxerga o que ele mesmo criou. Por isso a pasta de destino é um caminho digitado (o app cria a pasta), não um navegador do Drive.
- O app OAuth está **"Em produção"** sem verificação: `drive.file` é escopo não sensível, então não há aviso de "app não verificado", limite de 100 usuários nem queda do login em 7 dias (isso é só do modo Teste). Pedir **qualquer** escopo sensível a mais traria tudo de volta e exigiria verificação. Sem logo no console: logo exige verificação de marca, que exige domínio próprio.
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
- Permissões `files.content.write`, `account_info.read` e `sharing.write` (só para "Copiar link"; ver acima). "Abrir" leva à pasta no dropbox.com com o arquivo em pré-visualização. O segundo "Copiar link" do mesmo arquivo recebe "link já existe" com o link junto; listar links exigiria `sharing.read`, que o app não pede.
- Guarda só o refresh token no `TokenStore`; o `DropboxClient` renova sozinho.

### Tokens e credenciais

- `TokenStore`: `DataProtectionProvider("LOCAL=user")` (DPAPI pelo WinRT), um arquivo por chave em `%LocalAppData%\BoxeZone\tokens`. Não usar `ProtectedData` (exige outro NuGet) nem `PasswordVault` (limite de tamanho; o cache do MSAL não cabe).
- Tokens nunca em texto puro, nunca em log.
- `appsettings.local.json` (git-ignored) é copiado para a pasta do `.exe` e lido por `AppCredentials` (`JsonDocument`). Provedor sem valores = `NotConfigured`. Vai dentro do instalador: são credenciais de cliente público (Google desktop, MSAL, Dropbox PKCE), que os provedores não tratam como secretas. No CI vem do Secret `APPSETTINGS_LOCAL_JSON`; mudou o arquivo local, atualizar o Secret.
- Site e política (`https://weblucasdev.github.io/BoxeZone/` e `/privacidade/`) estão cadastrados como página inicial e política nos consoles do Google (Branding), da Microsoft (Identidade visual) e do Dropbox (Branding). Mudar o endereço do site exige atualizar os três.
- `SignInPage`: página mostrada no navegador ao fim do login dos três provedores (logo, uma frase, claro/escuro pelo navegador).

### Histórico e escolher a pasta

- **Histórico** (`HistoryService`, `HistoryViewModel`, `HistoryPage`): cada leva concluída entra no topo de `%LocalAppData%\BoxeZone\history.json` (máximo 50; título = pasta, arquivo ou "N arquivos", serviço, data, link do "Abrir" e os `UploadedItem` que o "Copiar link" compartilha). Gravado à parte e trocado, como o `TokenStore`. O "Copiar link" de lá usa o `ShareLinkService` com a conta conectada agora (outra conta não acha os itens). Página de 440 px como as Configurações; a lista rola a partir de 440 px de altura.
- **Procurar…** (cartão de cada conta nas Configurações): `ListFoldersAsync(pasta)` lista um nível de cada vez, sem criar nada (pasta que não existe = lista vazia). Google: só as pastas que o app criou (`drive.file`), com aviso na tela; OneDrive: `…/children?$select=name,folder` seguindo o `@odata.nextLink`; Dropbox: `ListFolderAsync` (raiz = "") + `ListFolderContinueAsync`. A raiz não pode ser destino. Os botões das pastas têm `AutomationProperties.Name` (sem ele, leitores de tela e scripts de UI Automation não os acham: o conteúdo é ícone + texto).

## Preferências (`SettingsService`)

- `%LocalAppData%\BoxeZone\settings.json` (System.Text.Json, camelCase, enums como texto). Grava 500 ms depois da última mudança, via arquivo temporário; "Sair" grava na hora. Arquivo ilegível volta aos padrões. `ApplicationData.Current` não funciona sem pacote.
- Ao abrir, um provedor só volta ligado se a sessão salva reconectar; senão o switch fica desligado (sem abrir o navegador sozinho) e a preferência salva não é apagada.
- Posição e estado: voltam só se ainda estiverem em algum monitor; recolhida abre recolhida; Hidden não é restaurado (só o `--startup` abre escondido).
- Pasta de destino: editada no próprio cartão ("Alterar" vira caixa de texto; Enter salva, Esc cancela). `\` vira `/`, espaços nas pontas saem; `<>:"|?*` e a raiz pura são recusados.
- Atalho: "Alterar" libera o atalho atual e captura a próxima combinação (Ctrl, Alt, Shift ou Win + tecla; Esc cancela). Se outro app já usa, volta o anterior e a tela avisa.
- Tema: aplicado ao `Window.Content` de cada janela; o Acrylic segue o tema desse elemento. Num elemento interno o fundo continua no tema do Windows.

## Integração com o Windows

- **Iniciar com o Windows**: valor `BoxeZone` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` com `"<exe>" --startup`, regravado a cada início (o caminho segue o `.exe`). `StartupTask` só existe para apps empacotados. Com `--startup` o app abre só na bandeja.
- **Notificações**: o `AppNotificationManager` do Windows App SDK **não funciona** self-contained sem pacote (`Register` falha com 0x8007007E e derrubava o app). Usa-se `Windows.UI.Notifications` com o AppUserModelID `BoxeZone.App`, registrado em `HKCU\Software\Classes\AppUserModelId\BoxeZone.App` (nome + `Assets\BoxeZone.png`). Uma notificação por lote ("Enviado para o …", com "Abrir") e uma por falha. Cliques usam ativação por protocolo e funcionam com o app fechado: o link do arquivo (https) abre no navegador; notificações sem link usam `boxezone:open`, protocolo registrado pelo app no HKCU a cada início (`ProtocolService`) que abre o widget pela instância única. Escolhido no lugar de um ativador COM (`ToastActivatorCLSID` no atalho + servidor COM no app): quem abre o link é o próprio Windows, então não precisa de servidor COM, CLSID nem do atalho do instalador, e funciona igual no build de desenvolvimento.
- **Bandeja** (`H.NotifyIcon.WinUI`): `ForceCreate(enablesEfficiencyMode: false)`; o padrão liga o Modo de eficiência, que deixaria os uploads lentos com a janela escondida.
- **Menu da bandeja**: no `ContextMenuMode.PopupMenu` o H.NotifyIcon transforma o `MenuFlyout` num menu nativo e executa o `Command` de cada `MenuFlyoutItem`; o evento `Click` **nunca** dispara (por isso "Abrir" e "Sair" não faziam nada até a 1.0.0). Usar `Command = new RelayCommand(...)`. Para testar por script: o menu abre mandando `WM_RBUTTONDOWN`/`WM_RBUTTONUP` (mensagem `0x400`) para a janela oculta `H.NotifyIcon_<guid>` do processo; os itens nativos não aparecem no UI Automation, então o clique é pelo mouse, a partir do retângulo da janela `#32768`.
- **Instância única entre cópias**: a chave do `AppInstance` vale por app, e para o Windows um `.exe` em outra pasta é outro app; a cópia instalada e a de `bin\Debug` rodam juntas. Fechar uma antes de testar a outra.
- **Falhas**: exceção no `OnLaunched` derruba o app sem mensagem (só `0xC000027B` no log de eventos); `CrashLog` grava tipo, mensagem e pilha em `%LocalAppData%\BoxeZone\crash.log`.
- **Versão nova** (`UpdateService`): pode ser desligada ("Verificar versões novas", `checkForUpdates` no `settings.json`); desligada, o app não fala com nada além dos provedores do usuário. Regra do projeto (veio da candidatura à SignPath Foundation, recusada, e foi mantida): o que contata um sistema que o usuário não escolheu precisa estar na política, aparecer na instalação e poder ser desligado. O instalador tem a mesma opção: desmarcada, grava `HKCU\Software\BoxeZone\DisableUpdateCheck = 1`, que o app aplica e apaga no início (`ApplyInstallerChoice`, antes de montar as Configurações). No máximo uma vez a cada 24 h, `GET api.github.com/repos/WebLucasDev/BoxeZone/releases/latest` (sem autenticação, `User-Agent` obrigatório). O resultado fica no `settings.json` (`latestVersion`, `latestUrl`) e alimenta a linha "Versão" das Configurações; `notifiedVersion` garante uma notificação por versão. 404 (nenhuma Release) conta como consulta feita; falha de rede não grava nada. O app chama `CheckAsync` ao iniciar e de hora em hora (`UpdateService.PollInterval`): ele passa dias na bandeja, e ao ligar o PC a rede costuma não estar pronta; o limite de uma consulta real por dia continua. `releases/latest` ignora pré-lançamentos.

## Instalador e versões

- **Pacote**: o metapacote `Microsoft.WindowsAppSDK` fica (o `H.NotifyIcon.WinUI` depende dele; sem referência direta entra a 1.6, que quebra o build), mas os componentes AI, ML, Search e Widgets, e o `Microsoft.Windows.AI.MachineLearning` que o ML puxa, são referenciados com `ExcludeAssets="all"` e não entram no pacote (~57 MB a menos: `onnxruntime`, `DirectML`…). As versões precisam ser as que o metapacote pede (ver o `.nuspec` dele no cache do NuGet). Se uma API do SDK falhar em tempo de execução, conferir se ela vem de um desses componentes.
- **Inno Setup** (`app/installer/BoxeZone.iss`): instalação por usuário em `%LocalAppData%\Programs\Boxe Zone`, sem administrador; `AppId` fixo (nunca mudar: é como a versão nova acha a instalada); `[InstallDelete]` limpa a pasta do app antes de copiar; `[UninstallDelete] dirifempty` remove a pasta vazia (sem isso ela ficava). Atalho do Menu Iniciar com o AUMID `BoxeZone.App`.
- **App aberto**: o app vive na bandeja e cancela o fechamento da janela, então o Restart Manager não o fecha; o instalador e o desinstalador usam `taskkill /F` (preferências são salvas a cada mudança).
- **Desinstalação**: apaga `Run\BoxeZone`, `Classes\AppUserModelId\BoxeZone.App` e `Classes\boxezone` (gravados pelo app) e `Software\BoxeZone` (gravado pelo instalador), e pergunta antes de apagar `%LocalAppData%\BoxeZone`; no modo silencioso não apaga.
- **Privacidade na instalação**: `InfoBeforeFile=privacidade.txt` (UTF-8 **com BOM**) mostra o resumo da política antes de instalar; a tarefa "Verificar versões novas" vem marcada.
- **`UsePreviousTasks=no`**: por padrão o Inno lembra as tarefas da instalação anterior, e uma atualização com a caixa de versão desmarcada antes regravava a marca e desfazia o que o usuário tinha religado nas Configurações. (`dontinheritcheck` não serve: é para tarefas filhas.) Testar tarefas por linha de comando: `/MERGETASKS="!updatecheck"`.
- **Build local**: `.\app\installer\build.ps1 -Arch x64|arm64 -Version X.Y.Z[-sufixo]` publica e roda o ISCC (Inno Setup 6.5.2+, a primeira que aceita PNG nas imagens do assistente; `winget install JRSoftware.InnoSetup`); saída com nome fixo em `app\installer\output\` (git-ignored). Exige o `app\appsettings.local.json`. Apaga a pasta `publish` antes de publicar (arquivos velhos iriam para o instalador). `$env:ISCC` aponta outro ISCC.
- **Release**: `.github/workflows/release.yml` roda em tags `v*.*.*`: grava as credenciais a partir do Secret `APPSETTINGS_LOCAL_JSON`, gera os dois instaladores e cria a Release. Tag com sufixo (`v1.2.0-rc.1`) vira pré-lançamento, ignorado pelo botão do site e pelo `UpdateService`. A tag entra nos scripts só por variável de ambiente. O Inno Setup do CI é fixo na mesma versão do local (6.7.3, baixado do GitHub do Inno com SHA256 conferido; o Chocolatey só vai até a 6.7.1); para atualizar, trocar URL e hash (`winget show JRSoftware.InnoSetup --version X.Y.Z`).
- **Imagens do assistente**: `app\installer\images\` (luva no painel escuro, telas de boas-vindas e conclusão; logo com margem no canto das outras telas), 7 tamanhos de cada, um por escala de tela; o Setup escolhe o mais próximo. Geradas por `app\installer\make-images.ps1` (Edge headless a partir dos SVGs do logo): mudou o logo, rodar de novo. O Inno esconde a tela de boas-vindas por padrão, então a imagem grande aparece na conclusão; para conferir sem instalar, compilar uma cópia do `.iss` com `DisableWelcomePage=no` e cancelar na primeira tela.
- **Testar o desinstalador por automação**: os botões do Inno aparecem como "Pane" no UI Automation, sem Invoke; usar `BM_CLICK` no handle do botão. A janela das perguntas é do processo `_unins.tmp` (cópia temporária), não do `unins000.exe`.

## Recursos e estilos

- Strings: `x:Uid` no XAML, `ResourceLoader` no código. `Window.Title` não aceita `x:Uid` (Window não é DependencyObject).
- `DefaultLanguage` é `pt-BR` no `.csproj`, porque os recursos só existem em pt-BR (o padrão do template é `en-US`).
- Cores em `ThemeDictionaries` (Dark com os valores do protótipo, Light com os recursos equivalentes do WinUI). Sobrescritas de controles nativos com valor igual nos dois temas (como `ToggleSwitchFillOn`) vão no dicionário principal do `App.xaml`: dentro dos `ThemeDictionaries` a do `ToggleSwitch` não teve efeito.
- Nome de `VisualState` não pode repetir nome de evento do controle (`DragOver` gerava CS0108); usar `DragTarget`.
- **`x:Bind` numa `Window` só liga no `Activated`** (o código gerado chama `Bindings.Initialize` ali; numa `Page` é no `Loading`). Aberto recolhido, a faixa aparece sem ativar a janela, e o painel compacto ficava sem provedores e com "Ative um destino" até o widget ser aberto uma vez. O `MainWindow` chama `Bindings.Update()` no construtor.
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
- **Vários arquivos**: selecionar os itens pelo `SelectionItemPattern` (`Select` no primeiro, `AddToSelection` nos outros) e arrastar um deles. **Painel compacto**: arrastar até a faixa, esperar o painel abrir e seguir até a área do provedor (o texto dela é o nome do provedor, ou o último resultado). A faixa não tem `InvokePattern`; para abrir o widget pelo script, usar o atalho (Ctrl+Alt+D). O painel só recolhe com o cursor fora dele (`SetCursorPos` basta, ele lê `GetCursorPos`).
- **Drop de arquivo**: um `DoDragDrop` do WinForms com só `CF_HDROP` é recusado pela área de drop do WinUI. Funciona arrastar de uma janela real do Explorer: abrir a pasta, achar o item (`ListItem`, nome sem extensão se o Explorer as esconde), `mouse_event` para apertar, mover em passos até a área, mexer alguns pixels sobre ela e soltar.
