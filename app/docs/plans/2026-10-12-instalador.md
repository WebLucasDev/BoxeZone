# Instalador e primeira versão pública: plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Boxe Zone baixável pelo site, instalável sem administrador (x64 e ARM64), com clique na notificação funcionando com o app fechado, aviso de versão nova e Releases geradas pelo GitHub Actions.

**Architecture:** O app encolhe trocando o metapacote do Windows App SDK pelos componentes que usa. Notificações passam a usar ativação por protocolo (`https` para links, `boxezone:open` para abrir o widget), registrado pelo próprio app no HKCU. Um `UpdateService` consulta a última Release do GitHub uma vez por dia. Um script Inno Setup (`app/installer/`) empacota a pasta `publish`, e um workflow de tag gera os instaladores e a Release.

**Tech Stack:** C# / .NET 10, WinUI 3 (Windows App SDK 2.x, unpackaged, self-contained), Inno Setup 6.3+, GitHub Actions (`windows-latest`), PowerShell.

**Spec:** `app/docs/specs/2026-10-12-instalador-design.md`

## Global Constraints

- Branch `feat/installer`; merge e push na `main` só depois de o usuário testar.
- `dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64` sem avisos ao fim de cada tarefa de código.
- Pacotes NuGet: só os permitidos no `CLAUDE.md`, mais os componentes `Microsoft.WindowsAppSDK.WinUI` 2.3.9 e `Microsoft.WindowsAppSDK.Foundation` 2.3.12 (aprovados com o design). Nenhum outro.
- Strings de UI em `app/Strings/pt-BR/Resources.resw`, nunca fixas. Código, nomes, comentários e commits em inglês.
- AppUserModelID `BoxeZone.App`; protocolo `boxezone`; pasta de dados `%LocalAppData%\BoxeZone`; chave Run `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\BoxeZone`.
- Instalação em `%LocalAppData%\Programs\Boxe Zone`, sem administrador. Instaladores com nome fixo: `BoxeZone-Setup-x64.exe`, `BoxeZone-Setup-arm64.exe`.
- Credenciais (`appsettings.local.json`) nunca no git; no CI vêm do Secret `APPSETTINGS_LOCAL_JSON`.
- Antes de compilar: fechar o BoxeZone que estiver aberto (o `.exe` em uso trava o build).
- O repositório não tem projeto de testes (ver `docs/arquitetura.md`, "Testes manuais automatizados"); a verificação é por build, scripts PowerShell e o app rodando.

## Review Focus

- Release sem `tag_name` válido, tag com sufixo (`v1.2.0-rc.1`) ou repositório sem nenhuma Release (API responde 404): o app não mostra aviso nem quebra; 404 conta como "sem versão nova". → Tarefa 3, passo 4.
- Sem internet ao iniciar: nenhum aviso, nenhum crash, e a próxima abertura tenta de novo (o horário da última consulta não é gravado em falha de rede). → Tarefa 3, passo 4.
- App aberto durante a instalação, atualização ou desinstalação: o instalador fecha o processo e conclui sem erro de arquivo em uso. → Tarefa 4, passo 6.
- Desinstalar sem apagar dados e reinstalar: contas e preferências continuam lá; desinstalar apagando: `%LocalAppData%\BoxeZone` some, e as chaves de registro também. → Tarefa 4, passo 7.
- Notificação clicada com o app fechado, depois de "Sair": o link abre no navegador; a de falha abre o app. → Tarefa 2, passo 5.

---

### Task 1: Pacote menor e versão no `.exe`

**Files:**
- Modify: `app/BoxeZone.csproj`

**Interfaces:**
- Produces: versão do assembly `1.0.0.0` (lida por `UpdateService.CurrentVersion` na Tarefa 3); propriedade MSBuild `Version` sobrescrita por `-p:Version=` no `build.ps1` (Tarefa 4).

- [ ] **Step 1: Medir o tamanho atual (referência)**

Run (PowerShell, na raiz):
```powershell
dotnet publish app/BoxeZone.csproj -c Release -p:Platform=x64
$d = "app\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
$f = Get-ChildItem -Recurse -File $d; "{0:N1} MB, {1} arquivos" -f (($f | Measure-Object Length -Sum).Sum/1MB), $f.Count
```
Expected: ~296,9 MB, ~542 arquivos (medido em 12/10/2026).

- [ ] **Step 2: Trocar o metapacote pelos componentes**

Em `app/BoxeZone.csproj`, substituir:
```xml
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.5.1" />
```
por:
```xml
    <!-- Only the Windows App SDK parts the app uses: WinUI (pulls Base and InteractiveExperiences)
         and Foundation (AppLifecycle, MRT resources, windowing). The full metapackage also ships
         AI/ML/Search/Widgets (~40 MB of onnxruntime, DirectML...). -->
    <PackageReference Include="Microsoft.WindowsAppSDK.WinUI" Version="2.3.9" />
    <PackageReference Include="Microsoft.WindowsAppSDK.Foundation" Version="2.3.12" />
```

- [ ] **Step 3: Versão, metadados e idiomas**

