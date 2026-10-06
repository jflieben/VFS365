# Deploying VFS365

VFS365 shows Microsoft 365 files (OneDrive, Teams and SharePoint libraries) as a drive and as an entry in Explorer's navigation pane, with each user's own permissions. It is free software under the GNU GPLv3 and uses WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos, https://github.com/winfsp/winfsp.

## What a release contains

| File | Use |
|---|---|
| `VFS365-<version>-x64-setup.exe`, `VFS365-<version>-arm64-setup.exe` | Setup: installs WinFsp (when missing or older) and VFS365. The simplest way to deploy |
| `VFS365-<version>-x64.msi`, `VFS365-<version>-arm64.msi` | VFS365 alone, for when WinFsp is deployed separately |
| `winfsp-<version>.msi` | The unmodified WinFsp installer the setup contains |
| `policy\VFS365.admx`, `policy\en-US\VFS365.adml` | Policy templates for Intune (ADMX import) or Group Policy |
| `LICENSE`, `THIRD-PARTY-NOTICES.md` | GPLv3 and the licences of included components |
| `DEPLOYMENT.md`, `APP-REGISTRATION.md`, `SHA256SUMS.txt` | This guide, the app registration steps, checksums |

All programs are self-contained: no .NET is needed on the devices.

## Prerequisites

1. **Admin consent** for JSolve's VFS365 app, once per tenant: one link, see [APP-REGISTRATION.md](APP-REGISTRATION.md) (delegated permissions only). Or your own app registration, through the `ClientId` policy.
2. Windows 11, x64 or ARM64. Entra joined or registered devices sign in silently; others prompt once.

## Install

- **Setup (recommended):** Intune Win32 app or any deployment tool, device context. Install `VFS365-<version>-<arch>-setup.exe /quiet`, uninstall `VFS365-<version>-<arch>-setup.exe /uninstall /quiet`. Detection: the VFS365 product in Apps (or the file `C:\Program Files\VFS365\vfs365-agent.exe`).
- **MSI:** deploy `winfsp-<version>.msi` first (in Intune as a dependency), then `msiexec /i VFS365-<version>-<arch>.msi /qn`. The MSI refuses to install without WinFsp.
- VFS365 goes to `Program Files\VFS365` and `vfs365-agent.exe` starts at each user's logon. Users see the drive at their next sign-in.
- The network provider `vfs365np.dll` goes to System32 and SysWOW64, registered as `VFS365.Np` right after WinFsp's provider. It makes typed or pasted `\\VFS365\...` paths open in Explorer, file dialogs and Run. Explorer loads it at its next start (sign-in).
- Upgrades: install the newer setup or MSI. Running agents stop cleanly first (pending changes upload) and start again at the next logon. While Explorer has the network provider loaded, replacing it may need a restart (unverified).

## Uninstall

Through the setup, the MSI, or Settings, Apps. Before files are removed, `vfs365.exe machine-stop --cleanup` runs as SYSTEM:

- Every signed-in user's agent is asked to stop: it removes its navigation pane entry, uploads pending changes and unmounts. Agents still running after 60 s are ended (their unsaved changes stay journaled).
- Each user's `%LOCALAPPDATA%\VFS365` is removed (downloaded file copies, state, logs). A `staging` folder that still holds unsaved changes is kept, and the uninstall log names it.
- Explorer entries (navigation pane and drive name) are removed for signed-in users; agents remove their own entry at logoff, so signed-out users have none.
- `VFS365.Np` leaves the provider order. A `vfs365np.dll` still loaded by Explorer goes at the next restart.
- WinFsp stays installed, as other software may use it. Remove it separately if wanted.

## Settings

Policy key `HKLM\SOFTWARE\Policies\JSolve\VFS365` (devices) or `HKCU\...` (users); the ADMX writes these values. Machine policy wins.

| Value | Type | Default | Meaning |
|---|---|---|---|
| `ClientId` | REG_SZ | JSolve's app | Client ID of your own app registration ([APP-REGISTRATION.md](APP-REGISTRATION.md)) |
| `TenantId` | REG_SZ | detected | Forces a tenant; normally the device's Entra tenant or the signed-in account's is used |
| `DriveLetter` | REG_SZ | `None` | `None`: no letter, only the navigation pane entry and `\\VFS365\<user>`. A letter, or `*` for the first free one counting down from Z |
| `Label` | REG_SZ | `VFS365` | Name of the drive and of the navigation pane entry |
| `Scope` | REG_SZ | `SharePoint` | `SharePoint` or `OneDrive`: only that, as the root of the drive. `All`: OneDrive and Sites folders |
| `IncludedSites` | REG_MULTI_SZ | `*/sites/*`, `*/teams/*` | Site URL patterns to show; `*` matches any characters, patterns match the whole URL |
| `ExcludedSites` | REG_MULTI_SZ | system sites (app catalog, content type hub, Viva Engage and so on) | Site URL patterns to hide; exclusions win; setting it replaces the built-in list |
| `NavigationPane` | REG_DWORD | `1` | `0` shows the drive under This PC only; with `DriveLetter` `None` that leaves just the UNC path |

The UNC path is `\\VFS365\<user>`, with the Windows user name (`%USERNAME%`).

## Multi-session hosts (AVD)

- Every signed-in user runs their own agent and volume; nobody else can open it. UNC paths are machine-wide, which is why they include the user name.
- Drive letters are per session: with `DriveLetter` set, every user has the same path (for example `M:\`). Set one when apps store file paths that other users open.
- Downloaded file copies go to `%LOCALAPPDATA%\VFS365\cache`, which FSLogix puts in the profile container by default and which has no size limit yet. Excluding `AppData\Local\VFS365\cache` in FSLogix `redirections.xml` keeps profiles small at the cost of downloading again in a new session. Keep `AppData\Local\VFS365\staging`: it holds unsaved changes.

## Troubleshooting

- Log: `%LOCALAPPDATA%\VFS365\agent.log` (per user).
- `C:\Program Files\VFS365\vfs365.exe settings` shows the effective settings and where each comes from; `vfs365.exe discover` lists the libraries found for the signed-in user; `vfs365.exe about` shows the version and notices.
- Unsaved changes wait in `%LOCALAPPDATA%\VFS365\staging` and upload at the next start.
