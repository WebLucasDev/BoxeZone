# Enviar pastas e retomar envios: plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** soltar pastas (recriadas no destino, "(1)" se o nome existir) e retomar envios interrompidos, sem perder o que já foi enviado, quando a internet cai (espera sem limite) ou quando o app fecha (continua sozinho ao abrir).

**Architecture:** o envio deixa de receber `StorageFile` e passa a trabalhar com uma **fila** (`UploadQueueState`: pastas soltas e arquivos, cada arquivo com seu `ResumeState`), montada a partir do drop por um `FolderScanner` e guardada criptografada por um `UploadQueueStore`. Os provedores ganham `UploadAsync(UploadJob, …, saveResumeState, …)` retomável e `CreateFolderAsync` com renomeação. Um `NetworkWaiter` segura o envio em "Aguardando conexão…" até a rede voltar.

**Tech Stack:** WinUI 3 / .NET 10, CommunityToolkit.Mvvm, Google.Apis.Drive.v3 (`ResumeAsync`, `UploadSessionData`), Graph por `HttpClient` (upload session, `nextExpectedRanges`), Dropbox.Api (`CreateFolderV2Async`, `UploadSessionOffsetError.CorrectOffset`), `Windows.Networking.Connectivity.NetworkInformation`, `System.Text.Json`, `TokenStore`.

**Spec:** `app/docs/specs/2026-10-13-pastas-e-retomar-design.md`

## Global Constraints

- Nenhum pacote novo. Strings de UI só no `.resw`; código, nomes e comentários em inglês.
- Escopos dos provedores não mudam.
- A fila salva contém caminhos de arquivos e endereços de envio (o do OneDrive é pré-autenticado): **só no `TokenStore`** (criptografado), nunca em `settings.json` nem em log.
- Arquivos ocultos ou de sistema e pontos de nova análise ficam de fora; pastas vazias não são criadas.
- `dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64` com 0 avisos ao fim de cada tarefa.
- O repositório não tem projeto de testes: a verificação é build + scripts de UI Automation no app rodando (scratchpad), como nas rodadas anteriores. Backup do `settings.json` antes dos testes; arquivos de teste com nome `boxezone-teste-*`; só o Google Drive recebe envios de teste (o drop vai para a área dele, a de cima — conferir antes de soltar).

## Review Focus

1. **Arquivo enorme com a internet caindo várias vezes**: cada volta continua do ponto certo; nada de arquivo duplicado ou corrompido no destino. Teste de retomada na Tarefa 2 (tamanho final igual ao local) e de rede na Tarefa 3.
2. **App fechado entre o fim de um arquivo e a gravação da fila**: ao reabrir, aquele arquivo pode ser enviado de novo e virar "nome (1)" no destino. Aceito (raro); registrar. A fila é gravada logo após cada arquivo terminar para reduzir a janela.
3. **Pasta enorme (milhares de arquivos) ou com caminhos muito longos**: o drop não pode travar a interface; a varredura roda fora da thread da UI. Teste na Tarefa 1 com 1.000 arquivos pequenos (só a varredura e a contagem; envio de uma parte e cancelar).
4. **Leva com falha permanente (sem espaço, sessão expirada) salva na fila**: ao reabrir, tenta de novo e mostra a mesma falha; não pode entrar em ciclo nem travar o início. Coberto por leitura do código (Tarefa 2) e pelo teste do usuário.
5. **Desconectar ou trocar de conta com fila salva**: a fila é apagada e nada é enviado para a conta nova. Teste na Tarefa 2.

---

### Task 1: Pastas (varredura, criação com "(1)", fila em memória, envio por caminho)

**Files:**
- Create: `app/Services/Upload/UploadQueue.cs` (tipos da fila)
- Create: `app/Services/Upload/FolderScanner.cs`
- Modify: `app/Services/Storage/IStorageProvider.cs`, `GoogleDriveProvider.cs`, `OneDriveProvider.cs`, `DropboxProvider.cs`
- Modify: `app/ViewModels/ProviderViewModel.cs`, `app/Views/Controls/ProviderRow.xaml.cs`
- Modify: `app/Strings/pt-BR/Resources.resw`