No primeiro `<PropertyGroup>` de `app/BoxeZone.csproj`, depois de `<ApplicationIcon>`:
```xml
    <!-- Shown in the .exe properties and Task Manager; the release workflow overrides Version from the tag. -->
    <Version>1.0.0</Version>
    <Product>Boxe Zone</Product>
    <AssemblyTitle>Boxe Zone</AssemblyTitle>
    <Company>Lucas Venancio</Company>
    <Authors>Lucas Venancio</Authors>
    <Copyright>© 2026 Lucas Venancio</Copyright>
    <!-- The UI is pt-BR only: skip the other languages' satellite assemblies. -->
    <SatelliteResourceLanguages>pt-BR</SatelliteResourceLanguages>
```

- [ ] **Step 4: Compilar**

Run: `dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64`
Expected: `0 Aviso(s)`, `0 Erro(s)`.
Se falhar por falta de um componente (erro citando `Microsoft.WindowsAppSDK.Runtime`, bootstrap ou DWrite), acrescentar só o componente citado, na versão do metapacote 2.5.1: `Microsoft.WindowsAppSDK.Runtime` 2.5.1 ou `Microsoft.WindowsAppSDK.DWrite` 2.1.0, e repetir.

- [ ] **Step 5: Rodar o app e conferir o que depende do SDK**

Run: `dotnet run --project app/BoxeZone.csproj -c Debug --property:Platform=x64`
Conferir: widget abre com Acrylic; ícone na bandeja; Configurações abrem (textos do `.resw` aparecem, não chaves vazias); arrastar um arquivo para um provedor conectado envia e mostra a notificação; abrir o `.exe` de novo traz o widget (instância única, `AppInstance`). Fechar pelo "Sair".

- [ ] **Step 6: Medir de novo e conferir os metadados**

Repetir o Step 1. Expected: ~40 MB e dezenas de arquivos a menos; sem `onnxruntime.dll` e `DirectML.dll`:
```powershell
Test-Path "$d\onnxruntime.dll"; Test-Path "$d\DirectML.dll"   # False, False
(Get-Item "$d\BoxeZone.exe").VersionInfo | Format-List ProductName, FileDescription, ProductVersion, CompanyName
```
Expected: `Boxe Zone`, `Boxe Zone`, `1.0.0+...` (ou `1.0.0`), `Lucas Venancio`. Rodar o `.exe` publicado uma vez (Release) e conferir que abre.

- [ ] **Step 7: Commit**

```bash
git add app/BoxeZone.csproj
git commit -m "build: smaller package and version metadata"
```

---

### Task 2: Notificações por protocolo e `boxezone:open`

**Files:**
- Create: `app/Services/ProtocolService.cs`
- Modify: `app/Services/NotificationService.cs` (classe inteira)
- Modify: `app/App.xaml.cs` (`OnLaunched`)
- Modify: `app/docs/arquitetura.md` (item "Notificações", seção "Integração com o Windows")

**Interfaces:**
- Produces: `ProtocolService.Register()`, `ProtocolService.OpenWidgetUri` (`"boxezone:open"`); `NotificationService()` (sem parâmetros), `ShowUploaded(string providerName, IReadOnlyList<string> fileNames, string? link)`, `ShowFailed(string providerName, string fileName, string reason)`, `ShowUpdateAvailable(string version, string url)` (usado na Tarefa 3).

- [ ] **Step 1: Criar `ProtocolService`**

`app/Services/ProtocolService.cs`:
```csharp
using System;
using Microsoft.Win32;

namespace BoxeZone.Services
{
    /// <summary>
    /// Registers the boxezone: URI scheme under HKCU, so "boxezone:open" (a notification click
    /// with the app closed) launches the .exe. With the app already running, the single-instance
    /// redirect in Program.Main shows the widget. Rewritten on every start, like the Run entry,
    /// so it follows the .exe if it moves; the uninstaller deletes the key.
    /// </summary>
    public static class ProtocolService
    {
        public const string Scheme = "boxezone";
        public const string OpenWidgetUri = Scheme + ":open";

        public static void Register()
        {
            try
            {
                string exe = Environment.ProcessPath!;
                using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
                key.SetValue(string.Empty, "URL:Boxe Zone");
                key.SetValue("URL Protocol", string.Empty);
                using (RegistryKey icon = key.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue(string.Empty, $"\"{exe}\",0");
                }

                using RegistryKey command = key.CreateSubKey(@"shell\open\command");
                command.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
            }
            catch (Exception ex)
            {
                // Only the "open the app" click depends on it; everything else works without.
                CrashLog.Write(ex);
            }
        }
    }
}
```

- [ ] **Step 2: Reescrever `NotificationService` com ativação por protocolo**

