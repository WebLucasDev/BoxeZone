# Boxe Zone: próximos passos

Lista viva do que vem depois da versão pública 1.0.0 (publicada em 12/10/2026: instaladores nas Releases do GitHub, download em https://weblucasdev.github.io/BoxeZone/). Ao começar um item, mover para "Em andamento"; ao terminar, para "Concluído", com o commit. Itens grandes ganham design e plano temporários em `app/docs/specs/` e `app/docs/plans/`, apagados ao concluir (o design e o plano do instalador e da 1.0.0 estão no histórico do git, commit `9dc1b82`).

## Ideias

Espaço livre para ideias novas, antes de virarem itens das listas abaixo.

-

## Em andamento

- [ ] **Rodada de funções (decidida em 13/10/2026).** Copiar link compartilhável: feito (v1.2.0). O resto em 3 rodadas, cada uma com design, plano e uma versão de teste: **1. envio** (enviar pastas + retomar envios grandes; em andamento, design em `app/docs/specs/2026-10-13-pastas-e-retomar-design.md`); **2. telas novas** (histórico de envios + escolher a pasta de destino navegando); **3. acabamento** (inglês e seleção de idioma + atualização automática, que confere o SHA256 publicado junto da versão antes de rodar o instalador baixado). Os itens estão descritos nas seções abaixo.

## Distribuição

- [ ] **Assinatura de código**: o programa gratuito da SignPath Foundation **recusou** o pedido em 13/10/2026 (o projeto ainda não tem sinais públicos suficientes: estrelas, forks, menções, artigos); dá para pedir de novo quando o projeto ganhar visibilidade. Decisão do usuário: seguir sem assinatura por enquanto. Alternativas pagas, se um dia valer a pena: certificado de código aberto da Certum (barato, pessoa física), SignPath paga ou certificado comercial (caros). Nenhum tira o aviso do SmartScreen na hora: a reputação vem com os downloads, mas com certificado ela passa de uma versão para a outra. O que ficou da candidatura (licença MIT, opção "Verificar versões novas", resumo de privacidade no instalador) continua valendo.
- [ ] **Atualização automática** (hoje o app só avisa a versão nova; instalar é baixar e rodar o instalador por cima). Sem assinatura, a conferência do instalador baixado é pelo SHA256 publicado na Release.
- [ ] **Pacote ainda menor**: o maior arquivo é `Microsoft.Windows.SDK.NET.dll` (52,8 MB), que só encolhe com trimming, desligado por causa da reflexão dos SDKs dos provedores. Avaliar `PublishSingleFile` com WinUI.

## Contas e provedores

- [ ] **Dropbox em produção**: em "Development" o app aceita até 50 contas sem fazer nada. Ao chegar a 50 contas conectadas, há **duas semanas** para pedir e receber a aprovação (App Console → Settings → "Apply for production"); depois disso, contas novas são recusadas até aprovar. Nome, site, descrição e ícone já estão na aba Branding.
- [ ] **Google: verificação de marca** (opcional), só se quiser mostrar o logo na tela de consentimento. Exige domínio próprio verificado; sem logo, não é necessária.
- [ ] **Microsoft**: verificação de editor (publisher verification) para tirar o aviso de editor não verificado na tela de consentimento. Exige conta no programa de parceiros da Microsoft com domínio próprio.
- [ ] **Revogar no servidor ao desconectar o OneDrive** (hoje o MSAL só apaga a conta do cache local).
- [ ] **Mais provedores** (Box, pCloud, S3, servidor próprio…): basta implementar `IStorageProvider`.

## Envio

- [ ] **Enviar pastas** (hoje pastas soltas são ignoradas): recriar a estrutura no destino.
- [ ] **Escolher a pasta de destino navegando** (possível no OneDrive e no Dropbox; no Drive o escopo `drive.file` só enxerga pastas criadas pelo app).
- [ ] **Retomar envios grandes** depois de queda de rede ou de fechar o app (as sessões de upload dos três provedores permitem).

## Interface

- [ ] **Brilho na faixa recolhida**, como no protótipo (`box-shadow` azul); a janela nativa hoje só tem a sombra padrão.
- [ ] **Dicas (tooltips)** na aba e na faixa, sem cortar dentro de janelas tão finas.
- [ ] **Inglês** (`Strings/en-US/Resources.resw`) e seleção de idioma.
- [ ] **Histórico de envios** recentes, com "Abrir" e "Copiar link".

## Qualidade

- [ ] **Testes automatizados**: unidade para os ViewModels, `UpdateService.TryParseTag` e provedores (com `IStorageProvider` falso) e um teste de fumaça com UI Automation (ver `app/docs/arquitetura.md`, seção de testes). Exige aprovar um pacote de testes (xUnit ou similar).
- [ ] **Log de diagnóstico opcional** (sem tokens nem nomes de arquivo), além do `crash.log`.

## Limitações conhecidas

- O drop não funciona se o app rodar como administrador (limitação do Windows).
- Abrir as Configurações e voltar pode mudar o widget de lugar: a tela de Configurações cresce para longe das bordas próximas e, na volta, o widget fica preso ao canto oposto do retângulo maior.
- Ligar "Iniciar com o Windows" num build de desenvolvimento aponta para o `.exe` em `bin\x64\Debug`; o caminho é regravado a cada início com a opção ligada. O mesmo vale para o protocolo `boxezone:`, regravado a cada início pelo `.exe` que rodou por último.
- Cópia instalada e cópia de desenvolvimento rodam ao mesmo tempo (a instância única vale por `.exe`), com dois ícones na bandeja. Fechar uma antes de testar a outra.
- O instalador não é assinado: o SmartScreen mostra "O Windows protegeu o computador" (o site explica "Mais informações → Executar assim mesmo"). Ver "Assinatura de código" em Distribuição.
- Versões de teste (`-rc.N`) mostram só "1.0.0" na linha "Versão" das Configurações, porque a versão do assembly é numérica.

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
| Menu da bandeja ("Abrir" e "Sair") funcionando | `2f76e1c` |
| Site e política de privacidade em `website/` (GitHub Pages), repositório dividido em `app/` e `website/` | `d152ca8` |
| Google OAuth publicado ("Em produção", sem verificação: o único escopo, `drive.file`, não é sensível); Microsoft e Dropbox com site e política cadastrados | configuração nos consoles |
| **Versão 1.0.0 publicada** (instaladores x64 e ARM64 nas Releases do GitHub, botão de download no site) | tag `v1.0.0` |
| Código aberto (MIT), README, "Code signing policy" no site, resumo de privacidade no instalador e opção "Verificar versões novas" | `e572c02`, tag `v1.0.1` |
| "Cancelar" um envio em andamento (para o arquivo atual e o resto do lote, sem notificação) | `aa5872c` |
| Leva de vários arquivos com progresso total ("3 de 10", barra pelo tamanho; soltar durante um envio entra na leva) | `e11573d` |
| Painel compacto vazio ("Ative um destino") quando o app abre recolhido | `0d8116a` |
| **Versão 1.1.0 publicada** ("Cancelar", leva de vários arquivos, correção do painel compacto; testada antes como `v1.1.0-rc.1`) | tag `v1.1.0` |
| Arrumação: imagens do instalador com a luva, Inno Setup fixo no CI, `publish` limpo, `.gitattributes`, ajustes da revisão da 1.0.0 | `58c5b7f` |
| "Copiar link": link "qualquer um com o link" no clique, um por linha numa leva, na área de drop e na notificação (design e plano no histórico do git, commit `749749e`) | `0cbe542`, `646df8f`, `5171528`, `e55cff6` |
| **Versão 1.2.0 publicada** (arrumação + "Copiar link"; testada antes como `v1.2.0-rc.1`, a primeira com o Inno Setup fixo no CI) | tag `v1.2.0` |
