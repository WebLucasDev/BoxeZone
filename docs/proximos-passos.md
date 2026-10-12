# BoxeZone: próximos passos

Lista viva do que vem depois da versão 1 (as 8 etapas originais, concluídas). Ao começar um item, mover para "Em andamento"; ao terminar, para "Concluído", com o commit.

## Ideias

Espaço livre para ideias novas, antes de virarem itens das listas abaixo.

-

## Em andamento

- [ ] **Ícones reais dos provedores** no lugar das iniciais (branch `feat/provider-logos`, `adaa1fb`): SVGs oficiais em `Assets/Providers/`, 18 px dentro do quadrado de 28 px, no widget e nas Configurações. Falta o merge na `main`.
- [ ] **Logo nova: luva de boxe** (branch `feat/glove-logo`, `83dd378`) no lugar da seta de envio: `.ico` (exe e bandeja), `.png` (notificações) e cabeçalho do widget. Falta o merge na `main`.
- [ ] **Polimento** (branch `feat/ui-polish`): nome "Boxe Zone" na interface; a aba só recolhe e o widget é arrastado pelo corpo; sempre no topo, sem a opção nas Configurações; aba clicável na largura toda; faixa recolhida volta a estreitar ao tirar o mouse; captura do atalho ignora cliques do mouse; "Cancelar" no login pelas Configurações e limite de 2 min. Falta o merge na `main`.

## Distribuição

- [ ] **Diminuir a versão publicada.** Hoje: ~297 MB em 539 arquivos (`dotnet publish -c Release -p:Platform=x64`). Caminhos: `SatelliteResourceLanguages` só com os idiomas usados; avaliar ReadyToRun (abre mais rápido, ocupa mais); testar `PublishSingleFile` com WinUI.
- [ ] **Instalador** (Inno Setup, WiX ou similar) que instale em `Program Files` e crie atalho no Menu Iniciar com o AppUserModelID `BoxeZone.App`. Com isso dá para registrar um ativador COM e fazer o clique em notificações funcionar com o app fechado.
- [ ] **Desinstalação limpa**: remover a entrada `Run`, a chave `HKCU\Software\Classes\AppUserModelId\BoxeZone.App` e, opcionalmente, `%LocalAppData%\BoxeZone`.
- [ ] **Versão e metadados do `.exe`** (versão, nome do produto, empresa) e assinatura de código, para o SmartScreen não bloquear.
- [ ] **Atualização automática** ou ao menos aviso de versão nova.

## Contas e provedores

- [ ] **Publicar o app OAuth do Google** (sair do modo Teste): tira o aviso "O Google não verificou este app" e a queda do login a cada 7 dias. Exige página inicial e política de privacidade.
- [ ] **Dropbox em produção**: o app em "Development" tem limite de usuários; pedir produção no console quando for distribuir.
- [ ] **Microsoft**: verificação de editor (publisher verification) para tirar o aviso de app não verificado na tela de consentimento.
- [ ] **Revogar no servidor ao desconectar o OneDrive** (hoje o MSAL só apaga a conta do cache local).
- [ ] **Mais provedores** (Box, pCloud, S3, servidor próprio…): basta implementar `IStorageProvider`.

## Envio

- [ ] **Enviar pastas** (hoje pastas soltas são ignoradas): recriar a estrutura no destino.
- [ ] **Cancelar um envio em andamento**: o `CancellationToken` já chega aos provedores; falta o botão na área de drop.
- [ ] **Fila de vários arquivos** com progresso total ("3 de 10") em vez de um arquivo por vez.
- [ ] **Copiar link compartilhável** depois do envio (Dropbox precisa da permissão `sharing.write`; Drive e OneDrive têm APIs de compartilhamento).
- [ ] **Escolher a pasta de destino navegando** (possível no OneDrive e no Dropbox; no Drive o escopo `drive.file` só enxerga pastas criadas pelo app).
- [ ] **Retomar envios grandes** depois de queda de rede ou de fechar o app (as sessões de upload dos três provedores permitem).

## Interface

- [ ] **Brilho na faixa recolhida**, como no protótipo (`box-shadow` azul); a janela nativa hoje só tem a sombra padrão.
- [ ] **Dicas (tooltips)** na aba e na faixa, sem cortar dentro de janelas tão finas.
- [ ] **Inglês** (`Strings/en-US/Resources.resw`) e seleção de idioma.
- [ ] **Histórico de envios** recentes, com "Abrir" e "Copiar link".

## Qualidade

- [ ] **Testes automatizados**: unidade para os ViewModels e provedores (com `IStorageProvider` falso) e um teste de fumaça com UI Automation (ver `docs/arquitetura.md`, seção de testes).
- [ ] **`.gitattributes`** para padronizar fim de linha (hoje o git converte LF/CRLF e avisa a cada commit).
- [ ] **Log de diagnóstico opcional** (sem tokens nem nomes de arquivo), além do `crash.log`.

## Limitações conhecidas

- Clicar numa notificação antiga depois de "Sair" não faz nada (o `AppNotificationManager` não funciona self-contained; ver `docs/arquitetura.md`).
- O drop não funciona se o app rodar como administrador (limitação do Windows).
- Abrir as Configurações e voltar pode mudar o widget de lugar: a tela de Configurações cresce para longe das bordas próximas e, na volta, o widget fica preso ao canto oposto do retângulo maior.
- Ligar "Iniciar com o Windows" num build de desenvolvimento aponta para o `.exe` em `bin\x64\Debug`; o caminho é regravado a cada início com a opção ligada.

## Concluído

Versão 1, em ordem (commits na `main`):

| Etapa | Commit |
|---|---|
| 0. Base: .NET 10, unpackaged, renomeado para BoxeZone | `da79fe4` |
| 1. Janela sem borda, Acrylic, bandeja | `03725d0` |
| 2. Lista de provedores e áreas de drop | `f719778` |
| 3. Normal / Docked / Hidden, aba lateral, painel compacto | `9cdf71e` |
| 4. Hotkey global e instância única | `baaa4fd` |
| 5. Google Drive ponta a ponta (+ correção do "sempre no topo") | `fb00c9e`, `97835b6` |
| 6. OneDrive e Dropbox | `27a840a` |
| 7. Configurações e preferências salvas | `3c188be` |
| 8. Notificações, iniciar com o Windows, animações | `937f643` |
