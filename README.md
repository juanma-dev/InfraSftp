# InfraSftp

A two-pane SFTP client for Windows, inspired by WinSCP and FileZilla. Saved
connection profiles, side-by-side local/remote browsers, drag-and-drop
transfers, rsync-style skip-on-match, and Windows DPAPI password storage.

> **Languages**: [English](#english) · [Español](#español)

---

## English

### Install

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

### Connection profiles

Open the sidebar (`☰` → **New profile** or `Ctrl+N`). Required fields:

| Field | Notes |
|---|---|
| Name | Display label only — does not need to match the host |
| Host | Hostname or IP |
| Port | Default 22 |
| User | SSH username |
| Auth method | Password **or** Private key |

Passwords are stored in the **Windows DPAPI vault** at
`%APPDATA%\InfraSftp\vault.dat`. The OS binds the encryption key to your
Windows user account: the file cannot be read by another user on the same
machine, nor copied to another computer.

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

InfraSftp stores everything under `%APPDATA%\InfraSftp\`:

| File / folder | Purpose |
|---|---|
| `profiles.json` | Connection profile metadata (no passwords) |
| `vault.dat` | DPAPI-encrypted passwords |
| `known_hosts.json` | Pinned host-key fingerprints |
| `settings.json` | Theme, hidden-file visibility, force-transfer, telemetry choice, window layout |
| `logs\app-*.log` | Local error log, last 7 days |

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

Auto-updates only work in the **installed** flavour; the portable ZIP
does not self-update.

### Reporting bugs

Open an issue at <https://github.com/juanma-dev/InfraSftp/issues>. If you
have not opted into crash reporting, please attach the latest file from
`%APPDATA%\InfraSftp\logs\` — it makes diagnosis dramatically faster.

### License

GPL-3.0. See [LICENSE](LICENSE) for the canonical notice and
<https://www.gnu.org/licenses/gpl-3.0.html> for the full license text.

---

## Español

### Instalación

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

### Perfiles de conexión

Abre la barra lateral (`☰` → **Nuevo perfil** o `Ctrl+N`). Campos:

| Campo | Notas |
|---|---|
| Nombre | Sólo etiqueta visible — no tiene que coincidir con el host |
| Host | Nombre de host o IP |
| Puerto | Por defecto 22 |
| Usuario | Usuario SSH |
| Método de autenticación | Contraseña **o** Clave privada |

Las contraseñas se guardan en el **vault DPAPI de Windows** en
`%APPDATA%\InfraSftp\vault.dat`. El sistema operativo vincula la clave
de cifrado a tu cuenta de Windows: el archivo no puede ser leído por
otro usuario de la misma máquina, ni copiado a otro equipo.

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

InfraSftp guarda todo bajo `%APPDATA%\InfraSftp\`:

| Archivo / carpeta | Contenido |
|---|---|
| `profiles.json` | Metadatos de los perfiles (sin contraseñas) |
| `vault.dat` | Contraseñas cifradas con DPAPI |
| `known_hosts.json` | Huellas digitales fijadas de servidores |
| `settings.json` | Tema, archivos ocultos, forzar retransferencia, telemetría, layout de ventana |
| `logs\app-*.log` | Log local de errores, últimos 7 días |

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

La actualización automática sólo funciona en la versión **instalada**;
la versión portable no se autoactualiza.

### Reportar errores

Abre una incidencia en <https://github.com/juanma-dev/InfraSftp/issues>.
Si no has activado el envío de reportes de errores, adjunta por favor
el archivo más reciente de `%APPDATA%\InfraSftp\logs\` — acelera mucho
el diagnóstico.

### Licencia

GPL-3.0. Ver [LICENSE](LICENSE) para el aviso canónico y
<https://www.gnu.org/licenses/gpl-3.0.html> para el texto completo de la
licencia.
