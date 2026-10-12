# Instalador e primeira versão pública: design

Data: 12/10/2026. Branch: `feat/installer`. Aprovado em conversa; este documento registra o que foi decidido.

## Objetivo

Qualquer pessoa consegue baixar o Boxe Zone no site, instalar com dois cliques, receber aviso de versão nova e desinstalar sem deixar rastro. Cada versão sai do GitHub de forma reproduzível, sem depender da máquina do desenvolvedor.

Fora do escopo: assinatura de código (SmartScreen continua avisando; próximo passo, possivelmente pela SignPath, que exige licença de código aberto), atualização automática silenciosa e Microsoft Store/MSIX.

## Decisões

| Tema | Decisão | Motivo |
|---|---|---|
| Ferramenta | Inno Setup 6 | Gratuito, `Setup.exe` clássico, controle do atalho e da desinstalação, nada muda no código do app |
| Onde instala | Só para o usuário: `%LocalAppData%\Programs\Boxe Zone`, sem administrador | O app já guarda tudo por usuário (preferências, tokens, `Run` em HKCU) |
| Arquiteturas | x64 e ARM64, um instalador para cada | ARM64 roda nativo em PCs Snapdragon; o x64 continua servindo nos dois |
| Clique na notificação | Ativação por protocolo, não ativador COM | O shell abre o link sozinho com o app fechado; sem servidor COM nem CLSID |
| Versão nova | Consulta à última Release do GitHub; aviso por notificação e nas Configurações | Sem servidor próprio; instalar por cima já atualiza |
| Publicação | GitHub Actions disparado por tag `v*` | Reproduzível; credenciais num Secret |

## 1. Pacote menor e versão

- Hoje: 297 MB em 542 arquivos (`dotnet publish` Release x64). Peso morto identificado: componentes de IA/ML do Windows App SDK (`onnxruntime.dll` 20,7 MB, `DirectML.dll` 17,8 MB e afins), que o app não usa.
- Trocar `Microsoft.WindowsAppSDK` (metapacote) por `Microsoft.WindowsAppSDK.WinUI`, que traz Foundation, Base e InteractiveExperiences como dependências. Ficam de fora AI, ML, Search, Widgets (e DWrite/Runtime se o app não precisar deles; conferir no build). Se faltar algo em tempo de execução, acrescentar só o componente que faltar.
- Idiomas: `SatelliteResourceLanguages=pt-BR` corta as pastas de idiomas das bibliotecas .NET (ganho pequeno, ~3 MB).
- Mantidos: self-contained, `PublishReadyToRun` no Release (abre mais rápido), `PublishTrimmed=false` (os SDKs dos provedores usam reflexão).
- Metadados no `.csproj`: `Version` (1.0.0; o workflow sobrescreve pela tag), `Product` "Boxe Zone", `Company`/`Authors` "Lucas Venancio", `Copyright`, `AssemblyTitle`. O `.exe` mostra nome e versão nas propriedades, e o app lê a própria versão para comparar com a Release.
- Critério de aceite: o app abre, conecta, envia e notifica depois da troca; tamanho medido antes e depois.

## 2. Instalador (`app/installer/BoxeZone.iss`)

- `AppId` fixo (GUID) para que versões novas instalem por cima; `PrivilegesRequired=lowest`; `DefaultDirName={localappdata}\Programs\Boxe Zone`; sem página de escolha de pasta nem de grupo do Menu Iniciar.
- Entrada: a pasta `publish` da arquitetura; versão e arquitetura passadas por `/D` na linha de comando (`ISCC /DAppVersion=1.0.0 /DArch=x64`).
- Saída com nome fixo: `BoxeZone-Setup-x64.exe` e `BoxeZone-Setup-arm64.exe`, para o link "última versão" do site nunca mudar.
- `ArchitecturesAllowed`/`ArchitecturesInstallIn64BitMode`: `x64compatible` no x64 e `arm64` no ARM64.
- Atalho no Menu Iniciar "Boxe Zone" com `AppUserModelID: BoxeZone.App` (o mesmo das notificações). Atalho na área de trabalho: opcional, desmarcado.
- Idioma do assistente: português do Brasil. Ícone: `Assets\BoxeZone.ico`. Ao terminar, opção "Abrir o Boxe Zone" marcada.
- App aberto durante a instalação ou desinstalação: o instalador fecha o processo antes de copiar os arquivos (o app é de bandeja e cancela o fechamento pela janela; preferências são salvas a cada mudança, então fechar à força não perde nada).
- Desinstalação: remove arquivos e atalho; apaga `HKCU\...\Run\BoxeZone`, `HKCU\Software\Classes\AppUserModelId\BoxeZone.App` e `HKCU\Software\Classes\boxezone`; pergunta "Apagar também as preferências e as contas conectadas?" e, se sim, apaga `%LocalAppData%\BoxeZone`.
- O `appsettings.local.json` vai no pacote (o app precisa dele para o login). São credenciais de cliente público (Google desktop, MSAL, Dropbox PKCE), que os provedores não tratam como secretas.

