# Site do Boxe Zone

Publicado em https://weblucasdev.github.io/BoxeZone/ pelo workflow `.github/workflows/pages.yml` a cada push na `main` que mude esta pasta.

- `index.html`: página do app.
- `privacidade/`: política de privacidade (link exigido pelo Google, Microsoft e Dropbox na tela de login; também explica o login do MEGA).
- `assets/`: estilo, logo e ícones dos provedores (os mesmos de `app/Assets`).
- `404.html`: usa caminhos absolutos com `/BoxeZone/`, porque é servido em qualquer endereço inexistente.

HTML e CSS puros, sem build. As demonstrações (widget, avisos do instalador e as ilustrações de "Como funciona" e "Detalhes") são HTML e CSS que imitam o app, com um script curto no fim do `index.html` que as inicia quando aparecem na tela; com "reduzir animações" ligado no sistema, cada uma fica parada no quadro principal. Textos que imitam o app usam os mesmos do `app/Strings/`. Links para fora do site abrem em outra aba (`target="_blank" rel="noopener"`), menos os de download do instalador. Para ver localmente, abra `index.html` no navegador. A política de privacidade descreve o que o app faz: ao mudar permissões, dados guardados ou o fluxo de login no app, atualizar a política (e a data no topo) antes de publicar a versão.