Substituir o conteúdo da classe (manter os `using` de `System`, `System.Collections.Generic`, `System.IO`, `System.Security`, `Microsoft.Win32`, `Microsoft.Windows.ApplicationModel.Resources`, `Windows.Data.Xml.Dom`, `Windows.UI.Notifications`; remover `System.Diagnostics` se ficar sem uso):
```csharp
    /// <summary>
    /// Windows notifications ("Notificar ao concluir", new version) through the OS toast API.
    /// </summary>
    /// <remarks>
    /// AppNotificationManager (Windows App SDK) fails to register in a self-contained unpackaged
    /// app (0x8007007E), so the app registers its own AppUserModelID under
    /// HKCU\Software\Classes\AppUserModelId. Clicks use protocol activation: the shell opens the
    /// URI itself, so they work with the app closed (https links open in the browser,
    /// boxezone:open starts the app).
    /// </remarks>
    public sealed class NotificationService
    {
        private const string AppUserModelId = "BoxeZone.App";

        private readonly ResourceLoader _strings = new();
        private ToastNotifier? _notifier;

        public void Register()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
                key.SetValue("DisplayName", _strings.GetString("AppTitle"));
                key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "BoxeZone.png"));
                _notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            }
            catch (Exception ex)
            {
                // Notifications are a nicety; the app works without them.
                CrashLog.Write(ex);
                _notifier = null;
            }
        }

        public void ShowUploaded(string providerName, IReadOnlyList<string> fileNames, string? link)
        {
            string body = fileNames.Count == 1
                ? fileNames[0]
                : string.Format(_strings.GetString("NotificationFilesFormat"), fileNames.Count);

            Show(
                string.Format(_strings.GetString("NotificationUploadedFormat"), providerName),
                body,
                link ?? ProtocolService.OpenWidgetUri,
                link is null ? null : _strings.GetString("NotificationOpen"));
        }

        public void ShowFailed(string providerName, string fileName, string reason) =>
            Show(
                string.Format(_strings.GetString("NotificationFailedFormat"), providerName),
                $"{fileName} · {reason}",
                ProtocolService.OpenWidgetUri,
                null);

        public void ShowUpdateAvailable(string version, string url) =>
            Show(
                _strings.GetString("NotificationUpdateTitle"),
                string.Format(_strings.GetString("NotificationUpdateFormat"), version),
                url,
                _strings.GetString("NotificationUpdateDownload"));

        /// <param name="uri">Opened by a click on the notification (and on the button, if any).</param>
        /// <param name="buttonText">Optional button that opens the same URI.</param>
        private void Show(string title, string body, string uri, string? buttonText)
        {
            if (_notifier is null)
            {
                return;
            }

            string actions = buttonText is null
                ? string.Empty
                : $"<actions><action content=\"{Escape(buttonText)}\" arguments=\"{Escape(uri)}\" activationType=\"protocol\"/></actions>";

            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast activationType=\"protocol\" launch=\"{Escape(uri)}\"><visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text><text>{Escape(body)}</text>" +
                $"</binding></visual>{actions}</toast>");

            _notifier.Show(new ToastNotification(xml));
        }

        private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
    }
```
Strings novas em `app/Strings/pt-BR/Resources.resw` (junto das outras `Notification*`):
```xml
  <data name="NotificationUpdateTitle" xml:space="preserve">
    <value>Versão nova do Boxe Zone</value>
  </data>
  <data name="NotificationUpdateFormat" xml:space="preserve">
    <value>A versão {0} está disponível.</value>
  </data>
  <data name="NotificationUpdateDownload" xml:space="preserve">
    <value>Baixar</value>
  </data>
```

- [ ] **Step 3: Ajustar `App.OnLaunched`**

Em `app/App.xaml.cs`, trocar:
```csharp
            DispatcherQueue dispatcher = _window.DispatcherQueue;
            _notificationService = new NotificationService(() => dispatcher.TryEnqueue(() => _dockService.ShowNormal()));
            _notificationService.Register();
```
por:
```csharp
            ProtocolService.Register();
            _notificationService = new NotificationService();
            _notificationService.Register();
```
Remover o `using Microsoft.UI.Dispatching;` se ficar sem uso.

- [ ] **Step 4: Compilar**

Run: `dotnet build app/BoxeZone.csproj -c Debug -p:Platform=x64`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

- [ ] **Step 5: Testar com o app aberto e fechado**

1. Rodar o app. Conferir o registro: `reg query HKCU\Software\Classes\boxezone\shell\open\command` mostra o caminho do `.exe` com `"%1"`.
2. Com o app aberto e o widget escondido ("X"): `Start-Process "boxezone:open"` → o widget aparece (uma instância só: `Get-Process BoxeZone` lista um processo).
3. "Sair" pela bandeja; `Start-Process "boxezone:open"` → o app abre.
4. Enviar um arquivo num provedor conectado; "Sair"; na Central de Notificações, clicar na notificação "Enviado" → o link abre no navegador, sem abrir o app. Repetir clicando em "Abrir".
5. Provocar uma falha (por exemplo, desconectar a internet e enviar), "Sair", clicar na notificação de falha → o app abre.

- [ ] **Step 6: Documentar e commitar**

Em `app/docs/arquitetura.md`, no item **Notificações** de "Integração com o Windows", trocar "Cliques só funcionam com o app aberto." por: "Cliques usam ativação por protocolo e funcionam com o app fechado: o link do arquivo (https) abre no navegador; notificações sem link usam `boxezone:open`, protocolo registrado pelo app no HKCU a cada início (`ProtocolService`) que abre o widget pela instância única." Em `app/docs/proximos-passos.md`, remover de "Limitações conhecidas" a linha "Clicar numa notificação antiga depois de \"Sair\" não faz nada…".
```bash
git add app/Services/ProtocolService.cs app/Services/NotificationService.cs app/App.xaml.cs app/Strings/pt-BR/Resources.resw app/docs/arquitetura.md app/docs/proximos-passos.md
git commit -m "feat: notification clicks work with the app closed"
```

