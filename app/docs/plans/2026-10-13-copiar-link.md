# Copiar link compartilhável: plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** "Copiar link" no estado "Enviado" e na notificação: torna os arquivos da leva legíveis por qualquer pessoa com o link e copia os links, um por linha.

**Architecture:** `IStorageProvider` passa a devolver `UploadedItem` no envio e ganha `CreateShareLinkAsync`. Um `ShareLinkService` cria os links, copia pela API Win32 da área de transferência e guarda as últimas 20 levas para o botão da notificação, que chega ao app pelo protocolo `boxezone:copy-link?batch=…` e pelo redirecionamento de instância única que já existe.

**Tech Stack:** WinUI 3 / .NET 10, CommunityToolkit.Mvvm, Google.Apis.Drive.v3, Microsoft Graph por `HttpClient` (MSAL), Dropbox.Api, `Windows.UI.Notifications`, P/Invoke.

**Spec:** `app/docs/specs/2026-10-13-copiar-link-design.md`

## Global Constraints

- Nenhum pacote novo (lista fechada no `CLAUDE.md`).
- Strings de UI só no `Strings/pt-BR/Resources.resw`; código, nomes e comentários em inglês.
- Google continua só com `drive.file`; OneDrive com `Files.ReadWrite`; Dropbox ganha `sharing.write` (e mais nada).
- **Antes de publicar qualquer build com o escopo novo**, `sharing.write` precisa estar ligado no App Console do Dropbox: sem isso, todo login novo no Dropbox falha (`invalid_scope`).
- Nada é compartilhado sem o clique em "Copiar link".
- Uma sessão do Dropbox sem `sharing.write` **não** pode ser desconectada pelo erro de escopo: vira `StorageErrorKind.MissingPermission`.
- `dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64` com 0 avisos ao fim de cada tarefa.
- O repositório não tem projeto de testes (exige aprovar um pacote; ver roadmap). A verificação de cada tarefa é o build mais scripts PowerShell de UI Automation contra o app rodando, como nas versões anteriores (`app/docs/arquitetura.md`, "Testes manuais automatizados"). Os scripts ficam no scratchpad da sessão, não no repositório.
- Fechar o Boxe Zone de `bin\x64\Debug` antes de compilar; a cópia instalada também, enquanto se testa a de desenvolvimento (mesma posição de janela e mesmo `settings.json`). Fazer backup do `%LocalAppData%\BoxeZone\settings.json` antes dos testes e restaurar no fim.

## Review Focus

1. **Sessão antiga do Dropbox (sem `sharing.write`)**: o usuário espera ver "Conecte o Dropbox de novo para copiar links" e continuar conectado (envios seguem funcionando). Coberto por leitura obrigatória do `ToStorageException` do Dropbox na Tarefa 1 e pelo teste do usuário na `-rc`.
2. **Área de transferência ocupada por outro app**: o usuário espera uma mensagem de falha, não um travamento nem um "Link copiado" falso. Teste na Tarefa 2 (segurar a área de transferência aberta por 3 s durante o clique).
3. **Clique em "Copiar link" e logo depois uma leva nova**: a linha nova não pode ficar presa em "Copiando…" nem mostrar "Link copiado" da leva anterior. Teste na Tarefa 2.
4. **Notificação de uma leva que o app não conhece mais** (app reiniciado, ou id inválido): o clique abre o widget, sem erro. Teste na Tarefa 3.
5. **Conta de trabalho do OneDrive que proíbe link anônimo**: cai para link da organização em vez de falhar. Coberto por leitura do código na Tarefa 1 e pelo teste do usuário na `-rc` (se ele tiver conta de trabalho; senão fica registrado como não testado).

---

### Task 1: Camada dos provedores (`UploadedItem`, `CreateShareLinkAsync`, `MissingPermission`)

**Files:**
- Modify: `app/Services/Storage/IStorageProvider.cs`
- Modify: `app/Services/Storage/GoogleDriveProvider.cs`
- Modify: `app/Services/Storage/OneDriveProvider.cs`
- Modify: `app/Services/Storage/DropboxProvider.cs`
- Modify: `app/ViewModels/ProviderViewModel.cs` (só o necessário para compilar com o tipo novo)

**Interfaces:**
- Produces:
  - `public sealed record UploadedItem(string Id, string Name, string WebUrl);` (namespace `BoxeZone.Services.Storage`)
  - `Task<UploadedItem> IStorageProvider.UploadAsync(StorageFile file, string targetFolder, IProgress<double> progress, CancellationToken ct)`
  - `Task<string> IStorageProvider.CreateShareLinkAsync(UploadedItem item, CancellationToken ct)`
  - `StorageErrorKind.MissingPermission`

- [ ] **Step 1: Interface**

Em `IStorageProvider.cs`, trocar o `UploadAsync` e acrescentar o método e o tipo:

