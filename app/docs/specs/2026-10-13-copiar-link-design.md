# Copiar link compartilhável: design

Parte 1 da rodada de funções decidida em 13/10/2026 (ver `app/docs/proximos-passos.md`). Documento temporário: ao concluir, o que importa vai para `arquitetura.md` e este arquivo é apagado.

## Objetivo

Depois de enviar, o usuário copia um link que **qualquer pessoa com o link** abre, sem conta, para mandar a alguém. O arquivo só fica compartilhado quando o usuário clica em "Copiar link"; nada é compartilhado sozinho.

Decisões do usuário (13/10/2026):

- Tipo de link: "qualquer um com o link" (leitura).
- Leva com vários arquivos: um link por arquivo, um por linha.
- Onde: na área de drop (estado "Enviado") e como botão na notificação de envio concluído.

## O que o usuário vê

### Área de drop, estado "Enviado"

- Linha atual: ✓ · "Enviado · nome" · "Abrir". Passa a ser: ✓ · "Enviado · nome" · "Abrir" · **"Copiar link"** (mesmo estilo de link do "Abrir": 12 px, `TextSecondaryBrush`).
- Clique em "Copiar link":
  1. o link vira **"Copiando…"** e fica desabilitado enquanto os links são criados;
  2. deu certo: os links vão para a área de transferência e o link vira **"Link copiado"** por 2 s, depois volta a "Copiar link";
  3. falhou: o texto da linha (no lugar de "Enviado · nome") mostra **"Não foi possível criar o link"** por 3 s; o link volta a "Copiar link". Dropbox sem a permissão nova: **"Conecte o Dropbox de novo para copiar links"** por 5 s.
- Leva: o texto continua "Enviado · 5 arquivos"; "Copiar link" cria e copia os 5 links, um por linha (`\r\n`), na ordem do envio.
- Clicar de novo depois de copiado cria de novo (os provedores devolvem o mesmo link; ver abaixo), sem custo para o usuário.
- **Painel compacto (200 px)**: no estado "Enviado" a linha fica ✓ · "Abrir" · "Copiar link", sem o texto "Enviado · nome" (não cabe). As mensagens de falha aparecem no lugar do texto enquanto duram.

### Notificação de envio concluído

- Hoje: título "Enviado para o {provedor}", corpo nome ou "N arquivos", botão "Abrir" (quando há link). Passa a ter também o botão **"Copiar link"**.
- Clique em "Copiar link" com o app aberto (o caso normal: ele vive na bandeja): cria os links da leva daquela notificação, copia e mostra uma notificação curta e **silenciosa** "Link copiado" (corpo: nome ou "N arquivos"). Falhou: notificação "Não foi possível criar o link" com o motivo.
- O app guarda na memória só as últimas 20 levas. Se o app foi fechado desde o envio (ou a leva é mais antiga), o clique só abre o widget, como o "abrir" de hoje.
- O app não toma o foco nem mostra o widget ao copiar pela notificação.

### Textos novos (`Resources.resw`)

| Chave | Texto |
|---|---|
| `CopyLink` (estado inicial do link; o texto muda, então vem do ViewModel, não de `x:Uid`) | Copiar link |
| `CopyLinkCopying` | Copiando… |
| `CopyLinkCopied` | Link copiado |
| `CopyLinkFailed` | Não foi possível criar o link |
| `CopyLinkReconnectFormat` | Conecte o {0} de novo para copiar links |
| `NotificationCopyLink` (botão) | Copiar link |
| `NotificationLinkCopied` (título) | Link copiado |
| `NotificationLinkFailed` (título) | Não foi possível criar o link |

## Como funciona

### `IStorageProvider`

```csharp
/// <summary>A file the app uploaded: what is needed to open it and to share it later.</summary>
public sealed record UploadedItem(string Id, string Name, string WebUrl);

Task<UploadedItem> UploadAsync(StorageFile file, string targetFolder, IProgress<double> progress, CancellationToken ct);

/// <summary>Makes the file readable by anyone with the link and returns that link.</summary>
Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct);
```

`WebUrl` é o link do dono que o "Abrir" já usa. Falhas de `CreateShareLinkAsync` saem como `StorageProviderException`, com um tipo novo em `StorageErrorKind`: `MissingPermission` (a sessão salva não tem a permissão nova; só o Dropbox usa).

### Google Drive

- `UploadAsync`: `Fields = "id, name, webViewLink"`; devolve `UploadedItem(id, name, webViewLink)`.
- `CreateShareLinkAsync`: `Permissions.Create(new Permission { Type = "anyone", Role = "reader" }, id)` e devolve o `webViewLink` (já serve para quem não tem conta, depois da permissão). Criar a mesma permissão de novo não duplica nada.
- Escopo continua só `drive.file` (o app pode compartilhar os arquivos que ele criou): **nenhuma permissão nova, nenhuma verificação**.

### OneDrive

- `UploadAsync`: lê `id`, `name` e `webUrl` do item criado.
- `CreateShareLinkAsync`: `POST /me/drive/items/{id}/createLink` com `{"type":"view","scope":"anonymous"}`; devolve `link.webUrl`. Se a conta (de trabalho ou escola) recusar links anônimos, tenta `{"type":"view","scope":"organization"}`. O Graph devolve o mesmo link se ele já existe.
- Escopo continua `Files.ReadWrite`.

### Dropbox