---

### Task 3: Aviso de versão nova

**Files:**
- Create: `app/Services/UpdateService.cs`
- Modify: `app/Services/SettingsService.cs` (`AppSettings`)
- Modify: `app/ViewModels/SettingsViewModel.cs`
- Modify: `app/Views/SettingsPage.xaml` (card "Atalho e aparência")
- Modify: `app/App.xaml.cs`
- Modify: `app/Strings/pt-BR/Resources.resw`
- Modify: `website/privacidade/index.html`
- Modify: `app/docs/configuracoes.dc.html` (linha "Versão" no protótipo)

**Interfaces:**
- Consumes: `NotificationService.ShowUpdateAvailable(string version, string url)` (Tarefa 2); versão do assembly (Tarefa 1).
- Produces: `UpdateInfo(Version Version, string Url)`; `UpdateService(SettingsService)`, `Task<UpdateInfo?> CheckAsync()`, `static Version CurrentVersion`, `static bool TryParseTag(string? tag, out Version version)`; `SettingsViewModel.ShowUpdate(UpdateInfo)`, `VersionText`, `UpdateText`, `UpdateUri`.

- [ ] **Step 1: Campos novos em `AppSettings`**

Em `app/Services/SettingsService.cs`, na classe `AppSettings`, depois de `Window`:
```csharp
        /// <summary>Last successful look at GitHub Releases (at most one a day).</summary>
        public DateTimeOffset? LastUpdateCheck { get; set; }

        /// <summary>Newest release seen ("1.2.0") and its page; null until one exists.</summary>
        public string? LatestVersion { get; set; }

        public string? LatestUrl { get; set; }

        /// <summary>Version already announced by notification, so each one is announced once.</summary>
        public string? NotifiedVersion { get; set; }
```

- [ ] **Step 2: Criar `UpdateService`**

`app/Services/UpdateService.cs`:
```csharp
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BoxeZone.Services
{
    public sealed record UpdateInfo(Version Version, string Url);

    /// <summary>
    /// Looks at the latest GitHub Release at most once a day and remembers what it found, so the
    /// Settings line survives restarts. Network errors are silent and retried on the next start.
    /// </summary>
    public sealed class UpdateService
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/WebLucasDev/BoxeZone/releases/latest";

        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
        private static readonly HttpClient Http = CreateClient();

        private readonly SettingsService _settingsService;

        public UpdateService(SettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        /// <summary>This build's version, as Major.Minor.Build.</summary>
        public static Version CurrentVersion { get; } =
            Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

        /// <summary>The newest release if it is newer than this build; otherwise null.</summary>
        public async Task<UpdateInfo?> CheckAsync()
        {
            AppSettings settings = _settingsService.Settings;
            bool isDue = settings.LastUpdateCheck is not { } last || DateTimeOffset.UtcNow - last >= CheckInterval;
            if (isDue)
            {
                try
                {
                    using HttpResponseMessage response = await Http.GetAsync(LatestReleaseUrl);

                    // 404: no release published yet. Counts as a successful check.
                    if (response.IsSuccessStatusCode)
                    {
                        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        string? tag = json.RootElement.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() : null;
                        string? url = json.RootElement.TryGetProperty("html_url", out JsonElement urlElement) ? urlElement.GetString() : null;
                        if (TryParseTag(tag, out Version version) && url is not null)
                        {
                            settings.LatestVersion = version.ToString(3);
                            settings.LatestUrl = url;
                        }
                    }

                    if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        settings.LastUpdateCheck = DateTimeOffset.UtcNow;
                        _settingsService.Save();
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
                {
                    // Offline, rate-limited or unexpected answer: try again on the next start.
                }
            }

            return Version.TryParse(settings.LatestVersion, out Version? latest)
                && Normalize(latest) > CurrentVersion
                && settings.LatestUrl is not null
                ? new UpdateInfo(Normalize(latest), settings.LatestUrl)
                : null;
        }

        /// <summary>"v1.2.0", "1.2" or "v1.2.0-rc.1" → 1.2.0. False for anything else.</summary>
        public static bool TryParseTag(string? tag, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            string core = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
            if (!Version.TryParse(core, out Version? parsed))
            {
                return false;
            }

            version = Normalize(parsed);
            return true;
        }

        private static Version Normalize(Version version) =>
            new(version.Major, version.Minor, Math.Max(version.Build, 0));

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BoxeZone");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }
    }
}
```

- [ ] **Step 3: Verificar `TryParseTag` antes de ligar na UI**

Sem projeto de testes (e o PowerShell não carrega um assembly .NET 10): bloco **temporário, não commitado** no início de `App.OnLaunched`:
```csharp
            // TEMP: TryParseTag cases; remove before committing.
            System.IO.File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "boxezone-tags.txt"),
                new[] { "v1.2.0", "1.2", "v1.2.0-rc.1", "V2.0.1+build5", "", null, "latest", "v" }
                    .Select(t => $"{t ?? "null"} -> {UpdateService.TryParseTag(t, out Version v)} {v}"));
```
(`using System.Linq;` temporário.) Rodar o app, fechar pelo "Sair" e ler `Get-Content $env:TEMP\boxezone-tags.txt`. Expected:
```
v1.2.0 -> True 1.2.0
1.2 -> True 1.2.0
v1.2.0-rc.1 -> True 1.2.0
V2.0.1+build5 -> True 2.0.1
 -> False 0.0.0
null -> False 0.0.0
latest -> False 0.0.0
v -> False 0.0.0
```
Remover o bloco e o `using` temporários.