**Interfaces:**
- Produces:
  - `public sealed record UploadJob(string LocalPath, string FileName, string TargetFolder, long Size, DateTimeOffset LastWriteTime, string? ResumeState);`
  - `Task<UploadedItem> IStorageProvider.UploadAsync(UploadJob job, IProgress<double> progress, Action<string> saveResumeState, CancellationToken ct)`
  - `Task<UploadedItem> IStorageProvider.CreateFolderAsync(string parentFolder, string name, CancellationToken ct)`
  - `UploadQueueState`, `QueuedFolder`, `QueuedFile`, `QueuedFileStatus` (abaixo), `FolderScanner.ScanAsync(IReadOnlyList<string> paths)`
  - `ProviderViewModel.EnqueueAsync(IReadOnlyList<string> paths)` (substitui `UploadFilesAsync`)

- [ ] **Step 1: Tipos da fila**

`app/Services/Upload/UploadQueue.cs`:

```csharp
using System;
using System.Collections.Generic;
using BoxeZone.Services.Storage;

namespace BoxeZone.Services.Upload
{
    public enum QueuedFileStatus
    {
        Pending,
        Done,

        /// <summary>The local file disappeared; "Tentar de novo" moves on without it.</summary>
        Skipped,
    }

    /// <summary>A folder dropped on the zone: recreated at the destination before its files.</summary>
    public sealed class QueuedFolder
    {
        public string LocalPath { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>Drop order, shared with loose files: "Copiar link" lists items in this order.</summary>
        public int Order { get; set; }

        /// <summary>Set once the folder exists at the destination (its real name may end in " (1)").</summary>
        public UploadedItem? Created { get; set; }
    }

    public sealed class QueuedFile
    {
        public string LocalPath { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        /// <summary>Index in <see cref="UploadQueueState.Folders"/>, or -1 for a loose file.</summary>
        public int FolderIndex { get; set; } = -1;

        /// <summary>Subfolder inside the dropped folder, with "/" separators ("" at its root).</summary>
        public string RelativeFolder { get; set; } = string.Empty;

        public int Order { get; set; }

        public long Size { get; set; }

        public DateTimeOffset LastWriteTime { get; set; }

        public string? ResumeState { get; set; }

        public QueuedFileStatus Status { get; set; }

        public UploadedItem? Uploaded { get; set; }
    }

    /// <summary>One provider's batch in progress. Saved encrypted (UploadQueueStore) so it survives a restart.</summary>
    public sealed class UploadQueueState
    {
        public string TargetFolder { get; set; } = "/";

        public List<QueuedFolder> Folders { get; set; } = [];

        public List<QueuedFile> Files { get; set; } = [];

        public int NextOrder { get; set; }
    }
}
```

- [ ] **Step 2: `FolderScanner`**

`app/Services/Upload/FolderScanner.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BoxeZone.Services.Upload
{
    /// <summary>What a drop turned into: loose files and folders, each folder with its files.</summary>
    public sealed record ScannedFile(string LocalPath, string RelativeFolder, long Size, DateTimeOffset LastWriteTime);

    public sealed record ScannedFolder(string LocalPath, string Name, IReadOnlyList<ScannedFile> Files);

    public sealed record ScanResult(IReadOnlyList<ScannedFile> LooseFiles, IReadOnlyList<ScannedFolder> Folders);

    /// <summary>
    /// Walks dropped paths off the UI thread. Skips hidden and system entries (desktop.ini,
    /// Thumbs.db) and reparse points (links can loop); empty folders produce nothing.
    /// </summary>
    public static class FolderScanner
    {
        private const FileAttributes Skipped = FileAttributes.Hidden | FileAttributes.System;

        public static Task<ScanResult> ScanAsync(IReadOnlyList<string> paths) => Task.Run(() =>
        {
            var loose = new List<ScannedFile>();
            var folders = new List<ScannedFolder>();
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & Skipped) == 0)
                    {
                        loose.Add(new ScannedFile(info.FullName, string.Empty, info.Length, info.LastWriteTimeUtc));
                    }
                }
                else if (Directory.Exists(path))
                {
                    var root = new DirectoryInfo(path);
                    var files = new List<ScannedFile>();
                    Walk(root, string.Empty, files);
                    if (files.Count > 0)
                    {
                        folders.Add(new ScannedFolder(root.FullName, root.Name, files));
                    }
                }
            }

            return new ScanResult(loose, folders);
        });

        private static void Walk(DirectoryInfo folder, string relative, List<ScannedFile> files)
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = folder.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // A protected or vanished subfolder: send what can be read.
                return;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if ((entry.Attributes & (Skipped | FileAttributes.ReparsePoint)) != 0)
                {
                    continue;
                }

                if (entry is FileInfo file)
                {
                    files.Add(new ScannedFile(file.FullName, relative, file.Length, file.LastWriteTimeUtc));
                }
                else if (entry is DirectoryInfo sub)
                {
                    Walk(sub, relative.Length == 0 ? sub.Name : $"{relative}/{sub.Name}", files);
                }
            }
        }
    }
}
```