- Escopos: `files.content.write`, `account_info.read` e **`sharing.write`** (novo; precisa ser ligado no App Console antes, ver "Antes de testar").
- `UploadAsync`: `UploadedItem(metadata.Id, metadata.Name, link de hoje)`.
- `CreateShareLinkAsync`: `Sharing.CreateSharedLinkWithSettingsAsync(id)`; se o erro for "link já existe", `Sharing.ListSharedLinksAsync(id, directOnly: true)` e usa o primeiro. Devolve a URL.
- Sessão antiga sem `sharing.write` (erro de escopo faltando): `StorageProviderException(MissingPermission)`. Para resolver, o usuário desconecta e conecta de novo (Configurações, ou desliga e liga o switch): o login novo pede os três escopos.

### `ShareLinkService` (novo, `Services/ShareLinkService.cs`)

- `string Register(IStorageProvider provider, IReadOnlyList<UploadedItem> items)`: guarda a leva com um id (GUID `N`) e devolve o id; mantém as 20 mais recentes. O nome do provedor para as notificações vem de `provider.DisplayName`.
- `Task<ShareResult> CopyAsync(IStorageProvider provider, IReadOnlyList<UploadedItem> items, CancellationToken ct)`: chama `CreateShareLinkAsync` para cada item, em ordem, junta com `\r\n` e copia. Resultado: sucesso, ou o tipo do erro.
- `Task<ShareResult?> CopyBatchAsync(string batchId)`: para a notificação; `null` se a leva não está mais na memória.
- Área de transferência pela API Win32 (`OpenClipboard`, `EmptyClipboard`, `SetClipboardData(CF_UNICODETEXT)`) em `Helpers/Win32`, com a janela do widget como dona: funciona com o app em segundo plano, sem depender de foco. Se a área de transferência estiver ocupada por outro app, tenta de novo algumas vezes (50 ms) antes de falhar.

### `ProviderViewModel`

- Guarda os `UploadedItem` da leva enviada (`_uploadedItems`); "Abrir" usa o `WebUrl` do último, como hoje.
- `CopyLinkCommand` (assíncrono): chama `ShareLinkService.CopyAsync` e controla `CopyLinkLabel` ("Copiar link" / "Copiando…" / "Link copiado") e uma mensagem temporária `DoneMessage` que substitui o `DoneLabel` nas falhas. Os tempos (2 s, 3 s, 5 s) usam um `DispatcherQueueTimer`.
- Nova leva começando ou mudança de estado: limpa a mensagem e o "Link copiado"; uma cópia que ainda estava em andamento termina normalmente (os links vão para a área de transferência), mas o resultado dela não aparece na linha, que já mostra a leva nova.
- `UploadFinishedEventArgs` ganha `BatchId` (registrado no `ShareLinkService` ao terminar com sucesso).

### Notificação e ativação

- `NotificationService.ShowUploaded(..., batchId)`: adiciona a ação "Copiar link" com `arguments="boxezone:copy-link?batch={id}"` e `activationType="protocol"`, ao lado do "Abrir".
- `ShowLinkCopied(body)` e `ShowLinkFailed(reason)`: notificações sem som (`<audio silent="true"/>`) que somem da central em 1 minuto.
- O Windows abre `BoxeZone.exe "boxezone:copy-link?batch=…"`; o `Program.Main` já redireciona para a instância aberta. O `App.OnActivatedByAnotherLaunch` passa a receber os argumentos da ativação: `boxezone:copy-link?batch=…` chama `ShareLinkService.CopyBatchAsync` (leva desconhecida: abre o widget); qualquer outra coisa abre o widget, como hoje.
- App fechado: o `OnLaunched` ignora o `copy-link` e abre normalmente (a leva não existe mais).

### Política de privacidade e consoles

- `website/privacidade/`: dizer que, quando o usuário clica em "Copiar link", o app torna o arquivo legível por qualquer pessoa com o link (no próprio provedor) e que o Dropbox pede a permissão `sharing.write` para isso; atualizar a data.
- `app/installer/privacidade.txt`: o resumo não lista permissões; sem mudança.
- Dropbox App Console → Permissions: marcar `sharing.write` e salvar (o usuário faz; o app pede o escopo no login).

## Fora do escopo

- Escolher o tipo de link (só eu / qualquer um), senha ou validade do link.
- Desfazer o compartilhamento pelo app.
- Copiar link de envios antigos: vem com o histórico (parte 3).

## Antes de testar

1. Usuário liga `sharing.write` no App Console do Dropbox (passo a passo na conversa).
2. Branch `feat/share-link` (a partir de `chore/cleanup`).

## Testes

- `dotnet build` sem avisos.
- Google Drive (conectado aqui), automatizado com UI Automation e o arraste pelo Explorer:
  - 1 arquivo: "Copiar link" → "Copiando…" → "Link copiado"; a área de transferência tem um link; baixar esse link sem cookies (`Invoke-WebRequest`) não cai na tela de login do Google.
  - leva de 3: três linhas, três links diferentes, todos abrem sem login.
  - notificação: ler o `batch` do XML da notificação no histórico e executar `BoxeZone.exe "boxezone:copy-link?batch=…"`; a área de transferência recebe os links e aparece a notificação "Link copiado". Com um `batch` desconhecido, o widget abre.
  - painel compacto e os dois temas: prints do estado "Enviado".
  - falha: sem internet não dá para simular com segurança aqui; conferir por leitura do código e no teste do usuário.
- OneDrive e Dropbox: o usuário testa na versão `-rc` (incluindo a mensagem de reconectar no Dropbox, antes de conectar de novo).
