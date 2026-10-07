# Deploying VFS365

VFS365 shows Microsoft 365 files (OneDrive, Teams and SharePoint libraries) as a drive and as an entry in Explorer's navigation pane, with each user's own permissions. It is free software under the GNU GPLv3 and uses WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos, https://github.com/winfsp/winfsp.

## What a release contains

| File | Use |
|---|---|
| `VFS365-<version>-x64-setup.exe`, `VFS365-<version>-arm64-setup.exe` | Setup: installs WinFsp (when missing or older) and VFS365. The simplest way to deploy |
| `VFS365-<version>-x64.msi`, `VFS365-<version>-arm64.msi` | VFS365 alone, for when WinFsp is deployed separately |
| `winfsp-<version>.msi` | The unmodified WinFsp installer the setup contains |
| `VFS365-<version>-policy.zip` (`policy\VFS365.admx`, `policy\en-US\VFS365.adml`) | Policy templates for Intune (ADMX import) or Group Policy |
| `SHA256SUMS.txt` | Checksums |

Releases are on the repository's GitHub Releases page; the guides, `LICENSE` and `THIRD-PARTY-NOTICES.md` are in the repository (a local `./build.ps1 release` also copies them next to the installers).

All programs are self-contained: no .NET is needed on the devices.

## Prerequisites

1. **Admin consent** for JSolve's VFS365 app, once per tenant: one link, see [APP-REGISTRATION.md](APP-REGISTRATION.md) (delegated permissions only). Or your own app registration, through the `ClientId` policy.
2. Windows 11, x64 or ARM64. Entra joined or registered devices sign in silently; others prompt once.

## Install (.exe)

- **Setup (recommended):** Intune Win32 app or any deployment tool, device context. Install `VFS365-<version>-<arch>-setup.exe /quiet`, uninstall `VFS365-<version>-<arch>-setup.exe /uninstall /quiet`. Detection: the VFS365 product in Apps (or the file `C:\Program Files\VFS365\vfs365-agent.exe`).

## Install (2x msi)

- **MSI:** deploy `winfsp-<version>.msi` first (in Intune as a dependency)
- then `msiexec /i VFS365-<version>-<arch>.msi /qn`. The MSI refuses to install without WinFsp.