- [ ] **Step 3: Contrato dos provedores**

Em `IStorageProvider.cs`, trocar `UploadAsync` e acrescentar `CreateFolderAsync` e o `UploadJob` (texto do spec, "Contrato dos provedores"):

```csharp
        /// <summary>
        /// Uploads job.LocalPath into job.TargetFolder (created if missing). Calls saveResumeState
        /// whenever the upload can be continued from a new point; job.ResumeState continues one.
        /// </summary>
        Task<UploadedItem> UploadAsync(UploadJob job, IProgress<double> progress, Action<string> saveResumeState, CancellationToken ct);

        /// <summary>
        /// Creates a folder inside parentFolder, renamed ("Nome (1)") when the name is taken.
        /// The item carries the real name; Id and WebUrl point to the folder.
        /// </summary>
        Task<UploadedItem> CreateFolderAsync(string parentFolder, string name, CancellationToken ct);
```

```csharp
    /// <summary>A file to upload. ResumeState: the provider's own data to continue an interrupted upload (null: from zero).</summary>
    public sealed record UploadJob(string LocalPath, string FileName, string TargetFolder, long Size, DateTimeOffset LastWriteTime, string? ResumeState);
```

Nesta tarefa os provedores **ainda não retomam**: leem o arquivo por `FileStream` (`new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)`), ignoram `job.ResumeState` e não chamam `saveResumeState`. Tipo MIME (Google): de `HKEY_CLASSES_ROOT\{extensão}` valor `Content Type`, senão `application/octet-stream`:

```csharp
        private static string ContentTypeFor(string fileName)
        {
            string extension = Path.GetExtension(fileName);
            using RegistryKey? key = extension.Length > 0 ? Registry.ClassesRoot.OpenSubKey(extension) : null;
            return key?.GetValue("Content Type") as string ?? FallbackContentType;
        }
```

`CreateFolderAsync`:

- Google (dentro de `try` com o mesmo `catch` do `UploadAsync`):

```csharp
            string parentId = await GetOrCreateFolderAsync(drive, parentFolder, ct);
            string candidate = name;
            for (int n = 1; await FindFolderAsync(drive, parentId, candidate, ct) is not null; n++)
            {
                candidate = $"{name} ({n})";
            }

            FilesResource.CreateRequest create = drive.Files.Create(new DriveFile { Name = candidate, MimeType = FolderMimeType, Parents = [parentId] });
            create.Fields = "id, name, webViewLink";
            DriveFile folder = await create.ExecuteAsync(ct);
            return new UploadedItem(folder.Id, folder.Name, folder.WebViewLink);
```

  com `FindFolderAsync` extraído da busca que o `GetOrCreateFolderAsync` já faz (mesma `Q`, `Fields = "files(id)"`, devolve o id ou null), e o `GetOrCreateFolderAsync` passando a usá-lo.

- OneDrive:

```csharp
            string parentPath = string.Join('/', parentFolder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            string url = parentPath.Length == 0 ? "/me/drive/root/children" : $"/me/drive/root:/{parentPath}:/children";
            using HttpRequestMessage request = await CreateGraphRequestAsync(HttpMethod.Post, url, ct);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = name, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "rename" }),
                Encoding.UTF8, "application/json");
            return ReadItem(await SendAsync(request, ct));
```

  O `/children` não cria o pai que falta (o destino, "/BoxeZone", pode ainda não existir numa conta nova): antes do pedido acima, `EnsureFolderPathAsync(parentFolder)` cria cada segmento do caminho do pai com `POST …/children` e `"@microsoft.graph.conflictBehavior": "fail"`, ignorando a resposta 409 (já existe).

- Dropbox:

