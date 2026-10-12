# BoxeZone: próximos passos

Lista viva do que vem depois da versão 1 (as 8 etapas originais, concluídas). Ao começar um item, mover para "Em andamento"; ao terminar, para "Concluído", com o commit.

## Ideias

Espaço livre para ideias novas, antes de virarem itens das listas abaixo.

-

## Em andamento

Nada no momento.

## Distribuição

- [ ] **Assinatura de código**, para o SmartScreen não avisar ao instalar. Gratuita pela SignPath Foundation, que exige uma licença de código aberto no repositório; a alternativa paga é um certificado (Azure Trusted Signing ou tradicional).
- [ ] **Imagem do assistente do instalador** com a luva (hoje é a imagem genérica do Inno Setup).
- [ ] **Atualização automática** (hoje o app só avisa a versão nova; instalar é baixar e rodar o instalador por cima).
- [ ] **Pacote ainda menor**: o maior arquivo é `Microsoft.Windows.SDK.NET.dll` (52,8 MB), que só encolhe com trimming, desligado por causa da reflexão dos SDKs dos provedores. Avaliar `PublishSingleFile` com WinUI.

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

Depois da versão 1:

| Item | Commit |
|---|---|
| Ícones oficiais dos provedores (SVG em `Assets/Providers/`) no lugar das iniciais | `adaa1fb` |
| Logo nova: luva de boxe (`.ico`, `.png` e cabeçalho) | `83dd378` |
| Polimento: nome "Boxe Zone", aba só recolhe e arrasto pelo corpo, sempre no topo, aba clicável na largura toda, faixa volta a estreitar, captura do atalho ignora o mouse, "Cancelar" no login e limite de 2 min | `8ea6048` |
| Pacote menor (297 → 239 MB, 542 → 477 arquivos) e versão/autor no `.exe` | `3b30f4f` |
| Clique na notificação com o app fechado (protocolo `boxezone:`) | `f4a4652` |
| Aviso de versão nova (GitHub Releases) e linha "Versão" nas Configurações | `b1d2a66` |
| Instalador Inno Setup x64 e ARM64 (61,5 e 56,6 MB), sem administrador, desinstalação limpa | `3f3a796` |
| Releases pelo GitHub Actions a partir de tags `v*` | `9e8acdf` |
