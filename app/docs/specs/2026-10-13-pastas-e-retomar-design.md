# Enviar pastas e retomar envios: design

Rodada 1 de 3 das funções decididas em 13/10/2026 (ver `app/docs/proximos-passos.md`): **enviar pastas** e **retomar envios grandes**, juntas porque as duas mexem no envio. Documento temporário: ao concluir, o que importa vai para `arquitetura.md` e este arquivo é apagado.

## Decisões do usuário (13/10/2026)

- Pasta solta chega **inteira**: a pasta e as subpastas são recriadas no destino.
- App fechado (ou PC desligado) no meio de um envio: ao abrir, **continua sozinho**, sem perguntar.
- Internet caiu com o app aberto: **espera sem limite** e continua quando a conexão voltar.

## O que o usuário vê

### Pastas

- Soltar "Fotos da viagem" cria `{destino}/Fotos da viagem/` com as mesmas subpastas e arquivos.
- Já existe uma pasta com esse nome no destino: cria `Fotos da viagem (1)` (depois `(2)`…), como já acontece com arquivos. Um envio nunca se mistura com outro.
- Ficam de fora: arquivos ocultos ou de sistema (atributos `Hidden` ou `System`, como `desktop.ini` e `Thumbs.db`), pastas que são atalhos (pontos de nova análise, que podem formar ciclos) e pastas vazias (não são criadas no destino). Uma pasta solta sem nenhum arquivo enviável é ignorada, como hoje.
- Pode-se soltar pastas e arquivos juntos; tudo vira uma leva só.
- O contador "3 de 120" conta arquivos (os de dentro das pastas). A barra continua pesada pelo tamanho.
- Fim da leva:
  - só uma pasta solta (e nada mais): **"Enviado · Fotos da viagem"**;
  - qualquer outra combinação com 2 ou mais arquivos: "Enviado · N arquivos" (como hoje);
  - um arquivo: "Enviado · nome" (como hoje).
  - A notificação usa o mesmo resumo no corpo.
- "Abrir": com uma pasta solta, abre a pasta no site do provedor; nos outros casos, como hoje (último arquivo).
- "Copiar link": cada **pasta solta** vira **um link da pasta** (qualquer um com o link vê tudo que está dentro, que é só o desta leva, por causa do "(1)"); cada arquivo solto continua com o seu link. Um por linha, na ordem em que foram soltos.

### Retomar

- **Sem internet** no meio do envio: no lugar da porcentagem aparece **"Aguardando conexão…"**, a barra fica parada e o "Cancelar" continua funcionando. Quando a conexão volta, continua do ponto onde parou, sem limite de tempo de espera.
- **App fechado ou PC desligado** no meio do envio: ao abrir (e com o provedor conectado e ligado), a área de drop mostra **"Retomando…"** enquanto prepara e depois o progresso normal, continuando do ponto onde parou. Sem pergunta e sem notificação extra; a notificação de concluído sai normalmente no fim.
- Um arquivo **mudou** (tamanho ou data de modificação diferentes) desde o início do envio dele: esse arquivo recomeça do zero, sem aviso.
- Um arquivo **sumiu** (apagado ou movido): falha normal, "Não foi possível enviar · nome" com "Tentar de novo" (que segue com os arquivos seguintes da leva, como hoje).
- O "endereço" do envio expirou (envio parado por dias; o do Google dura 1 semana) ou o provedor não reconhece mais a sessão: o arquivo recomeça do zero, sem aviso.
- "Cancelar" e "Desconectar" apagam a fila salva.

### Textos novos (`Resources.resw`)

| Chave | Texto |
|---|---|
| `UploadWaitingForNetwork` | Aguardando conexão… |
| `UploadResuming` | Retomando… |
| `ErrorFileMissing` (motivo da falha quando o arquivo sumiu) | Arquivo não encontrado |

## Como funciona

### Contrato dos provedores (`IStorageProvider`)

```csharp
/// <summary>A file to upload. ResumeState is the provider's own data to continue an
/// interrupted upload (null: start from zero).</summary>
public sealed record UploadJob(string LocalPath, string FileName, string TargetFolder, long Size, DateTimeOffset LastWriteTime, string? ResumeState);

/// <summary>Uploads job.LocalPath into job.TargetFolder (created if missing). Calls
/// saveResumeState whenever the upload can be continued from a new point (after each chunk).</summary>
Task<UploadedItem> UploadAsync(UploadJob job, IProgress<double> progress, Action<string> saveResumeState, CancellationToken ct);

/// <summary>Creates a folder named <paramref name="name"/> inside <paramref name="parentFolder"/>,
/// renaming it ("Nome (1)") when the name is taken. The returned item carries the real
/// name; its Id and WebUrl point to the folder (for "Abrir" and "Copiar link").</summary>
Task<UploadedItem> CreateFolderAsync(string parentFolder, string name, CancellationToken ct);
```

- O arquivo é lido pelo caminho (`FileStream`), não mais pelo `StorageFile`. Itens arrastados sem caminho no disco (arquivos virtuais) ficam de fora, como as pastas hoje.
- `ResumeState` é um texto opaco que só o provedor entende:
  - **Google**: a URI da sessão retomável (evento `UploadSessionData`); para continuar, `ResumeAsync(uri)` com um pedido novo sobre o mesmo arquivo. A biblioteca pergunta ao Google quantos bytes chegaram.
  - **OneDrive**: a `uploadUrl` da sessão; para continuar, `GET uploadUrl` devolve `nextExpectedRanges` e o envio segue desse byte.
  - **Dropbox**: `sessionId` e bytes enviados; se o Dropbox responder `incorrect_offset`, usa o `correct_offset` que vem no erro.
  - Sessão desconhecida, expirada ou recusada (404/410 no OneDrive, `not_found`/`closed` no Dropbox, erro 404 da URI no Google): começa do zero com sessão nova, sem erro para o usuário.