```csharp
            string parent = "/" + string.Join('/', parentFolder.Split('/', StringSplitOptions.RemoveEmptyEntries));
            CreateFolderResult result = await client.Files.CreateFolderV2Async($"{parent.TrimEnd('/')}/{name}", autorename: true);
            FolderMetadata folder = result.Metadata;
            return new UploadedItem(folder.Id, folder.Name, BuildFolderLink(folder.PathDisplay));
```

  com `BuildFolderLink(path) => "https://www.dropbox.com/home" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString))`. (O Dropbox cria os pais que faltam.)

- [ ] **Step 4: Drop passa caminhos**

`ProviderRow.DropZone_Drop`: em vez de `items.OfType<StorageFile>()`, os caminhos de arquivos **e pastas**: `items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList()` e `await ViewModel.EnqueueAsync(paths)`. (Itens sem caminho, virtuais, ficam de fora.)

- [ ] **Step 5: `ProviderViewModel` com fila em memória**

Substituir `_batch`, `_weights`, `_totalWeight`, `_sentWeight`, `_failedFiles`, `UploadFilesAsync`, `AddToBatch` e `GetSizesAsync` por:

```csharp
        private UploadQueueState? _queue;
```

e os métodos:

- `public async Task EnqueueAsync(IReadOnlyList<string> paths)`: `ScanResult scan = await FolderScanner.ScanAsync(paths);` (nada: `return`). Sem fila ativa (`!IsUploading`): `_queue = new UploadQueueState { TargetFolder = TargetFolder }`. Para cada pasta: `QueuedFolder { LocalPath, Name, Order = _queue.NextOrder++ }` e um `QueuedFile` por arquivo dela (`FolderIndex` = índice da pasta, `RelativeFolder`, `Order` = o da pasta). Para cada arquivo solto: `QueuedFile { FolderIndex = -1, Order = _queue.NextOrder++ }`. Já estava enviando: só acrescenta e atualiza `FileCount`; senão chama `await RunQueueAsync()`.
- `private async Task RunQueueAsync()`: o laço do `UploadFilesAsync` atual, sobre `_queue.Files` com `Status == Pending` (a lista pode crescer; `Count` relido a cada volta). Antes do primeiro arquivo de uma pasta sem `Created`: `folder.Created = await Provider.CreateFolderAsync(_queue.TargetFolder, folder.Name, ct)`. Destino do arquivo: solto → `_queue.TargetFolder`; da pasta → `Join(_queue.TargetFolder, folder.Created.Name, file.RelativeFolder)` (com `Join` juntando segmentos não vazios por "/"). Arquivo: `if (!File.Exists(file.LocalPath))` → falha com `ErrorFileMissing` e `file.Status = Skipped` antes de mostrar a falha (o "Tentar de novo" segue sem ele). `UploadJob` a partir do `QueuedFile`; `saveResumeState` grava em `file.ResumeState` (nesta tarefa, só em memória). Fim de arquivo: `Status = Done`, `Uploaded = item`, `ResumeState = null`.
- Pesos: `FileCount` = arquivos não `Skipped`; `FileIndex` = posição do arquivo atual entre eles; progresso = (bytes dos `Done` + fração do atual × tamanho) / total, com tamanho mínimo 1 (como hoje).
- Fim da leva: `_uploadedItems` = pastas (`Created`) e arquivos soltos (`Uploaded`), ordenados por `Order` (é o que o "Copiar link" e o `ShareLinkService.Register` recebem). Resumo: uma pasta e nenhum arquivo solto → `DoneTitle` = nome real da pasta; senão as regras atuais (`FileCount`/`FileName`). `OpenUploaded`: uma pasta → `WebUrl` dela; senão o último arquivo. `UploadFinishedEventArgs.FileNames`: `[nome da pasta]` no caso da pasta única; senão os nomes dos arquivos enviados. `Link` = o mesmo que o "Abrir".
- `DoneLabel` passa a usar um campo `_doneTitle` (nome da pasta ou nulo) antes das regras atuais.
- `RetryAsync`: reconecta se preciso e `await RunQueueAsync()` (a fila continua de onde falhou).
- Cancelar: limpa `_queue`.

- [ ] **Step 6: Strings**

```xml
  <data name="ErrorFileMissing" xml:space="preserve">
    <value>Arquivo não encontrado</value>
  </data>
```

- [ ] **Step 7: Build** — `0 Aviso(s)`, `0 Erro(s)`.

- [ ] **Step 8: Verificar (Google)**