- [ ] **Step 4: Ligar no `App` e testar os casos de falha**

Em `app/App.xaml.cs`: campo `private UpdateService? _updateService;`; em `OnLaunched`, logo depois de `_ = viewModel.RestoreSessionsAsync();`:
```csharp
            // Once a day, a new GitHub Release is announced once and listed in Settings.
            _updateService = new UpdateService(_settingsService);
            _ = CheckForUpdateAsync(settings);
```
Método novo na classe `App`:
```csharp
        private async Task CheckForUpdateAsync(SettingsViewModel settings)
        {
            UpdateInfo? update = await _updateService!.CheckAsync();
            if (update is null)
            {
                return;
            }

            settings.ShowUpdate(update);
            string version = update.Version.ToString(3);
            if (_settingsService!.Settings.NotifiedVersion != version)
            {
                _notificationService?.ShowUpdateAvailable(version, update.Url);
                _settingsService.Settings.NotifiedVersion = version;
                _settingsService.Save();
            }
        }
```
(`using System.Threading.Tasks;` no topo.)
Testes (cada um apagando `lastUpdateCheck` do `settings.json` com o app fechado antes):
1. Repositório sem Release (situação atual): app abre; nenhum aviso; `settings.json` ganha `lastUpdateCheck`.
2. Sem internet (desligar o adaptador ou modo avião): app abre normalmente; nenhum aviso; `lastUpdateCheck` **não** é gravado.
3. Caso positivo, temporário e não commitado: trocar `LatestReleaseUrl` por `https://api.github.com/repos/microsoft/terminal/releases/latest` (tags `v1.x.y`, maiores que 1.0.0) → uma notificação "Versão nova do Boxe Zone"; clicar abre a página da Release no navegador, com o app aberto e depois de "Sair"; reabrir o app não repete a notificação; Configurações mostram o link. Desfazer a troca da URL e limpar `latestVersion`, `latestUrl`, `notifiedVersion` e `lastUpdateCheck` do `settings.json`.

- [ ] **Step 5: Linha "Versão" nas Configurações**

`app/ViewModels/SettingsViewModel.cs`:
```csharp
        /// <summary>This build, like "1.0.0".</summary>
        public string VersionText => UpdateService.CurrentVersion.ToString(3);

        /// <summary>"Baixar a 1.2.0" when a newer release exists; null otherwise.</summary>
        [ObservableProperty]
        public partial string? UpdateText { get; set; }

        [ObservableProperty]
        public partial Uri? UpdateUri { get; set; }

        public void ShowUpdate(UpdateInfo update)
        {
            UpdateUri = new Uri(update.Url);
            UpdateText = string.Format(_strings.GetString("UpdateDownloadFormat"), update.Version.ToString(3));
        }
```
(`using System;`.) Em `app/Views/SettingsPage.xaml`, no card "Atalho e aparência", depois do `Grid` do tema e antes de `</StackPanel>`:
```xml
                        <Grid
                            Padding="12,10"
                            BorderBrush="{ThemeResource SettingsCardBorderBrush}"
                            BorderThickness="0,1,0,0"
                            ColumnSpacing="12">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock
                                x:Uid="VersionTitle"
                                VerticalAlignment="Center"
                                FontSize="13" />
                            <TextBlock
                                Grid.Column="1"
                                VerticalAlignment="Center"
                                FontSize="12"
                                Foreground="{ThemeResource TextSecondaryBrush}"
                                Text="{x:Bind ViewModel.Settings.VersionText}" />
                            <HyperlinkButton
                                Grid.Column="2"
                                Padding="4,2"
                                VerticalAlignment="Center"
                                Content="{x:Bind ViewModel.Settings.UpdateText, Mode=OneWay}"
                                FontSize="12"
                                NavigateUri="{x:Bind ViewModel.Settings.UpdateUri, Mode=OneWay}"
                                Visibility="{x:Bind helpers:BindConverters.VisibleWhenText(ViewModel.Settings.UpdateText), Mode=OneWay}" />
                        </Grid>
```
Strings:
```xml
  <data name="VersionTitle.Text" xml:space="preserve">
    <value>Versão</value>
  </data>
  <data name="UpdateDownloadFormat" xml:space="preserve">
    <value>Baixar a {0}</value>
  </data>
```
No protótipo `app/docs/configuracoes.dc.html`, acrescentar a mesma linha ("Versão", "1.0.0", link "Baixar a 1.1.0") ao fim do card "Atalho e aparência", copiando o estilo da linha "Tema". Build sem avisos; abrir Configurações nos dois temas e conferir a linha (com e sem o link, usando o caso positivo temporário do Step 4).

- [ ] **Step 6: Política de privacidade**