## Upgrades
install the newer setup or MSI. Running agents stop cleanly first (pending changes upload) and start again at the next logon. While Explorer has the network provider loaded, replacing it may need a restart (unverified).

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
| `NavigationPane` | REG_DWORD | `1` | `0`: no navigation pane entry. With a `DriveLetter` the drive shows under This PC with that letter only; with `None` just the UNC path is left |
| `CacheSizeMB` | REG_DWORD | `2048` | Most space copies of opened files take per user, in MB (at least 100); least recently used go first |
| `TrayIcon` | REG_DWORD | `1` | `0` hides the tray icon and its notifications (conflict copies, waiting uploads, sign-in problems) |
| `HelpUrl` | REG_SZ | `https://jsolve.nl` | Page the tray menu's Help opens, for example your service desk (http or https) |
| `PinnedLocations` | REG_MULTI_SZ | none | Site, library or folder URLs to show for everyone with access, also when search doesn't find them (Restricted Content Discovery, new sites, the root site). A site URL shows all its libraries; site patterns don't apply |
| `ChangeCheckSeconds` | REG_DWORD | `20` | How often a library in use (last 2 minutes) checks for changes made elsewhere, 5 to 300 s; `0` relies on SharePoint's push (often 20 to 30 s). Pauses when the tenant is throttled |
| `DatabaseFiles` | REG_SZ | `Allow` | Multi-user database files (Access, QuickBooks, SQLite): `Allow`, `ReadOnly` (open but never saved) or `Block` (can't be opened). Their locks don't reach other PCs, so two users can both write |
| `ApiBudgetPerMinute` | REG_DWORD | `600` | SharePoint resource units one user's VFS365 uses per minute at most (at least 60; `0`: no limit of its own). See [API use](#api-use) |
| `ThrottlePauseMinutes` | REG_DWORD | `15` | After Microsoft throttles, background work stops this many minutes (0 to 240) |
| `WalkPrefetchFolders` | REG_DWORD | `10` | Folders listed ahead of an app walking a folder tree that it hasn't opened yet (0 to 200; `0`: off) |
| `ReadAheadFiles` | REG_DWORD | `8` | Small files (up to 4 MB) downloaded ahead of a copy out of the drive that it hasn't opened yet (0 to 64; `0`: off) |
| `BackgroundUploads` | REG_DWORD | `1` | New files upload right after their close, 4 at a time (kept safely on the PC until then). `0`: every new file uploads before its close returns. Saves into existing files always upload before the close returns |
| `RepeatSaveSeconds` | REG_DWORD | `30` | A file closed again within this many seconds of its last upload uploads once at their end (apps that append to a file and close it each time). `0`: every close uploads (0 to 600) |
| `MonitoringUrl` | REG_SZ | none | Table service SAS URL for reports to your own Azure storage. See [Monitoring](#monitoring) |

The UNC path is `\\VFS365\<user>`, with the Windows user name (`%USERNAME%`).

Drive letter only: set `DriveLetter` to a letter (for example `M`) and `NavigationPane` to `0`. The drive then shows under This PC as `M:`, with no navigation pane entry. The UNC path keeps working.

Accounts: VFS365 works with member accounts in their own tenant, through JSolve's app or your own app registration. Guests (B2B) and accounts synced in from another tenant are refused with a message in the log.

## API use

Microsoft limits every app per tenant, for all its users together: 1,250 resource units per minute up to 1,000 licenses, up to 6,250 above 50,000 licenses, plus limits per user ([Microsoft's throttling guidance](https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online#application-throttling)). All VFS365 users of a tenant share the budget of the app they sign in with. Listing a folder costs 2 units; opening a file or checking a library for changes costs 1.

- `ApiBudgetPerMinute` (default 600) is the most one user's VFS365 uses per minute. Normal use stays far below it; it caps peaks such as copying a large folder tree. Background work (loading ahead, change checks, discovery) only uses the second half, so what users wait for goes first. When the budget is used up requests wait; nothing fails.
- When Microsoft throttles anyway (429 or 503), every request waits as long as Microsoft asks, and background work stops for `ThrottlePauseMinutes` (default 15).
- `ChangeCheckSeconds` (default 20), `WalkPrefetchFolders` (default 10) and `ReadAheadFiles` (default 8) tune the background work. Uploads of new files are the user's own data and use the whole budget.

Lower the budget when many users work at the same time in a tenant with few licenses; raise it in large tenants. The daily statistics (see Monitoring) show what each user used and whether Microsoft throttled.

## Monitoring

With `MonitoringUrl` set, VFS365 sends reports to your own Azure table storage. Without it, nothing is reported.

| Table | Rows | Sent |
|---|---|---|
| `errors` | Partition: UTC date. Device name and ID, Entra device ID, user (UPN) and Windows user, tenant, version, source (start, sign-in, upload, conflict copy, discovery, file system, agent), message, time | At once; at most 20 an hour per user, the same error once an hour |
| `statistics` | Partition: day. One row per device and Windows user: Graph and SharePoint requests, resource units, bytes received and sent, times throttled and seconds waited, seconds waited for the budget, errors, minutes running, version | The next day, 1 to 15 minutes after the start or after midnight |
| `devices` | Partition: device ID. Device name, Entra device ID, signed-in users, version, action (`install`, `update`, `uninstall`), time | By the installer, at once |

The device ID is Windows' `MachineGuid`. Errors contain file and folder paths. Reporting is best effort: nothing waits for it and nothing is retried. The first failure after a start is written to the local log once, with the reason (for example a missing table or a SAS without the Add permission).

Setup:

1. A storage account (StorageV2) with three tables: `errors`, `statistics`, `devices` (portal: Storage browser, Tables, Add table; or `az storage table create --account-name <account> --name errors`).
2. A shared access signature (portal: Security + networking, Shared access signature): allowed services **Table**, allowed resource types **Object**, allowed permissions **Add**, HTTPS only, an expiry date. Copy the **Table service SAS URL**: `https://<account>.table.core.windows.net/?sv=...&ss=t&srt=o&sp=a&se=...&sig=...`. With Add only, the URL can't read or change rows.
3. Set `MonitoringUrl` to it as a **computer** policy. Install, update and uninstall reports come from the installer, which only sees computer policy.
4. Renew the SAS before it expires: an expired one sends nothing, and the log says so once.

Read the tables with Azure Storage Explorer, Excel or Power BI (Azure Table Storage connector).

## What users get

- At logon the drive shows at once with the libraries and folders of the last session; discovery and changes made elsewhere arrive in the background. Big folders show their first entries while the rest loads.
- Changes made in the browser or on another device appear in open Explorer windows, within about 20 s for a library in use (`ChangeCheckSeconds`).
- Tree walks (copying a folder tree, searching, backups) get up to `WalkPrefetchFolders` folders listed ahead of them, in their own order; the log reports per walk how many were used.
- Copies of many small files run at about disk speed: to the drive, new files upload right after they are closed (`BackgroundUploads`); out of the drive, the next small files are downloaded ahead (`ReadAheadFiles`). Measured with 300 small files and Microsoft Defender on: to the drive 3 s (all uploaded after 29 s), out of the drive 21 s with nothing cached.
- Apps that write a file in many small pieces, or append and close it again and again, upload it once when done or once per `RepeatSaveSeconds`, not on every close.
- A tray icon shows the status and notifies about conflict copies, uploads that wait (offline, throttled) and sign-in problems. Its menu: Show files, Show log, Restart, Help (`HelpUrl`), JSolve website.
- `vfs365.exe pin <url>` adds a site, library or folder link for the user (`unpin`, `pins` to list); it shows after Restart.
- `vfs365.exe signout` stops the drive in the session, forgets the sign-in and removes the cached data (unsaved changes stay).

## Data on the device

Per user in `%LOCALAPPDATA%\VFS365`:

| Item | Contents | Protection |
|---|---|---|
| `cache\` | Copies of opened files, one per version | AES-256, key in `cache.key` wrapped by DPAPI (this user only); limited by `CacheSizeMB` |
| `metadata.bin` | Folder listings for a fast start | AES-GCM with the same key |
| `discovery.json` | Libraries shown, their drive IDs | Plain (site and library names) |
| `staging\` | Unsaved changes until they upload | Plain, deleted after upload |
| `agent.log` | Log | Plain |

Wiped automatically: everything but staging on another account or tenant and when the device leaves Intune (MDM unenrollment); a library's copies when it is no longer shown; an item's copies when it is deleted.

## Antivirus

VFS365 needs no antivirus exclusions, and the measurements above ran with Microsoft Defender real-time protection on. Don't exclude the drive itself: files people open from Microsoft 365 should be scanned like any other.

What Microsoft Defender does with it by default ([scanning options](https://learn.microsoft.com/en-us/defender-endpoint/configure-advanced-scan-types-microsoft-defender-antivirus)):

- Real-time protection scans files on the drive when they are opened. That reads only what the app reads anyway, from the local copy.
- Full scans skip mapped network drives (`Run full scan on mapped network drives` is off by default), and VFS365's drive letter is per user, which full scans never include. Keep it that way: a full scan of the drive would download everything the user can reach.

Only if `MsMpEng.exe` uses a lot of CPU during large copies, exclude the agent's own working folders, and only for the agent itself. These hold encrypted copies of files (which a scanner can't read anyway) and the plain copies of files being saved, which Defender already scanned through the drive. A contextual exclusion (Defender platform 4.18.2205 or later) keeps them scanned for every other process:

```
C:\Users\*\AppData\Local\VFS365\cache\:{Process:"C:\Program Files\VFS365\vfs365-agent.exe"}
C:\Users\*\AppData\Local\VFS365\staging\:{Process:"C:\Program Files\VFS365\vfs365-agent.exe"}
```

Add them as path exclusions (Intune: Endpoint security, Antivirus, Microsoft Defender Antivirus exclusions, Excluded paths; Group Policy: Microsoft Defender Antivirus, Exclusions, Path Exclusions). Adjust `C:\Users` when profiles live elsewhere. User variables such as `%LOCALAPPDATA%` don't work in Defender exclusions; `*` stands for one folder ([wildcards](https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-antivirus-exclusions-overview#wildcards-in-microsoft-defender-antivirus-exclusions)). Check one with `MpCmdRun.exe -CheckExclusion -Path <path>`.

Avoid:

- Excluding the drive letter. Microsoft advises against excluding mapped drives at all; if a trusted app's folder on the drive must be excluded, use the network path (`\\VFS365\<user>\...`).
- Process exclusions for `vfs365-agent.exe` or `vfs365.exe`: they also switch off attack surface reduction rules and network protection for that process.
- Plain path exclusions of `AppData\Local\VFS365`: users can write there, so anything placed in it would run unscanned.

Other antivirus products: the same idea applies. Leave the drive scanned, and exclude at most the cache and staging folders for the agent process only.

## Multi-session hosts (AVD)

- Every signed-in user runs their own agent and volume; nobody else can open it. UNC paths are machine-wide, which is why they include the user name.
- Drive letters are per session: with `DriveLetter` set, every user has the same path (for example `M:\`). Set one when apps store file paths that other users open.
- Downloaded file copies go to `%LOCALAPPDATA%\VFS365\cache`, which FSLogix puts in the profile container by default; `CacheSizeMB` bounds it. Excluding `AppData\Local\VFS365\cache` in FSLogix `redirections.xml` keeps profiles smaller at the cost of downloading again in a new session. Keep `AppData\Local\VFS365\staging` (unsaved changes), `cache.key`, `metadata.bin` and `discovery.json` (fast start).

## Troubleshooting

- Log: `%LOCALAPPDATA%\VFS365\agent.log` (per user).
- `C:\Program Files\VFS365\vfs365.exe settings` shows the effective settings and where each comes from; `vfs365.exe discover` lists the libraries found for the signed-in user (`--audit` also checks what it might miss); `vfs365.exe about` shows the version and notices.
- `vfs365.exe inspect <path>` shows what Graph has at a path on the volume (`--versions` counts versions); `vfs365.exe watch <folder>` shows push notifications for its library.
- `vfs365.exe machine-report install` sends an install row to the `devices` table, or says why it can't; `vfs365.exe settings` shows the monitoring account (never the SAS) and the API use settings.
- Unsaved changes wait in `%LOCALAPPDATA%\VFS365\staging` and upload at the next start.
- Known WinFsp 2.1 issue: scripts that enumerate deep trees recursively (`Get-ChildItem -Recurse`, .NET `AllDirectories`) can fail with "The network path was not found". Explorer is not affected. Fixed in WinFsp 2026 (2.2), which a later release will bundle.