Pasta `bz-pasta\Fotos teste` com `a.txt`, `sub1\b.txt`, `sub1\sub2\c.txt`, um `oculto.txt` com atributo Hidden e um `desktop.ini` (Hidden+System). Soltar a pasta (arrastar o item da pasta pelo Explorer).
Expected: "1 de 3"… "Enviado · Fotos teste"; no Drive (busca `title contains 'Fotos teste'` e listagem dos filhos), a pasta com `a.txt`, `sub1/b.txt`, `sub1/sub2/c.txt`, sem `oculto.txt` nem `desktop.ini`.
Soltar a mesma pasta de novo. Expected: "Enviado · Fotos teste (1)".
"Copiar link": uma linha; `Invoke-WebRequest` sem cookies → 200 (a pasta de outra leva, sem compartilhar, dá 401 ou tela de login).
Soltar pasta + um arquivo solto juntos (`-All` numa pasta com os dois). Expected: "Enviado · 4 arquivos" e 2 links no "Copiar link".
Review Focus 3: pasta com 1.000 arquivos pequenos; soltar; a interface responde (o "Cancelar" aparece em segundos) e o contador mostra "x de 1000"; cancelar depois de alguns.

- [ ] **Step 9: Commit** — `feat: upload dropped folders`.

---

### Task 2: Retomar (provedores retomáveis, fila salva, retomada ao abrir)

**Files:**
- Create: `app/Services/Upload/UploadQueueStore.cs`
- Modify: os três provedores, `app/ViewModels/ProviderViewModel.cs`, `app/ViewModels/MainViewModel.cs`, `app/App.xaml.cs`, `app/Strings/pt-BR/Resources.resw`

**Interfaces:**
- Consumes: tipos e contrato da Tarefa 1.
- Produces: `UploadQueueStore(TokenStore)` com `Task<UploadQueueState?> LoadAsync(string providerId)`, `Task SaveAsync(string providerId, UploadQueueState state)`, `void Delete(string providerId)`; `ProviderViewModel.ResumeSavedQueueAsync()`; propriedade `IsResuming`.

- [ ] **Step 1: Provedores retomáveis**

- **Google**: `request.UploadSessionData += data => saveResumeState(data.UploadUri.AbsoluteUri);` e `IUploadProgress result = job.ResumeState is { } uri ? await request.ResumeAsync(new Uri(uri), ct) : await request.UploadAsync(ct);`. Se o resultado de uma retomada falhar com `GoogleApiException` 404/410 (sessão expirada), refazer com um pedido novo e `UploadAsync` (do zero). O `ResponseBody` vem preenchido nos dois caminhos.
- **OneDrive**: `ResumeState` = `uploadUrl`. Com estado: `GET uploadUrl` (sem cabeçalho de autenticação) → `nextExpectedRanges[0]` "início-" → `offset`; 404/410 → sessão nova. Sem estado: cria a sessão e chama `saveResumeState(uploadUrl)`. `stream.Seek(offset)`; depois de cada parte, `saveResumeState(uploadUrl)`. **Só apagar a sessão no cancelamento do usuário** (o `catch (OperationCanceledException)`); nas outras falhas ela fica, para retomar. Arquivo vazio: PUT simples, como hoje (sem retomar).
- **Dropbox**: `ResumeState` = `"{sessionId}|{offset}"`. Com estado: `stream.Seek(offset)` e segue com `UploadSessionAppendV2Async`; `ApiException<UploadSessionAppendError>` com `IsIncorrectOffset` → `offset = ex.ErrorResponse.AsIncorrectOffset.Value.CorrectOffset`, `Seek` e segue; `IsNotFound` ou `IsClosed` → sessão nova do zero. Sem estado: `UploadSessionStartAsync` com a primeira parte. Depois de cada parte (inclusive a primeira), `saveResumeState($"{sessionId}|{offset}")`. O `UploadSessionFinishAsync` com `incorrect_offset` também usa o `CorrectOffset` (lookup error) e tenta de novo uma vez.

- [ ] **Step 2: `UploadQueueStore`**