Em `website/privacidade/index.html`:
- Em "O que o app não faz", trocar o terceiro item por: "Não vende, aluga nem compartilha dados com terceiros. O app se comunica apenas com os serviços que você conectou (Google, Microsoft e Dropbox), para fazer login e enviar os seus arquivos, e com o GitHub, para saber se há versão nova."
- Nova seção antes de "Uso de dados das APIs do Google":
```html
    <h2>Verificação de versão nova</h2>
    <p>Uma vez por dia, ao abrir, o Boxe Zone consulta a página pública de versões do projeto no GitHub para saber se existe uma versão mais nova. O pedido não leva nenhum dado seu: o GitHub recebe apenas o que qualquer acesso a um site recebe, como o endereço IP. Se houver versão nova, o app avisa por uma notificação e nas Configurações; nada é baixado ou instalado sem você.</p>
```
- Data no topo: "Atualizada em {dia} de {mês} de 2026." com a data do commit.

- [ ] **Step 7: Commit**

```bash
git add app/Services/UpdateService.cs app/Services/SettingsService.cs app/ViewModels/SettingsViewModel.cs app/Views/SettingsPage.xaml app/App.xaml.cs app/Strings/pt-BR/Resources.resw app/docs/configuracoes.dc.html website/privacidade/index.html
git commit -m "feat: announce new versions from GitHub Releases"
```

---

### Task 4: Instalador Inno Setup

**Files:**
- Create: `app/installer/BoxeZone.iss`
- Create: `app/installer/build.ps1`
- Modify: `.gitignore` (raiz)

**Interfaces:**
- Consumes: pasta `publish` da Tarefa 1 (`app/bin/Release/net10.0-windows10.0.19041.0/win-{x64|arm64}/publish`).
- Produces: `app/installer/build.ps1 -Arch x64|arm64 -Version <tag sem "v">` → `app/installer/output/BoxeZone-Setup-{x64|arm64}.exe` (usado pela Tarefa 5).

- [ ] **Step 1: Instalar o Inno Setup na máquina de desenvolvimento**

Run: `winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements`
Expected: Inno Setup 6.3 ou mais novo. Conferir: `Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"` ou `Test-Path "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"`.

- [ ] **Step 2: Script do instalador**

`app/installer/BoxeZone.iss`:
```iss
; Boxe Zone installer. Built by build.ps1 (locally) and .github/workflows/release.yml (releases):
;   ISCC /DAppVersion=1.2.0-rc.1 /DNumericVersion=1.2.0 /DArch=x64 BoxeZone.iss
; Per-user install, no admin. Fixed output names so the site's "latest" link never changes.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "1.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

#define PublishDir "..\bin\Release\net10.0-windows10.0.19041.0\win-" + Arch + "\publish"

[Setup]
; Never change AppId: it is how a new version finds and replaces the installed one.
AppId={{5B7E0C4A-2D61-4B3F-9A7E-1C8D2F4B6A90}
AppName=Boxe Zone
AppVersion={#AppVersion}
AppVerName=Boxe Zone {#AppVersion}
AppPublisher=Lucas Venancio
AppPublisherURL=https://weblucasdev.github.io/BoxeZone/
AppSupportURL=https://weblucasdev.github.io/BoxeZone/
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=Boxe Zone
DefaultDirName={localappdata}\Programs\Boxe Zone
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=BoxeZone-Setup-{#Arch}
SetupIconFile=..\Assets\BoxeZone.ico
UninstallDisplayIcon={app}\BoxeZone.exe
UninstallDisplayName=Boxe Zone
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The app lives in the tray and cancels window closes, so it is ended before files are replaced.
CloseApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[CustomMessages]
ptbr.DeleteUserData=Apagar também as preferências e as contas conectadas do Boxe Zone neste computador?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files of an older version that the new one no longer ships.
Type: filesandordirs; Name: "{app}\*"

[Icons]
; Same AppUserModelID as the app's notifications.
Name: "{autoprograms}\Boxe Zone"; Filename: "{app}\BoxeZone.exe"; AppUserModelID: "BoxeZone.App"
Name: "{autodesktop}\Boxe Zone"; Filename: "{app}\BoxeZone.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BoxeZone.exe"; Description: "{cm:LaunchProgram,Boxe Zone}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CloseBoxeZone();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM BoxeZone.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseBoxeZone();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    CloseBoxeZone();

  if CurUninstallStep = usPostUninstall then
  begin
    // Written by the app itself on every start.
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BoxeZone');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\AppUserModelId\BoxeZone.App');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\boxezone');

    if (not UninstallSilent) and
       (MsgBox(CustomMessage('DeleteUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{localappdata}\BoxeZone'), True, True, True);
  end;
end;
```
(O `[InstallDelete]` limpa a pasta do app antes de copiar a versão nova; os dados do usuário ficam em `%LocalAppData%\BoxeZone`, fora dela.)

- [ ] **Step 3: Script de build**