```csharp
        /// <summary>Uploads the file into <paramref name="targetFolder"/> (created if missing).</summary>
        Task<UploadedItem> UploadAsync(StorageFile file, string targetFolder, IProgress<double> progress, CancellationToken ct);

        /// <summary>Makes the file readable by anyone with the link and returns that link.</summary>
        Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct);
    }

    /// <summary>A file the app uploaded: what is needed to open it (owner link) and to share it later.</summary>
    public sealed record UploadedItem(string Id, string Name, string WebUrl);
```

E no enum, depois de `SessionExpired`:

```csharp
        /// <summary>The saved session lacks a permission added in a later version; signing in again grants it.</summary>
        MissingPermission,
```

- [ ] **Step 2: Google Drive**

No `UploadAsync`: `request.Fields = "id, name, webViewLink";` e o retorno:

```csharp
                progress.Report(100);
                DriveFile created = request.ResponseBody;
                return new UploadedItem(created.Id, created.Name ?? file.Name, created.WebViewLink);
```

Assinatura: `public async Task<UploadedItem> UploadAsync(...)`. Novo método, depois do `UploadAsync`:

```csharp
        public async Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct)
        {
            DriveService drive = _drive
                ?? throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to Google Drive.");

            try
            {
                // drive.file lets the app share the files it created. Creating the same
                // "anyone" permission again does not add a second one.
                PermissionsResource.CreateRequest request = drive.Permissions.Create(
                    new Permission { Type = "anyone", Role = "reader" }, item.Id);
                request.Fields = "id";
                await request.ExecuteAsync(ct);
                return item.WebUrl;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw await ToStorageExceptionAsync(ex);
            }
        }
```

Acrescentar `using Permission = Google.Apis.Drive.v3.Data.Permission;` junto do alias `DriveFile`.

- [ ] **Step 3: OneDrive**

Trocar `ReadWebUrl` por:

```csharp
        private static UploadedItem ReadItem(string driveItemJson)
        {
            using JsonDocument item = JsonDocument.Parse(driveItemJson);
            JsonElement root = item.RootElement;
            return new UploadedItem(
                root.GetProperty("id").GetString() ?? string.Empty,
                root.GetProperty("name").GetString() ?? string.Empty,
                root.GetProperty("webUrl").GetString() ?? string.Empty);
        }
```

Nos dois `return ReadWebUrl(...)` do `UploadAsync`, usar `ReadItem(...)`; assinatura `public async Task<UploadedItem> UploadAsync(...)`. Novos métodos:

```csharp
        public async Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct)
        {
            try
            {
                try
                {
                    return await CreateLinkAsync(item.Id, "anonymous", ct);
                }
                catch (GraphHttpException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
                {
                    // Work and school accounts can forbid anonymous links: people in the organization instead.
                    return await CreateLinkAsync(item.Id, "organization", ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        /// <summary>Graph returns the existing link when the item already has one of this type and scope.</summary>
        private async Task<string> CreateLinkAsync(string itemId, string scope, CancellationToken ct)
        {
            using HttpRequestMessage request = await CreateGraphRequestAsync(HttpMethod.Post, $"/me/drive/items/{itemId}/createLink", ct);
            request.Content = new StringContent($"{{\"type\":\"view\",\"scope\":\"{scope}\"}}", Encoding.UTF8, "application/json");
            using JsonDocument response = JsonDocument.Parse(await SendAsync(request, ct));
            return response.RootElement.GetProperty("link").GetProperty("webUrl").GetString()
                ?? throw new InvalidDataException("createLink returned no webUrl.");
        }
```

(O id do item vai sem escapar: ids do OneDrive pessoal têm `!`, aceito no caminho.)

- [ ] **Step 4: Dropbox**

Escopos: `private static readonly string[] Scopes = ["files.content.write", "account_info.read", "sharing.write"];`

No `UploadAsync`: assinatura `Task<UploadedItem>` e `return new UploadedItem(metadata.Id, metadata.Name, BuildWebLink(folder, metadata.Name));`. Comentário do `BuildWebLink` passa a ser: `/// <summary>Owner link ("Abrir"): the folder on the web with the file in preview.</summary>`.

Novo método (`using Dropbox.Api.Sharing;`):

```csharp
        public async Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct)
        {
            DropboxClient client = _client
                ?? throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to Dropbox.");

            try
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return (await client.Sharing.CreateSharedLinkWithSettingsAsync(item.Id)).Url;
                }
                catch (ApiException<CreateSharedLinkWithSettingsError> ex) when (ex.ErrorResponse.IsSharedLinkAlreadyExists)
                {
                    ListSharedLinksResult existing = await client.Sharing.ListSharedLinksAsync(item.Id, directOnly: true);
                    return existing.Links.FirstOrDefault()?.Url
                        ?? throw new InvalidDataException("Dropbox reported an existing link but listed none.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }
```

No `ToStorageException`, **antes** do braço `AuthException =>`:

```csharp
                // A session from before sharing.write: keep it (uploads still work), ask to sign in again.
                AuthException { ErrorResponse.IsMissingScope: true } =>
                    new StorageProviderException(StorageErrorKind.MissingPermission, ex.Message, ex),
```

- [ ] **Step 5: ProviderViewModel compila com o tipo novo**

