# Deploying VFS365

VFS365 shows Microsoft 365 files (OneDrive, Teams and SharePoint libraries) as a drive and as an entry in Explorer's navigation pane, with each user's own permissions. It is free software under the GNU GPLv3 and uses WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos, https://github.com/winfsp/winfsp.

## What a release contains

| File | Use |
|---|---|
| `VFS365-<version>-x64-setup.exe`, `VFS365-<version>-arm64-setup.exe` | Setup: installs WinFsp (when missing or older) and VFS365. The simplest way to deploy |
| `VFS365-<version>-x64.msi`, `VFS365-<version>-arm64.msi` | VFS365 alone, for when WinFsp is deployed separately |
| `winfsp-<version>.msi` | The unmodified WinFsp installer the setup contains |
| `VFS365-<version>-policy.zip` (`policy\VFS365.admx`, `policy\en-US\VFS365.adml`) | Policy templates for Intune (ADMX import) or Group Policy |
| `vfs365-monitoring.html` | Dashboard for the monitoring tables, see [Monitoring](#monitoring) |
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
| `IncludedSiteTemplates` | REG_MULTI_SZ | all | Show only sites with these templates: `GROUP` (team sites with a Microsoft 365 group, including Teams), `SITEPAGEPUBLISHING` (communication sites), `TEAMCHANNEL` (private and shared channel sites), `STS` (team sites without a group). `*` matches any characters. A subsite has its site collection's template; a number after `#` is ignored (search doesn't report it). `vfs365.exe discover` shows each library's template |
| `ExcludedSiteTemplates` | REG_MULTI_SZ | none | Hide sites with these templates, for example `TEAMCHANNEL`; exclusions win. Template and URL rules both apply; pinned locations always show |
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

Lower the budget when many users work at the same time in a tenant with few licenses; raise it in large tenants. The daily statistics (see Monitoring) show what each user used, whether Microsoft throttled, and how long requests waited for the budget: for user actions (opening, saving: users notice this) and for background work (discovery after sign-in, change checks, loading ahead: nobody waits for it). Waits are added up over requests, so four requests waiting 10 s at the same time count 40 s. Background waits are normal pacing; repeated waits for user actions while Microsoft doesn't throttle mean the budget can go up.

## Monitoring

With `MonitoringUrl` set, VFS365 sends reports to your own Azure table storage. Without it, nothing is reported.

| Table | Rows | Sent |
|---|---|---|
| `errors` | Partition: UTC date. Device name and ID, Entra device ID, user (UPN) and Windows user, tenant, version, source (start, sign-in, upload, conflict copy, discovery, file system, drive letter, crash, agent), message, time | At once; at most 20 an hour per user, the same error once an hour |
| `statistics` | Partition: day. One row per device and Windows user: Graph and SharePoint requests, resource units, bytes received and sent, times throttled and seconds waited, seconds waited for the budget (`BudgetWaitSeconds`; from 0.3.0 also split into `BudgetWaitForegroundSeconds` for what users wait for and `BudgetWaitBackgroundSeconds`; added up over requests), errors, minutes running, version | The next day, 1 to 15 minutes after the start or after midnight |
| `devices` | Partition: device ID. Device name, Entra device ID, signed-in users, version, action (`install`, `update`, `uninstall`), time | By the installer, at once |

The device ID is Windows' `MachineGuid`. Errors contain file and folder paths. Reporting is best effort: nothing waits for it and nothing is retried. The first failure after a start is written to the local log once, with the reason (for example a missing table or a SAS without the Add permission).

Setup:

1. A storage account (StorageV2) with three tables: `errors`, `statistics`, `devices` (portal: Storage browser, Tables, Add table; or `az storage table create --account-name <account> --name errors`).
2. A shared access signature (portal: Security + networking, Shared access signature): allowed services **Table**, allowed resource types **Object**, allowed permissions **Add**, HTTPS only, an expiry date. Copy the **Table service SAS URL**: `https://<account>.table.core.windows.net/?sv=...&ss=t&srt=o&sp=a&se=...&sig=...`. With Add only, the URL can't read or change rows.
3. Set `MonitoringUrl` to it as a **computer** policy. Install, update and uninstall reports come from the installer, which only sees computer policy.
4. Renew the SAS before it expires: an expired one sends nothing, and the log says so once.

### Dashboard

