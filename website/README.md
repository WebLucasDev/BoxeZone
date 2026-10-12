# Site do Boxe Zone

Publicado em https://weblucasdev.github.io/BoxeZone/ pelo workflow `.github/workflows/pages.yml` a cada push na `main` que mude esta pasta.

- `index.html`: página do app.
- `privacidade/`: política de privacidade (link exigido pelo Google, Microsoft e Dropbox na tela de login).
- `assets/`: estilo, logo e ícones dos provedores (os mesmos de `app/Assets`).
- `404.html`: usa caminhos absolutos com `/BoxeZone/`, porque é servido em qualquer endereço inexistente.

HTML e CSS puros, sem build. Para ver localmente, abra `index.html` no navegador. A política de privacidade descreve o que o app faz: ao mudar permissões, dados guardados ou o fluxo de login no app, atualizar a política (e a data no topo) antes de publicar a versão.