Em `ProviderViewModel.UploadFilesAsync`, trocar `_uploadedLink = await Provider.UploadAsync(...)` por `_uploadedLink = (await Provider.UploadAsync(file, TargetFolder, progress, cts.Token)).WebUrl;` (a Tarefa 2 substitui isso pela lista de itens).

- [ ] **Step 6: Build**

Run: `dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

- [ ] **Step 7: Envio continua funcionando (Google)**

Rodar o app de `bin\x64\Debug`, soltar um arquivo pequeno na área do Google Drive com o `explorer-drop.ps1` do scratchpad (arrasta de uma janela real do Explorer; `-File <arquivo> -X <x> -Y <y>`, coordenadas do texto "Solte arquivos aqui" por UI Automation).
Expected: "Enviado · nome"; "Abrir" continua abrindo o arquivo (o link do dono não mudou).

- [ ] **Step 8: Commit**

```powershell
git add app/Services/Storage app/ViewModels/ProviderViewModel.cs
git commit -m "feat: share links in the storage providers"
```

---

### Task 2: Copiar na área de drop (`ShareLinkService`, área de transferência, estado "Enviado")

**Files:**
- Create: `app/Services/ShareLinkService.cs`
- Modify: `app/Helpers/Win32.cs`
- Modify: `app/ViewModels/ProviderViewModel.cs`
- Modify: `app/ViewModels/MainViewModel.cs`
- Modify: `app/App.xaml.cs`
- Modify: `app/Views/Controls/ProviderRow.xaml`, `app/Views/Controls/ProviderRow.xaml.cs`
- Modify: `app/Strings/pt-BR/Resources.resw`
- Modify: `app/docs/widget.dc.html`

**Interfaces:**
- Consumes: `UploadedItem`, `IStorageProvider.CreateShareLinkAsync`, `StorageErrorKind.MissingPermission` (Tarefa 1).
- Produces:
  - `public static bool Win32.TrySetClipboardText(IntPtr owner, string text)`
  - `public sealed record ShareResult(StorageErrorKind? Error) { public bool Succeeded => Error is null; }`
  - `public sealed record SharedBatch(IStorageProvider Provider, IReadOnlyList<UploadedItem> Items);`
  - `public sealed record BatchCopy(SharedBatch Batch, ShareResult Result);`
  - `ShareLinkService(IntPtr clipboardOwner)` com `string Register(IStorageProvider, IReadOnlyList<UploadedItem>)`, `Task<ShareResult> CopyAsync(IStorageProvider, IReadOnlyList<UploadedItem>, CancellationToken)`, `Task<BatchCopy?> CopyBatchAsync(string batchId)`, `static string DescribeFailure(StorageErrorKind kind, string providerName)`
  - `UploadFinishedEventArgs(bool Succeeded, IReadOnlyList<string> FileNames, string? Link, string? ErrorMessage, string? BatchId)`
  - `ProviderViewModel`: `CopyLinkCommand`, `CopyLinkLabel`, `DoneText`, `HasDoneMessage`; construtor ganha `ShareLinkService shareLinks` como último parâmetro.
  - `MainViewModel(SettingsService, SettingsViewModel, ShareLinkService, IStorageProvider, IStorageProvider, IStorageProvider)`

- [ ] **Step 1: Strings**

No `Resources.resw`, depois de `UploadFilesFormat`:

```xml
  <data name="CopyLink" xml:space="preserve">
    <value>Copiar link</value>
  </data>
  <data name="CopyLinkCopying" xml:space="preserve">
    <value>Copiando…</value>
  </data>
  <data name="CopyLinkCopied" xml:space="preserve">
    <value>Link copiado</value>
  </data>
  <data name="CopyLinkFailed" xml:space="preserve">
    <value>Não foi possível criar o link</value>
  </data>
  <data name="CopyLinkReconnectFormat" xml:space="preserve">
    <value>Conecte o {0} de novo para copiar links</value>
    <comment>{0} = provider name.</comment>
  </data>