`vfs365-monitoring.html` (in each release and in the repository's `monitoring` folder) shows the tables in a browser. It is one file; it reads straight from the storage account and sends nothing anywhere else.

- **What it shows.** What needs attention: crashes, sign-in and upload problems, drive letter conflicts, throttling, older versions, quiet devices, an expiring SAS. Totals compared with the period before, charts per day, and tables of devices, users, errors (grouped by cause) and installs. Click a row for its details. Every chart has a table view.
- **Refresh.** Every 5 minutes by default (1 minute to 1 hour, or off). A refresh reads only the last days again.
- **A SAS of its own.** Allowed services Table, resource types Container and Object, permissions **Read** and **List**, HTTPS only, an expiry date. Paste its Table service SAS URL or its connection string. The dashboard checks the SAS first (expired, not valid yet, missing permissions, more than reading) and explains what Azure refuses.
- **A CORS rule**, so a browser may read the storage account. Under Settings, Resource sharing (CORS), Table service: allowed origins `*` (or where the page is hosted), methods GET and OPTIONS, allowed headers `*`, exposed headers `*`, max age 3600. Exposed headers are needed for days with more than 1000 rows. Or run:
  `az storage cors add --account-name <account> --services t --methods GET OPTIONS --origins "*" --allowed-headers "*" --exposed-headers "*" --max-age 3600`
- **Connecting.** Adding `#connect=<URL-encoded SAS URL>` to the page's address connects at once, for example on a wall screen; that address then contains the SAS. "Remember" keeps the SAS in that browser.

Azure Storage Explorer, Excel and Power BI (Azure Table Storage connector) read the tables too.

## What users get

- At logon the drive shows at once with the libraries and folders of the last session; discovery (5 to 90 minutes after sign-in when the last session is less than a week old) and changes made elsewhere arrive in the background. Big folders show their first entries while the rest loads.
- Changes made in the browser or on another device appear in open Explorer windows, within about 20 s for a library in use (`ChangeCheckSeconds`).
- Tree walks (copying a folder tree, searching, backups) get up to `WalkPrefetchFolders` folders listed ahead of them, in their own order; the log reports per walk how many were used.
- Copies of many small files run at about disk speed: to the drive, new files upload right after they are closed (`BackgroundUploads`); out of the drive, the next small files are downloaded ahead (`ReadAheadFiles`). Measured with 300 small files and Microsoft Defender on: to the drive 3 s (all uploaded after 29 s), out of the drive 21 s with nothing cached.
- Apps that write a file in many small pieces, or append and close it again and again, upload it once when done or once per `RepeatSaveSeconds`, not on every close.
- A tray icon shows the status and notifies about conflict copies, uploads that wait (offline, throttled) and sign-in problems. Its menu: Status, Show files, Show log, Refresh (reads all libraries and folders again; once a day, and only when the libraries were read more than an hour ago; when greyed out, pointing at it says why), Restart, Help (`HelpUrl`), and the version, which opens the JSolve website.
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
| `agent.log` | Log of the current sign-in, starting with the settings in effect and where each comes from | Plain |
| `logs\` | Logs of earlier sign-ins (`agent-<time of the last line>.log`): kept 7 days, at most 50 files; a log that reaches 10 MB continues in a new file | Plain |

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

- Log: `%LOCALAPPDATA%\VFS365\agent.log` (per user, this sign-in; Show log in the tray opens it). It starts with the settings that differ from the defaults and where each comes from (computer or user policy, marked Intune when Intune set it, or the config file); `vfs365.exe settings` shows them all. Earlier sign-ins are in `logs\`, 7 days back.
- `C:\Program Files\VFS365\vfs365.exe settings` shows the effective settings and where each comes from; `vfs365.exe discover` lists the libraries found for the signed-in user (`--audit` also checks what it might miss); `vfs365.exe about` shows the version and notices.
- `vfs365.exe inspect <path>` shows what Graph has at a path on the volume (`--versions` counts versions); `vfs365.exe watch <folder>` shows push notifications for its library.
- `vfs365.exe machine-report install` sends an install row to the `devices` table, or says why it can't; `vfs365.exe settings` shows the monitoring account (never the SAS) and the API use settings.
- Unsaved changes wait in `%LOCALAPPDATA%\VFS365\staging` and upload at the next start.
- Crashes: `vfs365-agent.exe` runs the agent as a child process (`--child`) and starts it again when it crashes, at most 3 times in 15 minutes. The log then shows "stopped unexpectedly", the exit code and what Windows recorded (also in Event Viewer, Application log, sources .NET Runtime and Application Error). Please send those lines with a bug report.
- "Disconnected Network Drive" (Dutch "Niet-verbonden netwerkstation") on VFS365's letter while the drive works: Explorer labels a network drive by asking Windows' network providers who owns the letter, and shows it as disconnected when none answers or when a remembered mapping claims the letter. The agent checks this at the start and every 5 minutes and logs a `Warning:` line with the cause and the fix:
  - another share remembered on the same letter (`HKCU\Network\<letter>`, from a drive mapping policy, a logon script or `net use /persistent:yes`): remove that mapping from the policy or script, or delete the registry key. Don't use `net use <letter> /delete`: it disconnects VFS365.
  - no provider reports the letter: WinFsp.Np is missing from `HKLM\SYSTEM\CurrentControlSet\Control\NetworkProvider\Order`, often because a policy or another installer replaced the order. Add it back (with VFS365.Np after it), or run `vfs365.exe machine-setup` as an administrator.
  - the letter is gone (removed by another tool, for example `net use * /delete` in a logon script): the drive stays reachable at `\\VFS365\<user>`; Restart in the tray assigns the letter again.
  - Explorer can also keep showing an old state after the agent restarted; F5 in This PC refreshes it.
- Elevated apps ("Run as administrator") don't see VFS365's drive letter: Windows gives an elevated process its own set of drive letters. Use the UNC path `\\VFS365\<user>` there.
- "SharePoint has the library read-only for now" in the log: SharePoint answered `403 serviceReadOnly` ("Database Is Read Only"), usually during maintenance or while a site is moved, and it passes. The change check backs off (up to 30 minutes), saves into that library wait and retry, and the log says when it works again. If it lasts for days, check the site in the SharePoint admin center (a read-only lock or an archived site).
- A file fails to open once with "The volume for a file has been externally altered so that the opened file is no longer valid", and the log has a line "... bytes listed, ... served": Microsoft 365 served the file with another length than it listed. The file changed after the folder was listed, or SharePoint wrote library properties into an Office file as it served it. Opening it again works. Downloads that break off are tried twice more; only when all three attempts fail does the log get an `error: reading <path>` line (which monitoring reports).
- Known WinFsp 2.1 issue: scripts that enumerate deep trees recursively (`Get-ChildItem -Recurse`, .NET `AllDirectories`) can fail with "The network path was not found". Explorer is not affected. Fixed in WinFsp 2026 (2.2), which a later release will bundle.
