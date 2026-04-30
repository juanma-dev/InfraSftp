# InfraSftp

A two-pane SFTP client for **Windows** and **Linux (Fedora)**, inspired by
WinSCP and FileZilla. Saved connection profiles, side-by-side local/remote
browsers, drag-and-drop transfers, rsync-style skip-on-match, and a
per-platform encrypted password vault (Windows DPAPI / libsecret on Linux).

> **Languages**: [English](#english) · [Español](#español)

---

## English

### Install on Windows

InfraSftp is published as a Windows installer and a portable ZIP via
[GitHub Releases](https://github.com/juanma-dev/InfraSftp/releases).

- **Installer** (`com.webjuanma.InfraSftp-win-Setup.exe`) — recommended.
  Installs into `%LOCALAPPDATA%\InfraSftp` and adds a Start Menu shortcut.
  Auto-updates work from this install.
- **Portable** (`com.webjuanma.InfraSftp-win-Portable.zip`) — unzip and run
  `InfraSftp.exe`. No registry changes; no auto-updates.

#### First-launch SmartScreen warning

The installer is signed with a **self-signed certificate**. Windows
SmartScreen will show "Windows protected your PC" on the first run because
the certificate has no Microsoft-issued reputation yet. Click **More info →
Run anyway**. Subsequent launches will not warn. The signature still
guarantees the file wasn't modified after build — it just can't prove the
identity of the publisher to Microsoft.

### Install on Fedora Linux

A self-contained RPM is published with each release at
[GitHub Releases](https://github.com/juanma-dev/InfraSftp/releases) as
`infrasftp-<version>-1.fc.x86_64.rpm`.

```bash
# Optional but recommended: import the signing key first so dnf
# verifies the package.
sudo rpm --import https://github.com/juanma-dev/InfraSftp/releases/download/v0.2.0/RPM-GPG-KEY-InfraSftp.asc

sudo dnf install ./infrasftp-0.2.0-1.fc.x86_64.rpm
```

After install, launch from the desktop menu (**InfraSftp**) or run
`infrasftp` from a terminal.

**Runtime dependencies** (declared in the RPM, dnf pulls them in
automatically): `libsecret`, `dejavu-sans-fonts`, `google-noto-emoji-fonts`.
The .NET 8 runtime is bundled — you do not need to install dotnet
separately.

**Password storage**: passwords are stored via libsecret in the active
desktop session keyring (gnome-keyring, kwallet5, …). The keyring needs
a running Secret Service daemon — that is the standard setup on a normal
Fedora Workstation / KDE / Sway desktop. **WSL minimal installs do not
ship a Secret Service daemon by default**, so saving credentials there
will fail; the rest of the app works.

**Auto-updates**: the Linux build does not auto-update. Subscribe to the
GitHub release feed and install the new RPM with `dnf upgrade ./...rpm`.

### Connection profiles

Open the sidebar (`☰` → **New profile** or `Ctrl+N`). Required fields:

| Field | Notes |
|---|---|
| Name | Display label only — does not need to match the host |
| Host | Hostname or IP |
| Port | Default 22 |
| User | SSH username |
| Auth method | Password **or** Private key |

Passwords are stored in a per-platform encrypted vault:

- **Windows** — DPAPI under `%APPDATA%\InfraSftp\vault.dat`. The OS binds
  the encryption key to your Windows user account: the file cannot be
  read by another user on the same machine, nor copied to another
  computer.
- **Linux** — libsecret / Secret Service. Each entry is a separate item
  in the active session keyring (gnome-keyring, kwallet5, …) tagged
  `application=com.webjuanma.InfraSftp`. The keyring is unlocked by your
  desktop login.

#### First connect to a host (TOFU)

The first time you connect to a host, the app shows the server's host key
fingerprint and asks you to confirm. This is **trust on first use** — the
fingerprint is then pinned. If the server's key ever changes, the app
shows a red mismatch dialog instead of silently reconnecting; this is
the same protection OpenSSH uses with `~/.ssh/known_hosts`.

The pinned fingerprints live at `%APPDATA%\InfraSftp\known_hosts.json`.

### Browsing & transferring

- **Tabs**: each panel (left, right) hosts independent tabs. The left panel
  starts with a "Local PC" tab pinned to your home folder. Open new remote
  tabs by clicking a profile in the sidebar.
- **Drag a tab** between panels to move it across.
- **Click a file** in one panel to transfer it to the other.
- **Double-click a folder** to enter it; double-click `..` to go up.
- **Drag-and-drop files** from one pane onto the other to transfer.

#### Skip-on-match

Recursive transfers skip a file when source and destination match on
**both size and last-modified time within ±2 seconds**. This is the same
heuristic `rsync --modify-window=2` uses by default. To force every file
to re-transfer regardless of state, enable **Settings → Transferencias →
Forzar retransferencia**.

The 2-second tolerance absorbs timestamp-resolution mismatches between
FAT (2 s), NTFS (~100 ns), and ext4 (1 ns).

#### Long paths

InfraSftp handles file paths longer than the historical Windows 260-char
`MAX_PATH` limit (up to ~32,000 chars). Many older clients — including
some popular SFTP tools — fail with "path too long" errors on deep
directory trees and force users to fall back to `rsync`. InfraSftp
declares the long-path opt-in in its application manifest so the OS
treats long paths transparently. Note that NTFS still caps individual
filename segments at 255 chars — that's a filesystem rule and applies
to every Windows app.

### Keyboard shortcuts

| Gesture | Action |
|---|---|
| `Ctrl+N` | New profile |
| `Ctrl+Shift+D` | Disconnect all sessions |
| `F5` | Refresh active panel |
| `Ctrl+W` | Close active tab |
| `F12` *(debug builds)* | Open Avalonia DevTools |

The "active panel" follows focus — click into a panel to make it active.

### Privacy & data

InfraSftp stores everything under a per-user data directory:

- **Windows**: `%APPDATA%\InfraSftp\`
- **Linux**: `$XDG_CONFIG_HOME/InfraSftp/` (defaults to `~/.config/InfraSftp/`)

| File / folder | Purpose |
|---|---|
| `profiles.json` | Connection profile metadata (no passwords) |
| `vault.dat` *(Windows only)* | DPAPI-encrypted passwords. On Linux, passwords live in the desktop keyring via libsecret. |
| `known_hosts.json` | Pinned host-key fingerprints |
| `settings.json` | Theme, hidden-file visibility, force-transfer, telemetry choice, window layout |
| `logs/app-*.log` | Local error log, last 7 days |

**Crash reports are opt-in.** The toggle lives at **Settings → Privacidad
→ Enviar reportes de errores** and is off by default. When enabled, fatal
exceptions are sent to Sentry without your username, IP, or machine
name. When disabled, only the local log under `logs\` is written. Either
way, the local log is always available so you can attach it to a bug
report manually.

### Auto-updates

The installer build self-checks GitHub Releases ~2 seconds after launch.
If a newer version is available a banner appears at the top of the
window: it pre-downloads in the background and offers "Install &
restart" when ready. You can dismiss the banner with the `✕`; the
download still completes silently. Manual checks live in **Settings →
Actualizaciones → Buscar ahora**.

Auto-updates only work in the **installed** flavour on Windows; the
portable ZIP and the Linux RPM do not self-update. On Linux, `dnf
upgrade` against a freshly-downloaded RPM is the supported path.

### Reporting bugs

Open an issue at <https://github.com/juanma-dev/InfraSftp/issues>. If you
have not opted into crash reporting, please attach the latest file from
the `logs/` folder under the data directory listed above
(`%APPDATA%\InfraSftp\logs\` on Windows, `~/.config/InfraSftp/logs/` on
Linux) — it makes diagnosis dramatically faster.

### License

GPL-3.0. See [LICENSE](LICENSE) for the canonical notice and
<https://www.gnu.org/licenses/gpl-3.0.html> for the full license text.

---

## Español

### Instalación en Windows

InfraSftp se distribuye como instalador y como ZIP portable a través de
[GitHub Releases](https://github.com/juanma-dev/InfraSftp/releases).

- **Instalador** (`com.webjuanma.InfraSftp-win-Setup.exe`) — recomendado.
  Se instala en `%LOCALAPPDATA%\InfraSftp` y añade un acceso directo al
  menú Inicio. La actualización automática solo funciona con esta opción.
- **Portable** (`com.webjuanma.InfraSftp-win-Portable.zip`) — descomprime
  y ejecuta `InfraSftp.exe`. No toca el registro; no se autoactualiza.

#### Aviso de SmartScreen al primer arranque

El instalador está firmado con un **certificado autofirmado**. Windows
SmartScreen mostrará "Windows protegió tu PC" la primera vez que lo
ejecutes porque el certificado todavía no tiene reputación ante
Microsoft. Haz clic en **Más información → Ejecutar de todas formas**.
A partir de la segunda ejecución no volverá a avisar. La firma sigue
garantizando que el archivo no se modificó tras compilarse — simplemente
no puede demostrar la identidad del editor ante Microsoft.

### Instalación en Fedora Linux

Con cada release se publica un RPM auto-contenido en
[GitHub Releases](https://github.com/juanma-dev/InfraSftp/releases) con
el nombre `infrasftp-<version>-1.fc.x86_64.rpm`.

```bash
# Opcional pero recomendado: importa la clave de firma para que dnf
# verifique el paquete.
sudo rpm --import https://github.com/juanma-dev/InfraSftp/releases/download/v0.2.0/RPM-GPG-KEY-InfraSftp.asc

sudo dnf install ./infrasftp-0.2.0-1.fc.x86_64.rpm
```

Tras la instalación, ábrelo desde el menú del escritorio (**InfraSftp**)
o ejecuta `infrasftp` en una terminal.

**Dependencias en tiempo de ejecución** (declaradas en el RPM, dnf las
instala automáticamente): `libsecret`, `dejavu-sans-fonts`,
`google-noto-emoji-fonts`. El runtime de .NET 8 va incluido — no hace
falta instalar dotnet por separado.

**Almacenamiento de contraseñas**: las contraseñas se guardan vía
libsecret en el llavero de la sesión activa de escritorio
(gnome-keyring, kwallet5, …). El llavero requiere un demonio Secret
Service en ejecución — eso es el setup estándar en Fedora Workstation /
KDE / Sway. **Las instalaciones mínimas de WSL no traen demonio Secret
Service por defecto**, así que guardar credenciales fallará ahí; el
resto de la app funciona.

**Actualizaciones automáticas**: la versión Linux no se autoactualiza.
Suscríbete al feed de releases de GitHub e instala el RPM nuevo con
`dnf upgrade ./...rpm`.

### Perfiles de conexión

Abre la barra lateral (`☰` → **Nuevo perfil** o `Ctrl+N`). Campos:

| Campo | Notas |
|---|---|
| Nombre | Sólo etiqueta visible — no tiene que coincidir con el host |
| Host | Nombre de host o IP |
| Puerto | Por defecto 22 |
| Usuario | Usuario SSH |
| Método de autenticación | Contraseña **o** Clave privada |

Las contraseñas se guardan en un vault cifrado por plataforma:

- **Windows** — DPAPI en `%APPDATA%\InfraSftp\vault.dat`. El sistema
  operativo vincula la clave de cifrado a tu cuenta de Windows: el
  archivo no puede ser leído por otro usuario de la misma máquina, ni
  copiado a otro equipo.
- **Linux** — libsecret / Secret Service. Cada entrada es un ítem
  separado en el llavero de la sesión activa (gnome-keyring,
  kwallet5, …) etiquetado con `application=com.webjuanma.InfraSftp`. El
  llavero se desbloquea con tu inicio de sesión de escritorio.

#### Primera conexión a un host (TOFU)

La primera vez que conectas a un host, la app te muestra la huella
digital de la clave del servidor y te pide confirmación. Es el modelo
**trust on first use**: la huella queda fijada. Si la clave del servidor
cambiase en el futuro, la app muestra un diálogo rojo de "no coincide"
en lugar de reconectar silenciosamente; es la misma protección que
OpenSSH ofrece con `~/.ssh/known_hosts`.

Las huellas fijadas se guardan en `%APPDATA%\InfraSftp\known_hosts.json`.

### Navegar y transferir

- **Pestañas**: cada panel (izquierdo, derecho) tiene pestañas
  independientes. El panel izquierdo arranca con una pestaña "Local PC"
  fijada en tu carpeta de usuario. Abre pestañas remotas haciendo clic
  en un perfil de la barra lateral.
- **Arrastra una pestaña** entre paneles para moverla.
- **Haz clic en un archivo** de un panel para transferirlo al otro.
- **Doble clic en una carpeta** para entrar; doble clic en `..` para subir.
- **Arrastra y suelta archivos** de un panel al otro para transferirlos.

#### Omitir archivos ya actualizados

Las transferencias recursivas omiten un archivo cuando el origen y el
destino coinciden en **tamaño y fecha de modificación dentro de ±2
segundos**. Es el mismo criterio que `rsync --modify-window=2` usa por
defecto. Para forzar la retransferencia de todos los archivos
independientemente del estado, activa **Configuración → Transferencias
→ Forzar retransferencia**.

La tolerancia de 2 segundos absorbe diferencias de resolución de
timestamp entre FAT (2 s), NTFS (~100 ns) y ext4 (1 ns).

#### Rutas largas

InfraSftp soporta rutas de archivo más largas que el límite histórico
de Windows de 260 caracteres (`MAX_PATH`), llegando hasta ~32.000.
Muchos clientes antiguos — incluyendo algunas herramientas SFTP
populares — fallan con error "ruta demasiado larga" en árboles de
directorios profundos y obligan al usuario a recurrir a `rsync`. La
app declara el opt-in de rutas largas en su manifiesto de aplicación
para que el sistema operativo trate estas rutas de forma transparente.
Ten en cuenta que NTFS sigue limitando cada nombre individual de
archivo a 255 caracteres — eso es una regla del sistema de archivos y
aplica a cualquier app de Windows.

### Atajos de teclado

| Combinación | Acción |
|---|---|
| `Ctrl+N` | Nuevo perfil |
| `Ctrl+Shift+D` | Desconectar todas las sesiones |
| `F5` | Refrescar panel activo |
| `Ctrl+W` | Cerrar pestaña activa |
| `F12` *(builds debug)* | Abrir DevTools de Avalonia |

El "panel activo" sigue al foco — haz clic en un panel para activarlo.

### Privacidad y datos

InfraSftp guarda todo bajo un directorio de datos por usuario:

- **Windows**: `%APPDATA%\InfraSftp\`
- **Linux**: `$XDG_CONFIG_HOME/InfraSftp/` (por defecto `~/.config/InfraSftp/`)

| Archivo / carpeta | Contenido |
|---|---|
| `profiles.json` | Metadatos de los perfiles (sin contraseñas) |
| `vault.dat` *(solo Windows)* | Contraseñas cifradas con DPAPI. En Linux las contraseñas viven en el llavero del escritorio vía libsecret. |
| `known_hosts.json` | Huellas digitales fijadas de servidores |
| `settings.json` | Tema, archivos ocultos, forzar retransferencia, telemetría, layout de ventana |
| `logs/app-*.log` | Log local de errores, últimos 7 días |

**Los reportes de errores son opt-in.** El interruptor está en
**Configuración → Privacidad → Enviar reportes de errores** y viene
desactivado por defecto. Cuando lo activas, las excepciones fatales se
envían a Sentry sin tu nombre de usuario, IP ni nombre de máquina.
Cuando está desactivado, sólo se escribe el log local en `logs\`. En
ambos casos, el log local siempre está disponible para que puedas
adjuntarlo manualmente a un reporte de bug.

### Actualizaciones automáticas

La versión instalada consulta GitHub Releases ~2 segundos después del
arranque. Si hay una versión más reciente, aparece un banner en la parte
superior de la ventana: la descarga corre en segundo plano y al
terminar ofrece "Instalar y reiniciar". Puedes ocultar el banner con
`✕`; la descarga sigue completándose en silencio. Para verificación
manual, **Configuración → Actualizaciones → Buscar ahora**.

La actualización automática sólo funciona en la versión **instalada**
de Windows; la versión portable y el RPM de Linux no se autoactualizan.
En Linux, la vía recomendada es ejecutar `dnf upgrade` contra el RPM
recién descargado.

### Reportar errores

Abre una incidencia en <https://github.com/juanma-dev/InfraSftp/issues>.
Si no has activado el envío de reportes de errores, adjunta por favor
el archivo más reciente de la carpeta `logs/` del directorio de datos
indicado arriba (`%APPDATA%\InfraSftp\logs\` en Windows,
`~/.config/InfraSftp/logs/` en Linux) — acelera mucho el diagnóstico.

### Licencia

GPL-3.0. Ver [LICENSE](LICENSE) para el aviso canónico y
<https://www.gnu.org/licenses/gpl-3.0.html> para el texto completo de la
licencia.