```

- [ ] **Step 2: Área de transferência (Win32)**

Em `Win32.cs`, constantes no topo (`private const uint CF_UNICODETEXT = 13;`, `private const uint GMEM_MOVEABLE = 0x0002;`) e, no fim da classe:

```csharp
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint format, IntPtr data);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr memory);

        /// <summary>
        /// Puts text on the clipboard without needing focus (the WinRT Clipboard wants a
        /// foreground app). False when another app holds the clipboard open right now.
        /// The owner window must be real: with no owner, SetClipboardData fails.
        /// </summary>
        public static bool TrySetClipboardText(IntPtr owner, string text)
        {
            if (!OpenClipboard(owner))
            {
                return false;
            }

            try
            {
                EmptyClipboard();
                IntPtr memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * 2));
                if (memory == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr target = GlobalLock(memory);
                if (target == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return false;
                }

                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
                GlobalUnlock(memory);

                if (SetClipboardData(CF_UNICODETEXT, memory) == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return false;
                }

                // The clipboard owns the memory from here on.
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
```

- [ ] **Step 3: `ShareLinkService`**

Criar `app/Services/ShareLinkService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BoxeZone.Helpers;
using BoxeZone.Services.Storage;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BoxeZone.Services
{
    /// <summary>Error is null when the links were created and copied.</summary>
    public sealed record ShareResult(StorageErrorKind? Error)
    {
        public bool Succeeded => Error is null;
    }

    public sealed record SharedBatch(IStorageProvider Provider, IReadOnlyList<UploadedItem> Items);

    public sealed record BatchCopy(SharedBatch Batch, ShareResult Result);

    /// <summary>
    /// "Copiar link": shares the files of a batch (anyone with the link can view) and copies
    /// the links, one per line. Remembers the latest batches so the notification button can
    /// copy them later, while the app keeps running.
    /// </summary>
    public sealed class ShareLinkService(IntPtr clipboardOwner)
    {
        private const int MaxBatches = 20;
        private const int ClipboardAttempts = 10;

        private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(50);
        private static readonly ResourceLoader Strings = new();

        // Oldest first.
        private readonly List<(string Id, SharedBatch Batch)> _batches = [];

        public string Register(IStorageProvider provider, IReadOnlyList<UploadedItem> items)
        {
            string id = Guid.NewGuid().ToString("N");
            _batches.Add((id, new SharedBatch(provider, items)));
            if (_batches.Count > MaxBatches)
            {
                _batches.RemoveAt(0);
            }

            return id;
        }

        public async Task<ShareResult> CopyAsync(IStorageProvider provider, IReadOnlyList<UploadedItem> items, CancellationToken ct)
        {
            try
            {
                var links = new List<string>(items.Count);
                foreach (UploadedItem item in items)
                {
                    links.Add(await provider.CreateShareLinkAsync(item, ct));
                }

                string text = string.Join("\r\n", links);
                for (int attempt = 1; !Win32.TrySetClipboardText(clipboardOwner, text); attempt++)
                {
                    if (attempt == ClipboardAttempts)
                    {
                        // Another app kept the clipboard open the whole time.
                        return new ShareResult(StorageErrorKind.Unknown);
                    }

                    await Task.Delay(ClipboardRetryDelay, ct);
                }

                return new ShareResult(null);
            }
            catch (StorageProviderException ex)
            {
                return new ShareResult(ex.Kind);
            }
        }

        /// <summary>The notification's "Copiar link". Null when the batch is no longer remembered.</summary>
        public async Task<BatchCopy?> CopyBatchAsync(string batchId)
        {
            SharedBatch? batch = _batches.LastOrDefault(b => b.Id == batchId).Batch;
            if (batch is null)
            {
                return null;
            }

            return new BatchCopy(batch, await CopyAsync(batch.Provider, batch.Items, CancellationToken.None));
        }

        public static string DescribeFailure(StorageErrorKind kind, string providerName) =>
            kind == StorageErrorKind.MissingPermission
                ? string.Format(Strings.GetString("CopyLinkReconnectFormat"), providerName)
                : Strings.GetString("CopyLinkFailed");
    }
}
```

- [ ] **Step 4: `ProviderViewModel`**

1. `UploadFinishedEventArgs` ganha `string? BatchId` no fim (atualizar o comentário: "BatchId: for the notification's "Copiar link"; null on failure").
2. Campos: remover `_uploadedLink`; acrescentar

```csharp
        private readonly ShareLinkService _shareLinks;
        private readonly DispatcherQueueTimer _linkFeedbackTimer;

        // Files of the last batch that finished, for "Abrir" (last one) and "Copiar link" (all).
        private List<UploadedItem> _uploadedItems = [];
```

3. Construtor: novo parâmetro `ShareLinkService shareLinks` no fim; no corpo:

```csharp
            _shareLinks = shareLinks;
            CopyLinkLabel = Strings.GetString("CopyLink");

            // "Link copiado" and the failure messages go away after a moment.
            _linkFeedbackTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _linkFeedbackTimer.IsRepeating = false;
            _linkFeedbackTimer.Tick += (_, _) => ResetLinkFeedback();
```

(`using Microsoft.UI.Dispatching;`)

4. Propriedades:

```csharp
        /// <summary>"Copiar link", "Copiando…" or "Link copiado".</summary>
        [ObservableProperty]
        public partial string CopyLinkLabel { get; set; }

        /// <summary>A failure of "Copiar link", shown for a moment instead of "Enviado · nome".</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DoneText), nameof(HasDoneMessage))]
        public partial string? DoneMessage { get; set; }

        public string DoneText => DoneMessage ?? DoneLabel;

        public bool HasDoneMessage => DoneMessage is not null;
```

Acrescentar `nameof(DoneText)` ao `[NotifyPropertyChangedFor]` de `FileName` e de `FileCount`.

5. `UploadFilesAsync`: ao começar uma leva nova (junto de `_batch.Clear()`), `var uploaded = new List<UploadedItem>();`; no laço, `uploaded.Add(await Provider.UploadAsync(file, TargetFolder, progress, cts.Token));` no lugar da linha do `_uploadedLink`; no sucesso:

```csharp
            _uploadCts = null;
            _failedFiles = [];
            _uploadedItems = uploaded;
            string batchId = _shareLinks.Register(Provider, uploaded);
            State = UploadState.Done;
            UploadFinished?.Invoke(this, new UploadFinishedEventArgs(true, _batch.Select(f => f.Name).ToList(), uploaded[^1].WebUrl, null, batchId));
```

Na falha: `new UploadFinishedEventArgs(false, [file.Name], null, ErrorMessage, null)`.

6. `OpenUploaded` usa `_uploadedItems.LastOrDefault()?.WebUrl`.

7. Comando e auxiliares:

```csharp
        /// <summary>
        /// Shares every file of the last batch and copies the links. A batch that starts
        /// meanwhile keeps its own row: the result of this copy is then not shown.
        /// </summary>
        [RelayCommand]
        private async Task CopyLinkAsync()
        {
            List<UploadedItem> items = _uploadedItems;
            if (items.Count == 0)
            {
                return;
            }

            ResetLinkFeedback();
            CopyLinkLabel = Strings.GetString("CopyLinkCopying");
            ShareResult result = await _shareLinks.CopyAsync(Provider, items, CancellationToken.None);

            if (!IsDone || items != _uploadedItems)
            {
                ResetLinkFeedback();
                return;
            }

            if (result.Succeeded)
            {
                CopyLinkLabel = Strings.GetString("CopyLinkCopied");
                ShowLinkFeedbackFor(TimeSpan.FromSeconds(2));
                return;
            }

            CopyLinkLabel = Strings.GetString("CopyLink");
            DoneMessage = ShareLinkService.DescribeFailure(result.Error!.Value, Name);
            ShowLinkFeedbackFor(TimeSpan.FromSeconds(result.Error == StorageErrorKind.MissingPermission ? 5 : 3));
            RefreshConnection();
        }

        partial void OnStateChanged(UploadState value) => ResetLinkFeedback();

        private void ShowLinkFeedbackFor(TimeSpan duration)
        {
            _linkFeedbackTimer.Interval = duration;
            _linkFeedbackTimer.Start();
        }

        private void ResetLinkFeedback()
        {
            _linkFeedbackTimer.Stop();
            CopyLinkLabel = Strings.GetString("CopyLink");
            DoneMessage = null;
        }
```

- [ ] **Step 5: Ligar o serviço**

`MainViewModel`: construtor ganha `ShareLinkService shareLinks` depois de `SettingsViewModel settings`, e cada `new ProviderViewModel(...)` recebe `shareLinks` como último argumento.

`App.xaml.cs`: campo `private ShareLinkService? _shareLinks;`; logo depois de `_hotkeyService = ...`:

```csharp
            // The tab window exists first and never closes early: it owns what the app puts on the clipboard.
            _shareLinks = new ShareLinkService(_tabWindow.GetHwnd());
```

e `new MainViewModel(_settingsService, settings, _shareLinks, ...)`. Em `NotifyUploadFinished`, por enquanto nada muda (a Tarefa 3 usa o `BatchId`).

- [ ] **Step 6: Linha "Enviado"**

Em `ProviderRow.xaml`, no `Grid` do estado Done: quatro colunas (`Auto`, `*`, `Auto`, `Auto`); o `TextBlock` passa a ser

```xml
                    <TextBlock
                        Grid.Column="1"
                        VerticalAlignment="Center"
                        FontSize="12"
                        Text="{x:Bind ViewModel.DoneText, Mode=OneWay}"
                        TextTrimming="CharacterEllipsis"
                        Visibility="{x:Bind DoneTextVisibility(IsCompact, ViewModel.HasDoneMessage), Mode=OneWay}" />
```

e, depois do link "Abrir":

```xml
                    <HyperlinkButton
                        Grid.Column="3"
                        Padding="0"
                        VerticalAlignment="Center"
                        Command="{x:Bind ViewModel.CopyLinkCommand, Mode=OneWay}"
                        Content="{x:Bind ViewModel.CopyLinkLabel, Mode=OneWay}"
                        FontSize="12"
                        Foreground="{ThemeResource TextSecondaryBrush}" />
```

Em `ProviderRow.xaml.cs`, junto de `FileNameVisibility`:

```csharp
        /// <summary>The compact panel has no room for "Enviado · nome" next to two links; failure messages still show.</summary>
        private Visibility DoneTextVisibility(bool compact, bool hasMessage) =>
            compact && !hasMessage ? Visibility.Collapsed : Visibility.Visible;
```

No painel compacto, com o texto escondido, a coluna `*` fica vazia e os links ficam à direita, ao lado do ✓ à esquerda: aceito (é o desenho aprovado).

- [ ] **Step 7: Protótipo**

Em `app/docs/widget.dc.html`, no bloco `<sc-if value="{{p.done}}">`, depois de `<span style="color: #b3bcc8">Abrir</span>`: `<span style="color: #b3bcc8">Copiar link</span>`.

- [ ] **Step 8: Build**

Run: `dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

- [ ] **Step 9: Verificar, um arquivo (Google)**

Com o app de desenvolvimento aberto e o Google Drive ligado:
1. Soltar um arquivo de texto pequeno (`explorer-drop.ps1`) e esperar "Enviado · nome".
2. `Set-Clipboard "vazio"` (para não confundir com um link antigo).
3. Invocar "Copiar link" por UI Automation (`InvokePattern` no elemento de nome "Copiar link"); ler os nomes visíveis a cada 100 ms.
   Expected: aparece "Copiando…" e depois "Link copiado"; 2 s depois volta "Copiar link".
4. `$link = Get-Clipboard` → uma linha, `https://drive.google.com/file/d/…`.
   `Invoke-WebRequest $link -UseBasicParsing -MaximumRedirection 5` (sem cookies) → status 200 e `BaseResponse.ResponseUri` **não** contém `accounts.google.com`.
5. Controle negativo (prova que o teste distingue): soltar outro arquivo, pegar o link do dono pela notificação (`ToastNotificationManager.History`, `arguments` da ação "Abrir") **sem** clicar em "Copiar link"; o mesmo `Invoke-WebRequest` termina em `accounts.google.com`.

- [ ] **Step 10: Verificar, leva de 3 (Google)**

Soltar 3 arquivos de uma vez (`explorer-drop.ps1 -All`), esperar "Enviado · 3 arquivos", invocar "Copiar link".
Expected: `Get-Clipboard` devolve 3 linhas, 3 links diferentes, cada um com status 200 sem `accounts.google.com`.

- [ ] **Step 11: Review Focus 2 — área de transferência ocupada**

Num PowerShell à parte, `OpenClipboard(0)` por P/Invoke, esperar 3 s, `CloseClipboard()`. Durante esses 3 s, invocar "Copiar link".
Expected: a linha mostra "Não foi possível criar o link" por ~3 s e volta a "Enviado · …"; o link volta a "Copiar link"; nenhuma exceção no `crash.log`.

- [ ] **Step 12: Review Focus 3 — leva nova durante a cópia**

Com uma leva de ~10 arquivos pequenos no estado "Enviado", invocar "Copiar link" e, logo em seguida (sem esperar), soltar outro arquivo.
Expected: a linha do envio novo nunca mostra "Link copiado" nem fica presa em "Copiando…"; ao terminar o envio novo, o link mostra "Copiar link".

- [ ] **Step 13: Prints**

Prints do estado "Enviado" no widget (tema escuro e claro) e no painel compacto (recolher, arrastar até a faixa e soltar, como no `compact-test.ps1`).
Expected: "Enviado · nome", "Abrir" e "Copiar link" alinhados no widget; no compacto, ✓, "Abrir" e "Copiar link" sem cortar texto.

- [ ] **Step 14: Commit**

```powershell
git add app/Services/ShareLinkService.cs app/Helpers/Win32.cs app/ViewModels app/App.xaml.cs app/Views/Controls app/Strings/pt-BR/Resources.resw app/docs/widget.dc.html
git commit -m "feat: copy share links from the drop zone"
```

---

### Task 3: Botão "Copiar link" na notificação

**Files:**
- Modify: `app/Services/NotificationService.cs`
- Modify: `app/Services/ProtocolService.cs`
- Modify: `app/Program.cs`
- Modify: `app/App.xaml.cs`
- Modify: `app/Strings/pt-BR/Resources.resw`

**Interfaces:**
- Consumes: `ShareLinkService.CopyBatchAsync`, `ShareLinkService.DescribeFailure`, `BatchCopy`, `UploadFinishedEventArgs.BatchId` (Tarefa 2).
- Produces:
  - `NotificationService.ShowUploaded(string providerName, IReadOnlyList<string> fileNames, string? link, string? batchId)`
  - `NotificationService.ShowLinkCopied(IReadOnlyList<string> fileNames)`, `NotificationService.ShowLinkFailed(string reason)`
  - `ProtocolService.CopyLinkUri(string batchId)`, `ProtocolService.FindCopyLinkBatch(string? arguments)`, `ProtocolService.GetArguments(AppActivationArguments args)`
  - `App.OnActivatedByAnotherLaunch(AppActivationArguments args)`

- [ ] **Step 1: Strings**

```xml
  <data name="NotificationCopyLink" xml:space="preserve">
    <value>Copiar link</value>
  </data>
  <data name="NotificationLinkCopied" xml:space="preserve">
    <value>Link copiado</value>
  </data>
  <data name="NotificationLinkFailed" xml:space="preserve">
    <value>Não foi possível criar o link</value>
  </data>
```

- [ ] **Step 2: `ProtocolService`**

```csharp
        private static readonly Regex CopyLinkPattern = new(@"boxezone:copy-link\?batch=([0-9a-fA-F]{32})");

        /// <summary>The notification's "Copiar link" button.</summary>
        public static string CopyLinkUri(string batchId) => $"{Scheme}:copy-link?batch={batchId}";

        /// <summary>The batch id in a "boxezone:copy-link?batch=…" launch, or null.</summary>
        public static string? FindCopyLinkBatch(string? arguments)
        {
            Match match = CopyLinkPattern.Match(arguments ?? string.Empty);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        /// <summary>
        /// The command line of a launch redirected by Program.Main. The shell starts
        /// "BoxeZone.exe "boxezone:…"" (registered command), which arrives as a plain launch.
        /// </summary>
        public static string? GetArguments(AppActivationArguments args) => args.Data switch
        {
            Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch => launch.Arguments,
            Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol => protocol.Uri?.ToString(),
            _ => null,
        };
```

(`using System.Text.RegularExpressions;`, `using Microsoft.Windows.AppLifecycle;`.) Atualizar o comentário da classe: além de `boxezone:open`, `boxezone:copy-link?batch=…` (botão da notificação; com o app aberto, copia os links da leva).

- [ ] **Step 3: `NotificationService`**

Trocar o `Show` por uma versão com várias ações, som opcional e validade opcional:

```csharp
        private void Show(string title, string body, string uri, IReadOnlyList<(string Text, string Uri)> actions, bool silent = false, TimeSpan? lifetime = null)
        {
            if (_notifier is null)
            {
                return;
            }

            string buttons = actions.Count == 0
                ? string.Empty
                : "<actions>" + string.Concat(actions.Select(a =>
                    $"<action content=\"{Escape(a.Text)}\" arguments=\"{Escape(a.Uri)}\" activationType=\"protocol\"/>")) + "</actions>";
            string audio = silent ? "<audio silent=\"true\"/>" : string.Empty;

            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast activationType=\"protocol\" launch=\"{Escape(uri)}\"><visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text><text>{Escape(body)}</text>" +
                $"</binding></visual>{buttons}{audio}</toast>");

            var toast = new ToastNotification(xml);
            if (lifetime is { } span)
            {
                toast.ExpirationTime = DateTimeOffset.Now + span;
            }

            _notifier.Show(toast);
        }
```

Chamadores:

```csharp
        public void ShowUploaded(string providerName, IReadOnlyList<string> fileNames, string? link, string? batchId)
        {
            var actions = new List<(string Text, string Uri)>();
            if (link is not null)
            {
                actions.Add((_strings.GetString("NotificationOpen"), link));
            }

            if (batchId is not null)
            {
                actions.Add((_strings.GetString("NotificationCopyLink"), ProtocolService.CopyLinkUri(batchId)));
            }

            Show(string.Format(_strings.GetString("NotificationUploadedFormat"), providerName), Summarize(fileNames), link ?? ProtocolService.OpenWidgetUri, actions);
        }

        /// <summary>Feedback for the notification's "Copiar link": silent and short-lived.</summary>
        public void ShowLinkCopied(IReadOnlyList<string> fileNames) =>
            Show(_strings.GetString("NotificationLinkCopied"), Summarize(fileNames), ProtocolService.OpenWidgetUri, [], silent: true, lifetime: TimeSpan.FromMinutes(1));

        public void ShowLinkFailed(string reason) =>
            Show(_strings.GetString("NotificationLinkFailed"), reason, ProtocolService.OpenWidgetUri, [], silent: true, lifetime: TimeSpan.FromMinutes(1));

        private string Summarize(IReadOnlyList<string> fileNames) =>
            fileNames.Count == 1 ? fileNames[0] : string.Format(_strings.GetString("NotificationFilesFormat"), fileNames.Count);
```

`ShowFailed` passa `[]`; `ShowUpdateAvailable` passa `[(_strings.GetString("NotificationUpdateDownload"), url)]`. (`using System.Linq;`)

- [ ] **Step 4: Ativação**

`Program.cs`: `instance.Activated += (_, e) => App.OnActivatedByAnotherLaunch(e);`

`App.xaml.cs` (`using Microsoft.Windows.AppLifecycle;`):

```csharp
        /// <summary>
        /// Another launch of BoxeZone was redirected here (Program.Main). Called off the UI thread.
        /// "boxezone:copy-link?batch=…" (notification button) copies that batch's links without
        /// showing the widget; anything else, or a batch no longer remembered, shows the widget.
        /// </summary>
        public static void OnActivatedByAnotherLaunch(AppActivationArguments args)
        {
            var app = (App)Current;
            string? batchId = ProtocolService.FindCopyLinkBatch(ProtocolService.GetArguments(args));
            app._window?.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (batchId is not null && await app.CopyBatchLinksAsync(batchId))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex);
                }

                app._dockService?.ShowNormal();
            });
        }

        private async Task<bool> CopyBatchLinksAsync(string batchId)
        {
            if (_shareLinks is null || await _shareLinks.CopyBatchAsync(batchId) is not { } copy)
            {
                return false;
            }

            if (copy.Result.Succeeded)
            {
                _notificationService?.ShowLinkCopied(copy.Batch.Items.Select(i => i.Name).ToList());
            }
            else
            {
                _notificationService?.ShowLinkFailed(ShareLinkService.DescribeFailure(copy.Result.Error!.Value, copy.Batch.Provider.DisplayName));
            }

            return true;
        }
```

`NotifyUploadFinished`: `_notificationService.ShowUploaded(provider.Name, e.FileNames, e.Link, e.BatchId);`

- [ ] **Step 5: Build**

Run: `dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

- [ ] **Step 6: Verificar o botão**

1. Soltar um arquivo pequeno no Google Drive; esperar "Enviado".
2. No histórico (`ToastNotificationManager.History.GetHistory("BoxeZone.App")`, o mais novo é o índice 0), ler o XML: há duas ações, "Abrir" e "Copiar link", e a segunda tem `arguments="boxezone:copy-link?batch=<32 hex>"`.
3. `Set-Clipboard "vazio"`; executar `Start-Process <BoxeZone.exe de bin\x64\Debug> '"boxezone:copy-link?batch=<id>"'`.
   Expected: em até ~5 s, `Get-Clipboard` devolve o link, que abre sem login (como na Tarefa 2); o histórico ganha uma notificação "Link copiado" com o nome do arquivo; o widget **não** muda de estado nem de posição (comparar o retângulo da janela antes e depois).

- [ ] **Step 7: Review Focus 4 — leva desconhecida**

Recolher o widget ("–"); executar `Start-Process <exe> '"boxezone:copy-link?batch=00000000000000000000000000000000"'`.
Expected: o widget abre (estado Normal), nenhuma notificação nova, nada no `crash.log`.

- [ ] **Step 8: Commit**

```powershell
git add app/Services/NotificationService.cs app/Services/ProtocolService.cs app/Program.cs app/App.xaml.cs app/Strings/pt-BR/Resources.resw
git commit -m "feat: copy share links from the upload notification"
```

---

### Task 4: Política, documentação e versão de teste

**Files:**
- Modify: `website/privacidade/index.html`
- Modify: `app/docs/arquitetura.md`, `app/docs/proximos-passos.md`
- Delete (só depois da versão final publicada): `app/docs/specs/2026-10-13-copiar-link-design.md`, `app/docs/plans/2026-10-13-copiar-link.md`

- [ ] **Step 1: Política de privacidade**

Em `website/privacidade/index.html`:
- Data: `Atualizada em <dia> de <mês> de 2026.` (a data do commit).
- Google Drive, depois do item `drive.file`: `<li>Quando você clica em "Copiar link", o app dá ao arquivo enviado a permissão "qualquer pessoa com o link pode ver" e copia esse link. Nada é compartilhado sem esse clique.</li>`
- OneDrive, depois do item `Files.ReadWrite`: `<li>A mesma permissão cria o link de compartilhamento quando você clica em "Copiar link" (link de visualização para qualquer pessoa com o link, ou só para a sua organização, se a sua conta de trabalho não permitir links públicos). Nada é compartilhado sem esse clique.</li>`
- Dropbox, depois de `files.content.write`: `<li>Permissão <code>sharing.write</code>: usada só quando você clica em "Copiar link", para criar um link de visualização do arquivo enviado. Nada é compartilhado sem esse clique.</li>`

- [ ] **Step 2: `arquitetura.md`**

Na seção "Provedores e upload": o contrato novo (`UploadedItem`, `CreateShareLinkAsync`), o "Copiar link" (só no clique; leva = um link por linha; Google por permissão `anyone` dentro do `drive.file`; OneDrive `createLink` anônimo com recuo para organização; Dropbox `sharing.write`, link existente reaproveitado, sessão antiga = `MissingPermission` sem desconectar), a área de transferência por Win32 com a janela da aba como dona (a WinRT exige app em primeiro plano; `SetClipboardData` falha sem dona) e o botão da notificação (`boxezone:copy-link?batch=…`, últimas 20 levas na memória, leva desconhecida abre o widget, `ILaunchActivatedEventArgs.Arguments` traz a URI).

- [ ] **Step 3: `proximos-passos.md`**

Remover "Copiar link compartilhável" de "Envio"; na "Rodada de funções", marcar a parte 1 como feita; em "Concluído", uma linha com os commits das Tarefas 1 a 3.

- [ ] **Step 4: Commit e passo do usuário**

```powershell
git add website/privacidade/index.html app/docs
git commit -m "docs: share links in the privacy policy and architecture notes"
```

Antes de gerar a versão de teste, o usuário liga `sharing.write` no App Console do Dropbox (Permissions → marcar → Submit). Sem isso, não publicar (ver Global Constraints).

- [ ] **Step 5: Versão de teste**

Depois da confirmação do usuário: `git push -u origin feat/share-link`, tag `v1.2.0-rc.1` no topo da branch, push da tag, acompanhar o `release.yml` (o primeiro com o Inno Setup fixo da arrumação) até a Release aparecer como pré-lançamento com os dois instaladores. Passos de teste para o usuário: copiar link no Google, OneDrive e Dropbox (um arquivo e leva), abrir os links numa janela anônima, botão da notificação, e no Dropbox a mensagem de reconectar antes de conectar de novo.

- [ ] **Step 6: Final**

Com o OK do usuário: merge da `feat/share-link` (que contém a `chore/cleanup`) na `main`, apagar o spec e o plano, tag `v1.2.0`.