## 3. Clique na notificação com o app fechado

- Notificação "Enviado" com link: `<toast activationType="protocol" launch="{link}">` e o botão "Abrir" com `activationType="protocol" arguments="{link}"`. O shell abre o link do arquivo no navegador, com ou sem o app rodando.
- Notificações sem link (falha) e o aviso de versão nova: protocolo próprio. O app registra `HKCU\Software\Classes\boxezone` (`URL Protocol`, `shell\open\command` = `"{exe}" "%1"`) ao iniciar, como já faz com o AUMID, e o desinstalador remove.
  - `boxezone:open` abre o widget. Chega ao app como argumento de linha de comando; com o app já aberto, o redirecionamento de instância única existente leva a ativação à instância que roda (`App.OnActivatedByAnotherLaunch`).
  - O aviso de versão nova usa o link da página da Release diretamente (https), sem passar pelo app.
- O handler `toast.Activated` deixa de ser necessário para links; continua inofensivo.
- A conferir na implementação: ativação por protocolo em app sem pacote com AUMID registrado no HKCU (esperado que funcione, já que quem abre é o shell).

## 4. Aviso de versão nova

- Ao iniciar (e no máximo uma vez a cada 24 h, guardado em `settings.json`), `UpdateService` faz `GET https://api.github.com/repos/WebLucasDev/BoxeZone/releases/latest` (sem autenticação, `User-Agent` "BoxeZone", timeout curto). Lê `tag_name` (`v1.2.0`) e `html_url`.
- Se a versão da tag for maior que a do app: uma notificação "Versão nova do Boxe Zone" uma única vez por versão (versão avisada guardada em `settings.json`), com clique levando a `html_url`; e uma linha nas Configurações, seção "Atalho e aparência" ou nova seção "Sobre", com a versão atual e "Baixar a versão X" (link). Sem nova versão: a linha mostra só "Versão 1.0.0".
- Falha de rede, limite da API ou resposta inesperada: silêncio, tenta de novo no próximo início.
- Strings no `.resw`. Visual: seguir o padrão de linhas das Configurações (`docs/configuracoes.dc.html`); o protótipo não tem essa linha, então ela é desenhada com os mesmos estilos e registrada no protótipo.
- Política de privacidade: acrescentar que o app consulta o GitHub para saber se há versão nova (o GitHub recebe o pedido comum, com o IP; nenhum dado do usuário é enviado) e atualizar a data.

## 5. Publicação (`.github/workflows/release.yml`)

- Disparo: push de tag `v*.*.*`. Runner `windows-latest`.
- Passos: checkout; .NET 10; gravar `app/appsettings.local.json` a partir do Secret `APPSETTINGS_LOCAL_JSON`; `dotnet publish` Release com `-p:Version=` da tag para `x64` e `ARM64`; instalar Inno Setup se o runner não tiver; `ISCC` para as duas arquiteturas; criar a Release da tag com os dois instaladores (`gh release create`, já disponível no runner).
- Sem o Secret, o workflow falha cedo com mensagem clara (um instalador sem credenciais não conectaria a nada).
- Configuração única do usuário: em BoxeZone → Settings → Secrets and variables → Actions, criar `APPSETTINGS_LOCAL_JSON` com o conteúdo do `appsettings.local.json`.
- Para testar localmente: script `app/installer/build.ps1 [-Arch x64|arm64] [-Version 1.0.0]` que publica e roda o ISCC, com saída em `app/installer/output/` (git-ignored).

## 6. Site

- Botão "Baixar para Windows" → `https://github.com/WebLucasDev/BoxeZone/releases/latest/download/BoxeZone-Setup-x64.exe`; ao lado, link menor "Versão para ARM64". Texto curto sobre o aviso do SmartScreen ("Mais informações → Executar assim mesmo") enquanto não houver assinatura.
- A troca só vai para a `main` junto com (ou depois de) a primeira Release, para o botão nunca apontar para um arquivo inexistente.

## Ordem de implementação

1. Pacote menor e versão (medir; testar o app).
2. Protocolo `boxezone:` e notificações por protocolo (testar com o app fechado).
3. `UpdateService`, linha nas Configurações, política de privacidade.
4. Inno Setup local: script `.iss` e `build.ps1`; instalar, atualizar por cima e desinstalar numa conta de usuário.
5. Workflow de Release; primeira tag de teste (`v1.0.0-rc.1`, marcada como pré-lançamento) depois de o usuário cadastrar o Secret.
6. Site com o botão de download; `docs/arquitetura.md` e `docs/proximos-passos.md` atualizados.

## Riscos

- Componentes do Windows App SDK: algum recurso pode depender de um componente removido (falha só em tempo de execução). Mitigação: testar janela, Acrylic, bandeja, notificações e upload depois da troca.
- SmartScreen: instalador sem assinatura mostra aviso; mitigação parcial pelo texto no site, solução pela assinatura (fora do escopo).
- API do GitHub sem autenticação: 60 pedidos por hora por IP; uma consulta por dia por usuário não chega perto.
