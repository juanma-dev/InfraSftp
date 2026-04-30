# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build, Run & Test

```bash
dotnet build                                   # compile both TFMs
dotnet run -f net8.0-windows                   # launch on Windows
dotnet run -f net8.0                           # launch on Linux
dotnet build InfraSftp.sln                     # main + tests, both TFMs
dotnet test  InfraSftp.sln                     # run xUnit on both TFMs
```

The main project multi-targets `net8.0-windows;net8.0`. Pass `-f` to disambiguate when running. Outputs land in `bin/Debug/<tfm>/`. The solution adds a sibling [tests/InfraSftp.Tests](tests/InfraSftp.Tests) xUnit project that mirrors the multi-target and references the main project, so `dotnet test` runs the full suite once per TFM (currently 32 × 2 = 64 results). `[InternalsVisibleTo("InfraSftp.Tests")]` is wired in [InfraSftp.csproj](InfraSftp.csproj) so `internal static` helpers (e.g. `MainWindowViewModel.StripCopySuffix`) are reachable without exposing a wider public surface.

**Building from WSL:** `dotnet build` from a path under `/mnt/c/...` fails with *"Could not set file permission 755 for ... apphost"* because NTFS rejects chmod from WSL. Clone or rsync the source into the Linux filesystem (`~/InfraSftp`) and build from there. ext4 is also 5–10× faster than the NTFS bridge.

Tests use plain `[Fact]` / `[Theory]` for pure logic and `Avalonia.Headless` (with a `IClassFixture<HeadlessAvaloniaFixture>`) for dispatcher-bound flows. We avoid the `Avalonia.Headless.XUnit` add-on because it pulls xunit v3, which collides with the v2 baseline in the test csproj. Note: tests live under `./tests/`, which the SDK would otherwise auto-include into the main exe — [InfraSftp.csproj](InfraSftp.csproj) explicitly removes `tests/**` from the main project's compile / resource globs.

`AvaloniaUseCompiledBindingsByDefault=true` is set in [InfraSftp.csproj](InfraSftp.csproj), so `x:DataType` is required on XAML templates and binding errors surface at compile time rather than runtime.

`AvaloniaUI.DiagnosticsSupport` is included only in Debug — F12 opens the DevTools inspector in a dev build.

## Architecture

Two-pane SFTP client (inspired by WinSCP / FileZilla). Users manage saved connection profiles in a sidebar, open left/right browser tabs over Local or Remote filesystems, and transfer by clicking a file (direction is inferred from source pane).

### MVVM wiring

- **Pattern**: CommunityToolkit.Mvvm source generators. Fields marked `[ObservableProperty] private T _foo;` generate the public `Foo` property + `INotifyPropertyChanged` plumbing. Methods marked `[RelayCommand]` generate `FooCommand` / `FooAsyncCommand`. Do not write the generated property by hand — edit the backing field and let the generator rebuild.
- **Single window**: [Views/MainWindow.axaml.cs](Views/MainWindow.axaml.cs) instantiates `MainWindowViewModel` directly in its constructor. [App.axaml.cs](App.axaml.cs) also sets it as `DataContext` on the `MainWindow` it creates — both paths exist; only one runs depending on startup.
- **ViewLocator**: [ViewLocator.cs](ViewLocator.cs) does reflective `*ViewModel` → `*View` resolution. Registered as `Application.DataTemplates` in [App.axaml](App.axaml) but currently unused since the app is single-window. If you add `ContentControl Content="{Binding SomeVM}"` bindings, this is what renders them.
- **Codebehind escape hatch**: XAML uses `Click="..."` handlers in a few spots ([MainWindow.axaml.cs](Views/MainWindow.axaml.cs): `ToggleMenu`, `SwitchToLog`, `SwitchToTransfers`, `OpenCreateModal`) because they need to flip VM state that isn't conveniently a single `RelayCommand`. Everything else goes through commands.

### Services layer

Two scopes:

- **VM-owned (no DI container)** — created in `MainWindowViewModel`:
  - **[SftpService](Services/SftpService.cs)** — one instance per live SSH session. Wraps the synchronous SSH.NET API inside `Task.Run` so the UI thread never blocks. Surfaces progress via two events: `OnLog(string)` and `OnTransferProgress(fileName, transferred, total)`. The ViewModel stores these in `Dictionary<string, SftpService> _connections` keyed by `Profile.Id`, so multiple concurrent remote connections are possible.
  - **[ProfileService](Services/ProfileService.cs)** — persists `Profile` list to `%APPDATA%/InfraSftp/profiles.json` and passwords to `%APPDATA%/InfraSftp/vault.dat`.

- **Process-wide** — created as `static` properties on [Program](Program.cs) before Avalonia boots, so they're armed for crashes during XAML / theme init:
  - **[LoggingService](Services/LoggingService.cs)** — always-on local file log under `%APPDATA%/InfraSftp/logs/app-yyyyMMdd.log`. Synchronised writes; 7-day retention trimmed once per session. The privacy-preserving fallback for users who keep telemetry off.
  - **[CrashReportingService](Services/CrashReportingService.cs)** — wraps Sentry. **Opt-in by design** (`AppSettings.EnableTelemetry` defaults `false`). Hooks `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`. The hooks are wired regardless of the toggle — they always go to the local log, and forward to Sentry only when enabled. The toggle is live: `SetTelemetryEnabled(bool)` boots / disposes the SDK without restarting the app. The DSN is hard-coded in this service (Sentry DSNs are not secret — they're write-only ingest endpoints).
  - **[UpdateService](Services/UpdateService.cs)** — wraps `Velopack.UpdateManager` with `GithubSource("https://github.com/juanma-dev/InfraSftp", token: null, prerelease: false)`. The repo is public so no token is needed. `IsInstalled` is false in dev runs (`dotnet run`), in which case `CheckAsync` short-circuits — devs aren't pestered. The VM fires `CheckAsync` 2.5 s after window load, then auto-pre-stages the download so "Install & Restart" is instant when the user clicks.

### Password vault

The vault is fronted by [IPasswordVault](Services/Vault/IPasswordVault.cs) and resolved per-platform by [PasswordVaultFactory](Services/Vault/PasswordVaultFactory.cs). `ProfileService.SavePassword/GetPassword/DeletePassword` delegate; the public surface (`MainWindowViewModel`) does not know which backend is in use.

- **Windows — [DpapiVault](Services/Vault/DpapiVault.cs):** all secrets in a single JSON dictionary at `%APPDATA%/InfraSftp/vault.dat`, encrypted with DPAPI under `DataProtectionScope.CurrentUser`. The OS binds the key to the logged-in user's credential so the file cannot be decrypted by another account or copied to another machine. A constant entropy string (`InfraSftp.Vault.v2`) acts as a domain separator so other apps running as the same user can't decrypt it either — treat as a domain identifier, not a secret. Compiled only when the `WINDOWS` symbol is defined (auto-set by the `net8.0-windows` TFM); the whole class is wrapped in `#if WINDOWS`.

- **Linux — [LibsecretVault](Services/Vault/LibsecretVault.cs):** each secret is a separate item in the user's session keyring, stored via the `secret-tool` CLI from the `libsecret` package. Two attributes pin the lookup: `application=com.webjuanma.InfraSftp` (domain separator) and `key=<id>`. Passwords are passed via stdin so they never appear in `/proc/<pid>/cmdline`. If `secret-tool` is missing or the Secret Service isn't reachable, `Save/Get/Delete` throw `VaultUnavailableException` with a remediation message — fail loud, don't silently drop credentials.

- **WSL caveat:** the Linux vault needs an active Secret Service daemon (gnome-keyring or kwallet5). On a fresh WSL Fedora without a desktop session, neither is running and the vault throws on first use. This is fine for the RPM target (real Fedora desktop) but means dev-iterating the Linux build inside WSL won't exercise the vault end-to-end without manually starting `gnome-keyring-daemon`.

#### DPAPI v2 layout (Windows only)

- File layout: `"ISv2"` magic (4 bytes) + DPAPI blob.
- v1 (legacy AES-256-CBC, machine/user-seeded key) is still **read-on-load**: any pre-existing vault is decrypted with the legacy path and immediately re-saved as v2. Once every install has loaded the app once, the legacy decrypt path can be deleted.
- DPAPI itself is authenticated, but the 4-byte magic header is not — don't rely on it for tamper detection.

### Transfer semantics (rsync-like)

Recursive transfers (`UploadRecursive`, `DownloadRecursive`, `PipeDirRecursive`) skip a file when source and destination match on **both size AND last-modified time within ±2s** — the same heuristic rsync ships with as `--modify-window=2`. The skip decision is centralised in `SftpService.ShouldSkipTransfer` so all three flavours stay in sync; if you add a new recursive flavour, route it through that helper.

After every successful file transfer, the destination's mtime is stamped to match the source via `SftpClient.SetLastWriteTime` / `File.SetLastWriteTime` so subsequent passes can short-circuit. Both calls are best-effort — an SFTP server that denies SETSTAT or an antivirus that briefly holds a fresh local file will swallow the failure rather than fail the transfer.

The skip can be disabled wholesale by the user via the Settings → Transferencias → "Forzar retransferencia" toggle. The flag is read through `ForceTransferProvider` (a lambda set by the VM at connect time) so toggling it in the UI takes effect immediately on the next transfer without reconnecting. Preserve the `TransferStatus.Skipped` path so the UI log still shows the skip.

### Per-user data directory

[Services/AppPaths.cs](Services/AppPaths.cs) is the single source of truth for the InfraSftp data root. Every service that needed a per-user file (`LoggingService`, `ProfileService`, `KnownHostsService`, `SettingsService`, `DpapiVault`) goes through it instead of computing `Path.Combine(Environment.GetFolderPath(ApplicationData), "InfraSftp")` itself.

Why centralised: on Linux, `Environment.SpecialFolder.ApplicationData` returns an **empty string** when `~/.config` doesn't yet exist. Without the helper, every consumer would silently `Path.Combine("", "InfraSftp")` → `"InfraSftp"`, a relative path under the process CWD — first-run users on Linux would have logs / profiles / vault files scattered into whatever directory they launched from. `AppPaths.Root` falls back to `$XDG_CONFIG_HOME` → `$HOME/.config` → `Environment.UserProfile + .config` and creates the dir up front so subsequent reads always hit a stable absolute path.

Layout: Windows → `%APPDATA%\InfraSftp\`, Linux → `~/.config/InfraSftp/`.

### Long paths (Windows only)

[app.manifest](app.manifest) opts the process into Windows long-path support via `<longPathAware>true</longPathAware>` under `<windowsSettings>`. Without this entry .NET routes file I/O through the legacy Win32 surface and recursive transfers under deep directory trees fail with `PathTooLongException` at the 260-char `MAX_PATH` limit — the same failure mode xftp ships with. With it, paths up to ~32K are handled transparently; no code changes (no `\\?\` prefixes) are needed in `SftpService` or anywhere else. The opt-in is process-scoped, so it's independent of the per-machine `HKLM\...\LongPathsEnabled` registry switch.

The manifest is conditioned on `'$(TargetFramework)' == 'net8.0-windows'` in the csproj — Linux filesystems (ext4, btrfs, xfs) don't carry the 260-char limit so the opt-in is irrelevant there.

Note: the 255-char filename limit on NTFS still applies — that's a filesystem-level rule, not a path-length one. Long-path-aware doesn't help if any single segment between `\` exceeds 255 characters.

### Threading

SFTP callbacks fire on a background thread. Any mutation of `ObservableCollection<T>` or `[ObservableProperty]`-backed state from inside `Task.Run`, `OnTransferProgress`, or `OnLog` handlers **must be marshalled via `Avalonia.Threading.Dispatcher.UIThread.Post`**, otherwise Avalonia throws on collection-changed notifications. This is the established pattern throughout `MainWindowViewModel`.

### Tab model

`LeftTabs` and `RightTabs` are separate `ObservableCollection<TabItem>` — the same tab cannot appear in both. A `TabItem` carries its own `Sftp` reference (null for the built-in Local tab), so file-list refresh and transfers resolve the right connection from the tab itself, not from an "active connection" global.

Tab interactions are split:
- **Click**: `SelectLeftTab` / `SelectRightTab` — activates the tab **within its current panel** (sets `ActiveLeftTab` or `ActiveRightTab`).
- **Drag**: moves the tab **across panels** via `MoveTabToLeft` / `MoveTabToRight`, triggered by the drop handler, not by click. Only remote tabs are draggable — the Local tab is pinned.

There is a **name clash** with `Avalonia.Controls.TabItem` — codebehind files that reference the VM type must use `using TabItem = InfraSftp.ViewModels.TabItem;`.

### Password prompt flow

`ConnectToProfileAsync` first calls `ProfileService.GetPassword()`. On null, it opens the password-prompt modal (`IsPasswordPromptOpen`) with the profile pinned in `PasswordPromptProfile`. `ConnectWithPasswordPrompt` then saves the entered password to the vault and connects. This keeps the main "Edit Profile" modal free of the re-prompt concern.

### Drag-and-drop (Avalonia 12 API)

The legacy `DataObject` + `DragDrop.DoDragDrop` API is **obsolete** in Avalonia 12 and will not compile. Use:

- **Format**: `DataFormat.CreateInProcessFormat<T>(identifier)` for in-process references (no serialization; the payload is the real managed object).
- **Payload**: `new DataTransfer()` + `.Add(DataTransferItem.Create(format, value))`.
- **Source**: `await DragDrop.DoDragDropAsync(PointerPressedEventArgs e, IDataTransfer data, DragDropEffects)` — note the signature takes `PointerPressedEventArgs` specifically, not the general `PointerEventArgs`.
- **Drop target**: register via `DragDrop.AddDragOverHandler` / `AddDropHandler`; read `e.DataTransfer.Contains(format)` / `e.DataTransfer.TryGetValue(format)`.

Because `DoDragDropAsync` demands the original `PointerPressedEventArgs`, the codebehind in [Views/MainWindow.axaml.cs](Views/MainWindow.axaml.cs) stashes it in `_dragStartEvent` at `PointerPressed` and only fires the drag once the pointer has moved beyond a small Manhattan-distance threshold in `PointerMoved`. Do **not** delete this threshold — calling `DoDragDropAsync` on raw PointerPressed swallows the plain click and breaks the `SelectLeftTab`/`SelectRightTab` commands.

The tab format is defined once in `MainWindow`:
```csharp
private static readonly DataFormat<TabItem> TabFormat =
    DataFormat.CreateInProcessFormat<TabItem>("InfraSftp.Tab");
```

### Keyboard shortcuts

Declared once in `<Window.KeyBindings>` in [MainWindow.axaml](Views/MainWindow.axaml); each binding points at a VM `RelayCommand`:

| Gesture | Command |
|---|---|
| `Ctrl+N` | `OpenCreateModalCommand` |
| `Ctrl+Shift+D` | `DisconnectAllCommand` |
| `F5` | `RefreshActivePanelCommand` |
| `Ctrl+W` | `CloseActiveTabCommand` |

`RefreshActivePanel` and `CloseActiveTab` resolve "active" via the VM's `FocusedPanel` property (`"Left"` or `"Right"`), which is updated by `GotFocus` / `PointerPressed` handlers on each panel's root `Border` (both Borders have `Focusable="True"`). If you add new panel-scoped shortcuts, reuse `FocusedPanel` — don't introduce a parallel tracking mechanism.

### File row double-tap

`OnFileDoubleTapped` in the codebehind resolves the `FileNode` from the clicked button's DataContext and invokes `FileActionCommand`. The VM's `FileActionAsync` branches: directories (and `..`) navigate via `OpenItemAsync`, files transfer via `TransferFileAsync`. Single click on a file row still triggers `TransferFileCommand` directly (legacy binding) — keep both until the UX is unified, but prefer the double-tap path for new features.

## UX conventions baked into the XAML

- Dark theme hard-coded in [MainWindow.axaml](Views/MainWindow.axaml) `<Window.Styles>` — button classes `btn`, `btn-secondary`, `btn-danger`, `icon-btn`, `hamburger`, `tab-btn`/`tab-active`, `file-row`, `profile-card`, `menu-item`, `dock-tab`/`dock-tab-active`. Reuse these rather than redefining inline styles.
- Modals are overlay `<Grid>`s with `ZIndex="100"` toggled by `IsModalOpen` / `IsPasswordPromptOpen` / `IsAboutOpen` / `IsShortcutsOpen`. There is no modal manager — add a new one the same way.
- The hamburger **dropdown** (not a modal) lives at `ZIndex="90"` and uses a full-window transparent `Button` bound to `CloseMenuCommand` as its dismiss layer. Copy this pattern for any other non-modal popover.
- Commands inside `DataTemplate`s (profile cards, tab buttons, file rows) reach the VM through `{Binding $parent[Window].((vm:MainWindowViewModel)DataContext).XxxCommand}` because the `DataContext` inside the template is the item (Profile / TabItem / FileNode), not the VM.

## Release pipeline

Two parallel pipelines, one per platform. Both read `<Version>` from [InfraSftp.csproj](InfraSftp.csproj) so the version stamp is a single source of truth.

### Windows (Velopack)

[scripts/build-release.ps1](scripts/build-release.ps1) does the full pipeline: ensure self-signed cert exists in `Cert:\CurrentUser\My`, export to `.signing/InfraSftp.pfx`, `dotnet publish -f net8.0-windows -r win-x64 --self-contained`, then `vpk pack` (which signs every bundle file via signtool). Outputs to `releases/`: `com.webjuanma.InfraSftp-win-Setup.exe`, `com.webjuanma.InfraSftp-win-Portable.zip`, the `*-full.nupkg`, `RELEASES`, and `releases.win.json`. The `-f net8.0-windows` flag is mandatory — `dotnet publish` on a multi-targeted csproj errors NETSDK1129 without it.

### Linux (RPM)

[packaging/linux/build-rpm.sh](packaging/linux/build-rpm.sh) runs end-to-end on a Fedora host (or WSL Fedora — but build from `~/InfraSftp`, not `/mnt/c/...`, because NTFS rejects `chmod 755` on the apphost binary). It does `dotnet publish -f net8.0 -r linux-x64 --self-contained`, extracts PNGs from `Assets/avalonia-logo.ico` via `icotool`, stages everything into `~/rpmbuild/SOURCES/infrasftp-<ver>.tar.gz`, then `rpmbuild -bb` against a copy of [packaging/linux/infrasftp.spec](packaging/linux/infrasftp.spec). Sets `GPG_NAME` to sign the RPM at the end via `rpmsign --addsign`. Output lands in `releases-linux/`.

The spec disables debug-package generation (`%define debug_package %{nil}`) — without that, RPM 6 on Fedora 44 errors with *"Empty %files file ... debugsourcefiles.list"* because there are no source files in a self-contained binary RPM. `AutoReqProv: no` is also load-bearing: the bundled .NET native libraries don't follow standard SONAME conventions, so auto-detected requires would be unsatisfiable.

The signing step uses RPM's default `__gpg_sign_cmd`. Don't override that macro in `~/.rpmmacros` — RPM 6's default works, but the older 4.x-era override pattern (`%__gpg_sign_cmd %{__gpg} gpg ...`) duplicates the `gpg` binary name and fails with "no command supplied".

## Licensing

[LICENSE](LICENSE) is a **stub** containing only the canonical GPLv3 copyright notice, not the full license text. The file references `https://www.gnu.org/licenses/gpl-3.0.html` for the authoritative version. This is intentional — do not paste the full ~700-line GPL text into the repo.
