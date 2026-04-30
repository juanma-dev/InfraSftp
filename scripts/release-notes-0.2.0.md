# InfraSftp 0.2.0

First release with **Linux (Fedora)** support alongside Windows.

## What's new

- **Multi-target build**. The same codebase now targets `net8.0-windows` (Windows installer + portable, auto-update) and `net8.0` (Linux RPM). Tests run against both TFMs.
- **Linux password vault** backed by libsecret / Secret Service. Each entry is a separate item in the active desktop session keyring (gnome-keyring, kwallet5, …) tagged `application=com.webjuanma.InfraSftp`. Passwords are passed via stdin to `secret-tool` so they never appear in `/proc/<pid>/cmdline`.
- **Per-user data directory** centralised in `Services/AppPaths.cs`. Falls back through `$XDG_CONFIG_HOME` → `$HOME/.config` on Linux when `~/.config` doesn't yet exist (a .NET 8 quirk that previously caused first-run installs to scatter files into the process CWD).
- **Glyph fixes** for two toolbar buttons that needed niche fonts (CJK fullwidth `+`, Noto Symbols 2 power) — replaced with widely-available alternatives so a minimal Fedora doesn't render tofu.
- **README** rewritten with parallel Windows / Fedora install sections in EN + ES.

## Downloads

### Windows

| File | Notes |
|---|---|
| `com.webjuanma.InfraSftp-win-Setup.exe` | Recommended. Installer, registers in Start Menu, auto-updates. |
| `com.webjuanma.InfraSftp-win-Portable.zip` | Unzip and run. No registry, no auto-update. |
| `com.webjuanma.InfraSftp-0.2.0-full.nupkg` + `RELEASES` + `releases.win.json` | Velopack channel manifest (consumed by the auto-updater — you generally don't download these directly). |

The installer is signed with a self-signed certificate. Windows SmartScreen will show *"Windows protected your PC"* on first launch — click **More info → Run anyway**. Subsequent launches will not warn.

### Linux (Fedora)

| File | Notes |
|---|---|
| `infrasftp-0.2.0-1.fc44.x86_64.rpm` | Self-contained RPM (.NET 8 runtime bundled). Built and tested on Fedora 44. |
| `RPM-GPG-KEY-InfraSftp.asc` | GPG public key — import into `rpm` to verify the signature. |

```bash
# Optional but recommended:
sudo rpm --import https://github.com/juanma-dev/InfraSftp/releases/download/v0.2.0/RPM-GPG-KEY-InfraSftp.asc

sudo dnf install ./infrasftp-0.2.0-1.fc44.x86_64.rpm
infrasftp     # or launch from the desktop menu
```

**Runtime deps** (declared in the RPM, dnf pulls them in): `libsecret`, `dejavu-sans-fonts`, `google-noto-emoji-fonts`.

**GPG key fingerprint**: `C985 86D7 8D25 3157 0F8A C140 1E5F 3CA3 C56F 1FDC` — InfraSftp Release Signing.

## Auto-updates

- **Windows**: the installed flavour self-checks GitHub Releases ~2 s after launch and offers "Install & Restart" in-app.
- **Linux**: no auto-update path. Subscribe to release notifications and run `sudo dnf upgrade ./...rpm` against a freshly downloaded RPM.

## Bilingual UI

Spanish UI strings throughout. README covers both languages.