```csharp
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BoxeZone.Services.Upload
{
    /// <summary>
    /// The batch in progress of each provider, in the TokenStore (encrypted): it holds file paths
    /// and upload session addresses (OneDrive's is pre-authenticated). Deleted when the batch
    /// ends, is cancelled or the account is disconnected.
    /// </summary>
    public sealed class UploadQueueStore(TokenStore tokenStore)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<UploadQueueState?> LoadAsync(string providerId)
        {
            string? json = await tokenStore.ReadAsync(Key(providerId));
            try
            {
                return json is null ? null : JsonSerializer.Deserialize<UploadQueueState>(json);
            }
            catch (JsonException)
            {
                // From another version, or damaged: the batch is lost, nothing else.
                Delete(providerId);
                return null;
            }
        }

        public async Task SaveAsync(string providerId, UploadQueueState state)
        {
            string json = JsonSerializer.Serialize(state);
            await _gate.WaitAsync();
            try
            {
                await tokenStore.WriteAsync(Key(providerId), json);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Delete(string providerId) => tokenStore.Delete(Key(providerId));

        private static string Key(string providerId) => $"upload-queue-{providerId}";
    }
}
```

(Serializar na thread da UI, onde a fila muda; só a escrita é assíncrona.)

- [ ] **Step 3: Gravar a fila**

`ProviderViewModel` recebe `UploadQueueStore` (último parâmetro do construtor; `MainViewModel` repassa; `App` cria com o `tokenStore`). Grava: ao montar ou crescer a leva, ao criar uma pasta, ao terminar cada arquivo, e no `saveResumeState` no máximo a cada 2 s por arquivo (o callback pode vir de outra thread: `_dispatcher.TryEnqueue` para atualizar `file.ResumeState` e decidir gravar). Apaga: fim da leva com sucesso, "Cancelar", "Desconectar" (no `ToggleAccountAsync`, antes do `DisconnectAsync`, cancelando o envio se houver). Falha comum (não "offline"): a fila **fica** salva (ao reabrir, tenta de novo).

- [ ] **Step 4: Retomar ao abrir**

`public async Task ResumeSavedQueueAsync()`: se não está enviando, está ligado e conectado e `LoadAsync(Id)` devolve uma fila com arquivos `Pending`: `_queue = fila`, `IsResuming = true`, `await RunQueueAsync()`. `IsResuming` volta a falso no primeiro relatório de progresso ou ao fim do primeiro arquivo. Chamado no fim do `RestoreAsync` (depois de religar o provedor) e depois de um `ConnectAsync` que termina conectado com o provedor ligado. Fila de outro destino é respeitada: `_queue.TargetFolder` é o da leva, não o atual.
`ProgressLabel`: `IsResuming ? "Retomando…" : $"{Progress:0}%"` (a Tarefa 3 acrescenta a espera de rede).

```xml
  <data name="UploadResuming" xml:space="preserve">
    <value>Retomando…</value>
  </data>
```

- [ ] **Step 5: Build** — 0 avisos.

- [ ] **Step 6: Verificar retomada (Google)**

Arquivo de 300 MB com bytes aleatórios (`RandomNumberGenerator.Fill` em blocos). Soltar; quando o progresso passar de 35%, `Stop-Process -Force` no app; anotar a porcentagem. Reabrir o app.
Expected: "Retomando…" e depois o progresso a partir de perto da porcentagem anotada (não de 0%); "Enviado · arquivo"; no Drive, **um** arquivo com esse nome e tamanho igual ao local (metadados pelo conector do Drive ou listagem).
Review Focus 5: soltar outro arquivo grande, fechar a 30%, reabrir com o Google **desligado** (preferências), conferir que nada envia; desconectar o Google nas Configurações; a fila some (`TokenStore` sem `upload-queue-gdrive`, conferir pela ausência do arquivo na pasta de tokens). Reconectar e ligar: nada é retomado.

- [ ] **Step 7: Commit** — `feat: resume interrupted uploads after a restart`.

---

### Task 3: Sem internet ("Aguardando conexão…")

**Files:**
- Create: `app/Services/Upload/NetworkWaiter.cs`
- Modify: `app/ViewModels/ProviderViewModel.cs`, `app/Strings/pt-BR/Resources.resw`