`app/installer/build.ps1` (compatível com PowerShell 5.1 e 7):
```powershell
<#
.SYNOPSIS
  Publishes Boxe Zone and builds its installer: app\installer\output\BoxeZone-Setup-<arch>.exe
.EXAMPLE
  .\app\installer\build.ps1 -Arch x64 -Version 1.2.0
#>
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$appDir = Split-Path $PSScriptRoot -Parent
$platform = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
# Assembly and file versions must be numeric: "1.2.0-rc.1" -> "1.2.0".
$numericVersion = ($Version -split '[-+]')[0]

if (-not (Test-Path (Join-Path $appDir 'appsettings.local.json'))) {
    throw 'app\appsettings.local.json not found: an installer without provider credentials could not sign in to anything.'
}

dotnet publish (Join-Path $appDir 'BoxeZone.csproj') -c Release -p:Platform=$platform -p:Version=$numericVersion -p:InformationalVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it: winget install JRSoftware.InnoSetup' }

& $iscc "/DAppVersion=$Version" "/DNumericVersion=$numericVersion" "/DArch=$Arch" (Join-Path $PSScriptRoot 'BoxeZone.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Get-Item (Join-Path $PSScriptRoot "output\BoxeZone-Setup-$Arch.exe")
```
Em `.gitignore` (raiz), seção "Build output":
```gitignore
app/installer/output/
```

- [ ] **Step 4: Gerar os dois instaladores**

Fechar o BoxeZone. Run:
```powershell
.\app\installer\build.ps1 -Arch x64 -Version 1.0.0
.\app\installer\build.ps1 -Arch arm64 -Version 1.0.0
```
Expected: `BoxeZone-Setup-x64.exe` e `BoxeZone-Setup-arm64.exe` em `app\installer\output\`; anotar o tamanho de cada um.

- [ ] **Step 5: Instalar (x64)**

Rodar `BoxeZone-Setup-x64.exe`. Conferir: nenhum pedido de administrador; assistente em português; ao fim, "Abrir o Boxe Zone" abre o app; arquivos em `%LocalAppData%\Programs\Boxe Zone`; "Boxe Zone" no Menu Iniciar abre o app; em Configurações > Aplicativos instalados aparece "Boxe Zone 1.0.0" com o ícone da luva; contas e preferências existentes continuam (mesma pasta `%LocalAppData%\BoxeZone`); uma notificação de envio funciona e o clique com o app fechado abre o link.

- [ ] **Step 6: Atualizar por cima com o app aberto**

Com o app instalado aberto, gerar `-Version 1.0.1` e rodar o instalador novo. Expected: sem erro de "arquivo em uso"; o app é fechado e reaberto no fim; propriedades do `BoxeZone.exe` instalado mostram 1.0.1; Aplicativos instalados mostra uma só entrada, 1.0.1.

- [ ] **Step 7: Desinstalar**

1. Desinstalar respondendo **Não** a "Apagar também as preferências…" (com o app aberto). Expected: app fechado; pasta do programa e atalho removidos; `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v BoxeZone`, `reg query HKCU\Software\Classes\boxezone` e `reg query HKCU\Software\Classes\AppUserModelId\BoxeZone.App` não encontram nada; `%LocalAppData%\BoxeZone` continua.
2. Reinstalar e conferir que contas e preferências voltaram.
3. O caso "Sim" apaga `%LocalAppData%\BoxeZone`, que tem as contas reais do desenvolvedor: testar só copiando a pasta antes (`Copy-Item $env:LOCALAPPDATA\BoxeZone $env:TEMP\BoxeZone-backup -Recurse`), desinstalar com "Sim", conferir que a pasta sumiu e restaurar a cópia.
4. ARM64: sem máquina ARM disponível, conferir só que o instalador ARM64 recusa rodar numa máquina x64 com a mensagem do Inno sobre arquitetura.

- [ ] **Step 8: Commit**

```bash
git add app/installer/BoxeZone.iss app/installer/build.ps1 .gitignore
git commit -m "build: Inno Setup installer for x64 and ARM64"
```

---

### Task 5: Workflow de Release

**Files:**
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: `app/installer/build.ps1` (Tarefa 4); Secret `APPSETTINGS_LOCAL_JSON`.
- Produces: Release `vX.Y.Z` com `BoxeZone-Setup-x64.exe` e `BoxeZone-Setup-arm64.exe` (lida pelo `UpdateService` e pelo botão do site).

- [ ] **Step 1: Workflow**

`.github/workflows/release.yml`:
```yaml
# Builds both installers and publishes a GitHub Release when a version tag is pushed:
#   git tag v1.2.0 && git push origin v1.2.0
# Tags with a suffix (v1.2.0-rc.1) become pre-releases, which "latest" ignores.
name: Release

on:
  push:
    tags: ["v*.*.*"]

permissions:
  contents: write