- `CreateFolderAsync`:
  - **Google**: procura pastas com o mesmo nome no pai (como o `GetOrCreateFolderAsync`); se existir, tenta "Nome (1)", "Nome (2)"… e cria a primeira livre. `WebUrl` = `webViewLink` da pasta.
  - **OneDrive**: `POST /me/drive/root:/{pai}:/children` com `{"name", "folder": {}, "@microsoft.graph.conflictBehavior": "rename"}`; o Graph devolve o nome real.
  - **Dropbox**: `Files.CreateFolderV2Async(caminho, autorename: true)`; devolve o nome real. `WebUrl` = `https://www.dropbox.com/home{caminho}`.
- `CreateShareLinkAsync` já funciona com pastas nos três (Google: permissão `anyone` na pasta criada pelo app, dentro do `drive.file`; OneDrive: `createLink` no item da pasta; Dropbox: link da pasta).

### Leva e fila (`ProviderViewModel` + `UploadQueueStore`)

- Ao soltar: arquivos viram jobs; pastas são percorridas (`Directory.EnumerateFiles`/`EnumerateDirectories` recursivo, pulando ocultos, de sistema e pontos de nova análise). A pasta solta é criada no destino **antes** dos arquivos dela (`CreateFolderAsync`, com o nome real devolvido) e os jobs dela apontam para `{destino}/{nome real}/{subcaminho}`; as subpastas são criadas pelos próprios envios (os três provedores já criam o que falta no caminho).
- Pastas soltas durante um envio entram na leva como os arquivos (contador cresce).
- **`UploadQueueStore`** (novo, `Services/UploadQueueStore.cs`): guarda a leva em andamento de cada provedor no `TokenStore` (chave `upload-queue-{providerId}`, JSON criptografado): os jobs com o `ResumeState` atual, quais já terminaram, os `UploadedItem` dos que terminaram e das pastas criadas, e o que define o resumo final (pasta única ou não). Gravado ao montar a leva, a cada `saveResumeState` (no máximo uma vez a cada 2 s por arquivo, e sempre ao terminar um arquivo) e apagado ao terminar, cancelar ou desconectar.
- Ao iniciar: depois de restaurar as sessões, cada provedor ligado e conectado com fila salva retoma sozinho ("Retomando…" até o primeiro progresso). Provedor desligado ou desconectado: a fila espera; desconectar apaga.
- **Sem internet**: um erro `StorageErrorKind.Offline` durante o envio não vira falha. O estado fica `Uploading`, com `IsWaitingForNetwork = true` (o rótulo da porcentagem mostra "Aguardando conexão…"), e o envio é tentado de novo, com o mesmo `ResumeState`, quando o Windows avisar que a rede mudou (`NetworkInformation.NetworkStatusChanged`) ou a cada 15 s, 30 s e depois 60 s, sem limite. "Cancelar" interrompe a espera.

### Privacidade

- `website/privacidade/`, "O que fica no seu computador": novo item **"Envios em andamento"**: nomes e caminhos dos arquivos de um envio que ainda não terminou e os endereços de envio dos serviços, criptografados pelo Windows como as autorizações de acesso, para continuar o envio se o app fechar; apagados quando o envio termina, é cancelado ou a conta é desconectada. Atualizar a data.
- `app/installer/privacidade.txt`: o resumo não detalha o que fica no computador além das contas; acrescentar "e o andamento de envios não terminados" à linha das contas.

## Fora do escopo

- Escolher entre "manter pastas" e "só os arquivos".
- Recriar pastas vazias.
- Retomar arquivos sem caminho no disco (virtuais).
- Retomar no painel compacto de forma diferente do widget (o mesmo controle mostra os mesmos textos).

## Testes

- `dotnet build` sem avisos a cada tarefa.
- Google Drive (conectado aqui), com UI Automation e o arraste pelo Explorer:
  - pasta com 2 níveis de subpastas, um arquivo oculto e um `desktop.ini`: no Drive, a estrutura igual, sem os ocultos; contador "x de N" com N = arquivos visíveis; "Enviado · nome da pasta".
  - a mesma pasta de novo: chega como "nome (1)".
  - "Copiar link" de uma pasta: um link; sem login, abre (200).
  - pasta e arquivo soltos juntos: 2 links.
  - **fechar no meio**: arquivo de 300 MB com conteúdo aleatório; encerrar o app a ~40%; reabrir; o progresso volta perto de 40% (não do zero) e, no fim, o tamanho no Drive é igual ao local.
  - **sem internet**: apontar o proxy do usuário (`HKCU\...\Internet Settings`, `ProxyEnable`/`ProxyServer`) para um endereço morto durante o envio e tirar depois; esperado "Aguardando conexão…" e continuação. Se o .NET não enxergar a troca de proxy com o app aberto, esse teste fica para o usuário (desligar o Wi-Fi no meio de um envio).
- OneDrive e Dropbox: o usuário testa na `-rc` (pasta, "(1)", fechar no meio, Wi-Fi desligado no meio).