- [ ] **Step 1: `NetworkWaiter`**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace BoxeZone.Services.Upload
{
    /// <summary>
    /// Waits before retrying an upload that failed for lack of network: until Windows reports
    /// a network change, or 15 s, 30 s, then 60 s, whichever comes first. No overall limit.
    /// </summary>
    public static class NetworkWaiter
    {
        public static async Task WaitAsync(int attempt, CancellationToken ct)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            NetworkStatusChangedEventHandler handler = _ => changed.TrySetResult();
            NetworkInformation.NetworkStatusChanged += handler;
            try
            {
                TimeSpan delay = TimeSpan.FromSeconds(attempt switch { 0 => 15, 1 => 30, _ => 60 });
                await Task.WhenAny(changed.Task, Task.Delay(delay, ct));
                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                NetworkInformation.NetworkStatusChanged -= handler;
            }
        }
    }
}
```

- [ ] **Step 2: Espera no laço**

No `RunQueueAsync`, as chamadas `CreateFolderAsync` e `UploadAsync` passam por:

```csharp
        /// <summary>Retries while the provider reports no network ("Aguardando conexão…"), without limit.</summary>
        private async Task<T> WhileOfflineAsync<T>(Func<Task<T>> action, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    T result = await action();
                    IsWaitingForNetwork = false;
                    return result;
                }
                catch (StorageProviderException ex) when (ex.Kind == StorageErrorKind.Offline && !ct.IsCancellationRequested)
                {
                    IsWaitingForNetwork = true;
                    await SaveQueueAsync();
                    await NetworkWaiter.WaitAsync(attempt, ct);
                }
            }
        }
```

O `UploadJob` de cada tentativa é montado de novo dentro do `action`, com o `ResumeState` mais recente. `IsWaitingForNetwork` (observável) entra no `ProgressLabel`: `IsWaitingForNetwork ? "Aguardando conexão…" : IsResuming ? "Retomando…" : $"{Progress:0}%"`. O "Cancelar" cancela o `ct` e a espera acaba.

```xml
  <data name="UploadWaitingForNetwork" xml:space="preserve">
    <value>Aguardando conexão…</value>
  </data>
```

- [ ] **Step 3: Build** — 0 avisos.

- [ ] **Step 4: Verificar (Google)**

Arquivo de 200 MB; durante o envio (~20%), `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`: `ProxyServer = 127.0.0.1:9`, `ProxyEnable = 1`. Expected em até ~1 min: "Aguardando conexão…". Depois de 40 s, `ProxyEnable = 0`. Expected em até ~70 s: o progresso volta de onde parou e termina; tamanho no Drive igual ao local. Sempre restaurar `ProxyEnable`/`ProxyServer` originais no `finally` do script.
Se a mensagem não aparecer em 2 min (o .NET não percebe a troca com o app aberto): restaurar o proxy, registrar no ledger e deixar o teste de rede para o usuário (Wi-Fi desligado no meio).
"Cancelar" durante "Aguardando conexão…": volta a "Solte arquivos aqui" na hora.

- [ ] **Step 5: Commit** — `feat: wait for the network instead of failing an upload`.

---

### Task 4: Privacidade, documentação e versão de teste

- [ ] **Step 1:** `website/privacidade/index.html`, "O que fica no seu computador": item "**Envios em andamento**" (texto do spec); data da política. `app/installer/privacidade.txt`: "O acesso a essas contas e o andamento de envios não terminados ficam guardados só neste computador, criptografados pelo Windows." (UTF-8 com BOM, como o arquivo já é).
- [ ] **Step 2:** `app/docs/arquitetura.md`, "Provedores e upload": fila (`UploadQueueState`/`UploadQueueStore`), `ResumeState` por provedor, sessões só apagadas no cancelamento, `CreateFolderAsync` e "(1)", varredura de pastas e o que fica de fora, espera de rede, o que se aprendeu nos testes. `proximos-passos.md`: tirar "Enviar pastas" e "Retomar envios grandes" de "Envio"; rodada 1 feita, aguardando a `-rc`; linhas em "Concluído".
- [ ] **Step 3:** commit `docs: folders and resumable uploads in the privacy policy and notes`.
- [ ] **Step 4:** com o OK do usuário: push da branch, tag `v1.3.0-rc.1`, acompanhar o `release.yml`. Teste do usuário: pasta com subpastas no OneDrive e no Dropbox ("(1)" na segunda vez; link da pasta abrindo sem login), arquivo grande com o app fechado no meio e reaberto (nos dois), Wi-Fi desligado no meio de um envio e ligado depois.
- [ ] **Step 5:** com o OK: merge na `main`, apagar spec e plano, tag `v1.3.0`.