jobs:
  release:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - name: Provider credentials
        shell: pwsh
        env:
          APPSETTINGS: ${{ secrets.APPSETTINGS_LOCAL_JSON }}
        run: |
          if (-not $env:APPSETTINGS) { throw "Secret APPSETTINGS_LOCAL_JSON is missing (Settings > Secrets and variables > Actions)." }
          [IO.File]::WriteAllText("$PWD/app/appsettings.local.json", $env:APPSETTINGS)

      - name: Inno Setup
        shell: pwsh
        run: |
          if (-not (Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")) { choco install innosetup --no-progress -y }

      - name: Build installers
        shell: pwsh
        run: |
          $version = "${{ github.ref_name }}".TrimStart('v')
          ./app/installer/build.ps1 -Arch x64 -Version $version
          ./app/installer/build.ps1 -Arch arm64 -Version $version

      - name: Publish release
        shell: pwsh
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          $tag = "${{ github.ref_name }}"
          $files = (Get-ChildItem app/installer/output/*.exe).FullName
          $ghArgs = @($tag) + $files + @('--title', "Boxe Zone $($tag.TrimStart('v'))", '--generate-notes')
          if ($tag -match '-') { $ghArgs += '--prerelease' }
          gh release create @ghArgs
```

- [ ] **Step 2: Commit e push da branch**

```bash
git add .github/workflows/release.yml
git commit -m "ci: build installers and publish releases from version tags"
git push -u origin feat/installer
```
(O workflow só roda em tags; o push da branch não publica nada.)

- [ ] **Step 3: Secret (feito pelo usuário)**

Pedir ao usuário: GitHub → BoxeZone → Settings → Secrets and variables → Actions → New repository secret; nome `APPSETTINGS_LOCAL_JSON`; valor: o conteúdo inteiro de `app/appsettings.local.json`.

- [ ] **Step 4: Release de teste**

Com o Secret criado:
```bash
git tag v1.0.0-rc.1 feat/installer
git push origin v1.0.0-rc.1
```
Acompanhar em Actions. Expected: Release "Boxe Zone 1.0.0-rc.1" marcada como pré-lançamento, com os dois instaladores. Baixar o x64 da Release, instalar e repetir o essencial do Task 4, Step 5 (abre, conecta, envia). Se algo falhar, corrigir na branch, apagar a tag e a Release (`git push origin :refs/tags/v1.0.0-rc.1` e excluir a Release pela página) e repetir com `rc.2`.

---

### Task 6: Site, documentação e versão 1.0.0

**Files:**
- Modify: `website/index.html`
- Modify: `website/assets/style.css`
- Modify: `app/docs/arquitetura.md`
- Modify: `app/docs/proximos-passos.md`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: nomes fixos dos instaladores (Tarefa 4) e a Release (Tarefa 5).

- [ ] **Step 1: Botão de download**

Em `website/index.html`, trocar o bloco `.get`:
```html
        <div class="get">
          <a class="button" href="https://github.com/WebLucasDev/BoxeZone/releases/latest/download/BoxeZone-Setup-x64.exe">Baixar para Windows</a>
          <small>Windows 10 e 11, 64 bits · <a href="https://github.com/WebLucasDev/BoxeZone/releases/latest/download/BoxeZone-Setup-arm64.exe">versão para ARM</a></small>
        </div>
        <p class="smartscreen">Se o Windows mostrar "O Windows protegeu o computador", clique em Mais informações e depois em Executar assim mesmo. O aviso aparece porque o instalador ainda não tem assinatura digital.</p>
```
Em `website/assets/style.css`, depois de `.get small`:
```css
.get small a {
  color: var(--muted);
}

.smartscreen {
  max-width: 34em;
  margin: 16px 0 0;
  color: var(--muted);
  font-size: 13px;
}
```
Abrir `website/index.html` no navegador e conferir no desktop e em 390 px.

- [ ] **Step 2: Documentação**

- `app/docs/arquitetura.md`: seção nova "Instalador e versões" com: instalação por usuário em `%LocalAppData%\Programs\Boxe Zone`; `AppId` fixo; `[InstallDelete]` limpa a pasta do app a cada versão; o instalador fecha o app com `taskkill` (o app cancela o fechamento da janela); o desinstalador apaga Run, AUMID e `boxezone`, e pergunta pelos dados; `build.ps1` e o workflow de tag; Secret `APPSETTINGS_LOCAL_JSON`; tags com sufixo viram pré-lançamento, ignoradas por `releases/latest` (site e `UpdateService`); componentes do Windows App SDK em vez do metapacote, e como acrescentar um componente se faltar. Atualizar o item "Logo do app" se algo mudou.
- `app/docs/proximos-passos.md`: mover "Diminuir a versão publicada", "Instalador", "Desinstalação limpa", "Versão e metadados do `.exe`" e "Atualização automática ou ao menos aviso de versão nova" para Concluído (com os commits); registrar o tamanho final; manter "assinatura de código" em Distribuição.
- `CLAUDE.md`: em "Comandos", acrescentar `.\app\installer\build.ps1 -Arch x64 -Version 1.0.0   # app\installer\output\BoxeZone-Setup-x64.exe` e uma linha sobre publicar versão (`git tag vX.Y.Z` + push).

- [ ] **Step 3: Commit**

```bash
git add website/index.html website/assets/style.css app/docs/arquitetura.md app/docs/proximos-passos.md CLAUDE.md
git commit -m "docs: download button, installer notes and roadmap"
```

- [ ] **Step 4: Teste do usuário, merge e versão 1.0.0 (com aprovação explícita)**

Depois de o usuário testar o instalador da `v1.0.0-rc.1`: merge `--ff-only` de `feat/installer` na `main`, push (o site publica o botão), `git tag v1.0.0` na `main` e push da tag. Conferir que a Release 1.0.0 saiu (não marcada como pré-lançamento) e que os dois links do botão baixam os instaladores (`curl -sIL` retornando 200).
