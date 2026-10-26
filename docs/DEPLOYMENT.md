# Deploying VFS365

VFS365 shows Microsoft 365 files (OneDrive, Teams and SharePoint libraries) as a drive and as an entry in Explorer's navigation pane, with each user's own permissions. It is free software under the GNU GPLv3 and uses WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos, https://github.com/winfsp/winfsp.

## What a release contains

| File | Use |
|---|---|
| `VFS365-<version>-x64-setup.exe`, `VFS365-<version>-arm64-setup.exe` | Setup: installs WinFsp (when missing or older) and VFS365. The simplest way to deploy |
| `VFS365-<version>-x64.msi`, `VFS365-<version>-arm64.msi` | VFS365 alone, for when WinFsp is deployed separately |
| `winfsp-<version>.msi` | The unmodified WinFsp installer the setup contains |
| `VFS365-<version>-policy.zip` (`VFS365.admx`, `en-US\VFS365.adml`, laid out as in `PolicyDefinitions`) | Policy templates for Intune (ADMX import) or Group Policy |
| `vfs365-monitoring.html` | Dashboard for the monitoring tables, see [Monitoring](#monitoring) |
| `SHA256SUMS.txt` | SHA-256 checksums of the files above |

Releases are on the repository's GitHub Releases page; the guides, `LICENSE` and `THIRD-PARTY-NOTICES.md` are in the repository (a local `./build.ps1 release` also copies them next to the installers).

All programs are self-contained: no .NET is needed on the devices.

Releases from 0.4.2 on are Authenticode signed with JSolve B.V.'s certificate through Azure Artifact Signing: the setups, the MSIs, and VFS365's programs, DLLs and `vfs365np.dll` inside them. Check a file with `Get-AuthenticodeSignature <file>` (status `Valid`, signer JSolve B.V.). One file inside is unsigned: `winfsp-msil.dll`, WinFsp's .NET layer as its NuGet package ships it (WinFsp's files ship unmodified); with WDAC or App Control, allow it by hash. The WinFsp MSI carries its author's own signature.

Every VFS365 release file also has a build provenance attestation, which shows it was built by this repository's release workflow from `main`:

```
gh attestation verify <file> --repo jflieben/VFS365 --signer-workflow jflieben/VFS365/.github/workflows/release.yml --source-ref refs/heads/main
```

The WinFsp MSI is upstream's, so it has no attestation from VFS365; `SHA256SUMS.txt` lists it, and `build.ps1` pins its SHA-256.

## Prerequisites

1. **Admin consent** for JSolve's VFS365 app, once per tenant: one link, see [APP-REGISTRATION.md](APP-REGISTRATION.md) (delegated permissions only). Or your own app registration, through the `ClientId` policy.
2. Windows 11, x64 or ARM64. Entra joined or registered devices sign in silently; others prompt once.

## Install (.exe)

- **Setup (recommended):** Intune Win32 app or any deployment tool, device context. Install `VFS365-<version>-<arch>-setup.exe /quiet /norestart`, uninstall `VFS365-<version>-<arch>-setup.exe /uninstall /quiet /norestart`. Detection: the VFS365 product in Apps (or the file `C:\Program Files\VFS365\vfs365-agent.exe`).

## Install (2x msi)

- **MSI:** deploy `winfsp-<version>.msi` first (in Intune as a dependency)
- then `msiexec /i VFS365-<version>-<arch>.msi /qn /norestart`; uninstall with `msiexec /x VFS365-<version>-<arch>.msi /qn /norestart`. The MSI refuses to install without WinFsp, and the x64 package and setup refuse ARM64 devices (use the arm64 ones there).

Keep `/norestart` in quiet commands: without it the setup or msiexec restarts the device by itself when a file it replaces is in use (`vfs365np.dll`, which Explorer loads). With it, exit code 3010 means done, with a restart pending (Intune: soft reboot).

Both add a Start menu entry "VFS365" that starts the drive again after it was closed.


## Upgrades

Install the newer setup or MSI. The installer stops running agents with `vfs365.exe machine-stop` (pending changes upload first) and starts them again for every signed-in user with `vfs365.exe machine-start` after the upgrade, so users keep their drive. A `vfs365np.dll` that Explorer has loaded is replaced at the next restart; the old one keeps working until then.

When an upgrade through the setup fails, the setup puts the older version back; its agents start again at the next sign-in (or from VFS365 in the Start menu). A release built again under the same version replaces the installed one instead of installing next to it. A repair (Settings, Apps, or `msiexec /f`) only puts files and registry values back; it runs none of the `vfs365.exe machine-*` steps. These run as SYSTEM without a window; their output is in the MSI log (`msiexec /l*v <log>`).

## Uninstall

Through the setup, the MSI, or Settings, Apps. Before files are removed, `vfs365.exe machine-stop --cleanup` runs as SYSTEM:

- Every signed-in user's agent is asked to stop: it removes its navigation pane entry, uploads pending changes and unmounts. Agents still running after 60 s are ended (their unsaved changes stay journaled).
- Each user's `%LOCALAPPDATA%\VFS365` is removed (downloaded file copies, state, logs). A `staging` folder that still holds unsaved changes is kept, and the uninstall log names it. Links (junctions, symbolic links) in that path, a VFS365 folder owned by another account, and folders more than 64 levels deep are skipped and named in the log: the cleanup runs as SYSTEM and never follows them.
- Explorer entries (navigation pane, drive name and the VFS365 menu) are removed for signed-in users. Users who were signed out are cleaned up at their next sign-in (an Active Setup entry that stays in HKLM); normally their agent already removed its entry at logoff.
- `VFS365.Np` leaves the provider order. A `vfs365np.dll` still loaded by Explorer goes at the next restart.
- The event source `VFS365` is unregistered; events already written stay in the Application log.
- WinFsp stays installed, as other software may use it. Remove it separately if wanted.

## Settings

Policy key `HKLM\SOFTWARE\Policies\JSolve\VFS365` (devices) or `HKCU\...` (users); the ADMX writes these values. Machine policy wins per setting, also for lists: a list in computer policy replaces the user's list, they are not merged.

Settings are read when the agent starts. A change applies at the next sign-in or after Restart in the tray menu; the log and the tray say when a policy changed meanwhile. `MonitoringUrl` and `MonitoringDetail` are the exception: while monitoring runs, a change applies within 5 minutes without notifying users (turning monitoring on when it was off at the start needs a Restart).

Values: on/off settings also take REG_SZ (`1`/`0`, `true`/`false`, `yes`/`no`, `on`/`off`, `enabled`/`disabled`), numbers also REG_QWORD, and choices (`Scope`, `DatabaseFiles`, `ExplorerSearch`, `MonitoringDetail`) only their names, not numbers. Numbers outside their range count as the nearest allowed value; a value that can't be read keeps the default. Both show as a `Warning` in the log and in `vfs365.exe settings`.

| Value | Type | Default | Meaning |
|---|---|---|---|
| `ClientId` | REG_SZ | JSolve's app | Client ID of your own app registration ([APP-REGISTRATION.md](APP-REGISTRATION.md)) |
| `TenantId` | REG_SZ | detected | Forces a tenant; normally the device's Entra tenant or the signed-in account's is used |
| `DriveLetter` | REG_SZ | `None` | `None`: no letter, only the navigation pane entry and `\\VFS365\<user>`. A letter (`M`, `M:` or `M:\`), or `*` for the first free one counting down from Z. A letter in use or a value that isn't valid gives the drive without a letter, with a tray notification and a log line |
| `Label` | REG_SZ | `VFS365` | Name of the drive and of the navigation pane entry |
| `Scope` | REG_SZ | `All` | `All`: OneDrive and Sites folders. `SharePoint` or `OneDrive`: only that, as the root of the drive |
| `IncludedSites` | REG_MULTI_SZ | `*/sites/*`, `*/teams/*` | Site URL patterns to show; `*` matches any characters, patterns match the whole URL |
| `ExcludedSites` | REG_MULTI_SZ | system sites (app catalog, content type hub, Viva Engage and so on) | Site URL patterns to hide; exclusions win; setting it replaces the built-in list, but colleagues' OneDrives (`*/personal/*`) stay hidden |
| `IncludedSiteTemplates` | REG_MULTI_SZ | all | Show only sites with these templates: `GROUP` (team sites with a Microsoft 365 group, including Teams), `SITEPAGEPUBLISHING` (communication sites), `TEAMCHANNEL` (private and shared channel sites), `STS` (team sites without a group). `*` matches any characters. A subsite has its site collection's template; a number after `#` is ignored (search doesn't report it). `vfs365.exe discover` shows each library's template |
| `ExcludedSiteTemplates` | REG_MULTI_SZ | none | Hide sites with these templates, for example `TEAMCHANNEL`; exclusions win. Template and URL rules both apply; pinned locations always show |
| `SharedWithMe` | REG_DWORD | `1` | `0`: no "Shared with me" folder. It shows only with `Scope` All, with the files and folders others shared with the user by name (Microsoft 365 search); files only opened through an "anyone" or organisation link aren't listed. Shared folders work like library folders, shared files directly in it open read-only |
| `NavigationPane` | REG_DWORD | `1` | `0`: no navigation pane entry. With a `DriveLetter` the drive shows under This PC with that letter only; with `None` just the UNC path is left |
| `CacheSizeMB` | REG_DWORD | `2048` | Most space copies of opened files take per user, in MB (at least 100); least recently used go first |
| `OverlayIcons` | REG_DWORD | `1` | `0`: no status icons on the drive's files in Explorer. The VFS365 menu stays |
| `TrayIcon` | REG_DWORD | `1` | `0` hides the tray icon and its notifications (conflict copies, waiting uploads, sign-in problems), and with it Sign in. With the icon, the sign-in window opens only from Sign in (or at a first start); without it, a sign-in that is needed opens the window by itself, once per problem (not within 15 minutes of a cancel) |
| `NotifyUploads` | REG_DWORD | `1` | `0`: no "Uploaded" notification when files that had to wait are uploaded. Conflict copies and waiting uploads always notify |
| `HelpUrl` | REG_SZ | `https://jsolve.nl` | Page the tray menu's Help opens, for example your service desk (http or https) |
| `PinnedLocations` | REG_MULTI_SZ | none | Site, library or folder URLs to show for everyone with access, also when search doesn't find them (Restricted Content Discovery, new sites, the root site). A site URL shows all its libraries; site patterns don't apply |
| `FolderRedirection` | REG_MULTI_SZ | none | Windows' user folders in VFS365 while the drive is mounted: `Documents=OneDrive\Documents`, `\|move` to move their content in. See [Folder redirection and drive letters](#folder-redirection-and-drive-letters) |
| `DriveMappings` | REG_MULTI_SZ | none | Drive letters of their own for folders in VFS365: `Z:=Sites\Finance\Documents`, `\|Finance` for the name in Explorer. See [Folder redirection and drive letters](#folder-redirection-and-drive-letters) |
| `ChangeCheckSeconds` | REG_DWORD | `20` | How often a library in use (last 2 minutes) checks for changes made elsewhere, 5 to 300 s (1 to 4 count as 5); `0` relies on SharePoint's push (often 20 to 30 s). With push working, each check doubles the wait up to 8 times this (160 s) until this PC changes something there; without push, each check that finds nothing doubles it up to 4 times this (80 s). Pauses when the tenant is throttled |
| `DatabaseFiles` | REG_SZ | `Allow` | Multi-user database files (Access, QuickBooks, SQLite): `Allow`, `ReadOnly` (open but never saved) or `Block` (can't be opened). Their locks don't reach other PCs, so two users can both write |
| `ExplorerSearch` | REG_SZ | `Browser` | A search in Explorer's search box on the drive: `Browser` (stopped and opened in SharePoint search in the browser, see [What users get](#what-users-get)), `Stop` (only stopped, with a hint to use the VFS365 menu's "Search in Microsoft 365") or `Allow` (Explorer reads every folder below the one searched, which can get the tenant throttled) |
| `ApiBudgetPerMinute` | REG_DWORD | `600` | SharePoint resource units one user's VFS365 uses per minute at most (at least 60, so 1 to 59 count as 60; `0`: no limit of its own). See [API use](#api-use) |
| `ThrottlePauseMinutes` | REG_DWORD | `15` | After Microsoft throttles, background work (loading ahead, change checks, discovery) waits this many minutes (0 to 240), plus up to half as long again at random so devices don't all resume at once |
| `WalkPrefetchFolders` | REG_DWORD | `10` | Folders listed ahead of an app walking a folder tree that it hasn't opened yet (0 to 200; `0`: off) |
| `ReadAheadFiles` | REG_DWORD | `8` | Small files (up to 4 MB) downloaded ahead of a copy out of the drive that it hasn't opened yet (0 to 64; `0`: off) |
| `BackgroundUploads` | REG_DWORD | `1` | New files upload right after their close, 4 at a time (kept safely on the PC until then). `0`: every new file uploads before its close returns. Saves into existing files always upload before the close returns |
| `RepeatSaveSeconds` | REG_DWORD | `30` | A file closed again within this many seconds of its last upload uploads once at their end (apps that append to a file and close it each time). `0`: every close uploads (0 to 600) |
| `MonitoringUrl` | REG_SZ | none | Table service SAS URL for reports to your own Azure storage. Rows contain user and device names, and error rows file and folder paths (see `MonitoringDetail`). See [Monitoring](#monitoring) |
| `MonitoringDetail` | REG_SZ | `Full` | `Full`: error rows as in the log. `NoPaths`: file and folder paths become a short hash (`[path 1a2b3c4d]`, the same for the same path) and request addresses keep only their host |

The UNC path is `\\VFS365\<user>`, with the Windows user name (`%USERNAME%`).

Drive letter only: set `DriveLetter` to a letter (for example `M`) and `NavigationPane` to `0`. The drive then shows under This PC as `M:`, with no navigation pane entry. The UNC path keeps working.

Accounts: VFS365 works with member accounts in their own tenant, through JSolve's app or your own app registration. Guests (B2B) and accounts synced in from another tenant are refused with a message in the log.

## Folder redirection and drive letters

`FolderRedirection` moves Windows' user folders into VFS365; `DriveMappings` gives folders in VFS365 a drive letter of their own, for apps and habits that expect `Z:\` (for example after OneDriveMapper). One entry per line:

```
Documents=OneDrive\Documents|move
Desktop=OneDrive\Desktop
Pictures=Sites\Home\Documents\%USERNAME%\Pictures
Z:=Sites\Finance\Documents|Finance
H:=Sites\Home\Documents\%UPNNAME%|Home
P:=Sites\Project*\Documents
```

- Folders: `Desktop`, `Documents`, `Downloads`, `Music`, `Pictures`, `Videos`, `Favorites`, `Contacts`, `Links`, `SavedGames`, `SavedSearches`, or any known folder's ID in braces. Documents, Downloads, Music, Pictures and Videos move together with their "This PC" entry.
- Paths are from the drive's root, as the drive shows them (`OneDrive\...`, `Sites\<site>\<library>\...`; with `Scope` OneDrive just the folders in it). `M:\...` on the drive's own letter and `\\VFS365\<user>\...` work too; anything outside VFS365 is refused.
- Placeholders: environment variables (`%USERNAME%`, `%USERDOMAIN%`, `%COMPUTERNAME%`, ...), `%UPN%` (the signed-in account) and `%UPNNAME%` (its part before the @). `*` and `?` in a folder name take the first matching folder by name.
- Folders that don't exist are made inside the library (a user's own folder on a shared library); sites and libraries themselves never are.
- `|move` moves what the local folder holds into the new place when it is redirected; names that exist there stay local, and desktop.ini stays. Without it, the local content stays where it is (visible again when the folder goes back).

Redirection lasts as long as the drive is mounted: after the mount (and after each discovery, every hour and after sleep, for targets that weren't there yet) the folders move into VFS365; at unmount (sign-out, shutdown, `vfs365 signout`, uninstall) they go back to where they were. Windows therefore never starts a session with a desktop that isn't there yet; the next start moves them again. A folder that something else moved (OneDrive's folder backup, a Folder Redirection policy, the user) is left alone and reported in the log and monitoring; remove the other redirection to hand it over. A rule removed from the policy puts its folder back. After a crash the next start takes the folders over again.

Drive letters exist for the user's sign-in only and go at unmount. Explorer names them after the label, or as `<folder> (\\VFS365\<user>\...)`. A letter held by an old SharePoint WebDAV mapping (OneDriveMapper, `net use` to `*.sharepoint.com`), or remembered for the next sign-in (`HKCU\Network`), is taken over and that mapping removed; a letter in use by anything else is left and reported. Explorer shows a letter as connected only with `vfs365np.dll` in the network provider order (the installer puts it there); without it the letter works but shows as disconnected.

## API use

Microsoft limits every app per tenant, for all its users together: 1,250 resource units per minute up to 1,000 licenses, up to 6,250 above 50,000 licenses, plus limits per user ([Microsoft's throttling guidance](https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online#application-throttling)). All VFS365 users of a tenant share the budget of the app they sign in with. Listing a folder costs 2 units; opening a file or checking a library for changes costs 1.

- `ApiBudgetPerMinute` (default 600) is the most one user's VFS365 uses per minute. Normal use stays far below it; it caps peaks such as copying a large folder tree. Background work (loading ahead, change checks, discovery) only uses the second half, so what users wait for goes first. When the budget is used up requests wait; nothing fails.
- When Microsoft throttles anyway (429 or 503), every request waits as long as Microsoft asks, and background work (loading ahead, change checks, discovery) waits `ThrottlePauseMinutes` (default 15; 1 to 1.5 times that, at random). What users open and save goes on.
- `ChangeCheckSeconds` (default 20), `WalkPrefetchFolders` (default 10) and `ReadAheadFiles` (default 8) tune the background work. Uploads of new files are the user's own data and use the whole budget.
- Discovery reads a site's libraries with one request, reads each library's details again once a week (spread over several days), and hides a library only when two runs in a row didn't find it (three when most libraries disappear at once). Change checks slow down while nothing changes (up to 160 s with push notifications working, 80 s without), and libraries only glanced at in Explorer get no change checks.

Lower the budget when many users work at the same time in a tenant with few licenses; raise it in large tenants. The daily statistics (see Monitoring) show what each user used, whether Microsoft throttled, and how long requests waited for the budget: for user actions (opening, saving: users notice this) and for background work (discovery after sign-in, change checks, loading ahead: nobody waits for it). Waits are added up over requests, so four requests waiting 10 s at the same time count 40 s. Background waits are normal pacing; repeated waits for user actions while Microsoft doesn't throttle mean the budget can go up.

## Monitoring

With `MonitoringUrl` set, VFS365 sends reports to your own Azure table storage. Without it, nothing is reported.

| Table | Rows | Sent |
|---|---|---|
| `errors` | Partition: UTC date. `Source` (start, sign-in, upload, delete, conflict copy, discovery, file system, drive letter, security, crash, agent), `Message`, `OccurredAt` | At once; at most 20 an hour per user, the same error once an hour |
| `errors`, source `throttled` | Partition: UTC date of the start. One row per throttling episode (from the first throttled response until 2 minutes without one): start (`OccurredAt`) and end (`ThrottledUntil`), `Responses` (`Status429` and `Status503` separately), `HeldSeconds` (the pause Microsoft asked for, not the longer `ThrottlePauseMinutes` background work waits), `LongestWaitSeconds`, `GraphResponses`, `SharePointResponses`, `BackgroundResponses`, and a message. Not counted as an error | When the episode ends; at most 12 an hour per user |
| `statistics` | Partition: the day (`Day`), the device's local date, unlike the UTC dates of `errors`. One row per device and Windows user: `GraphRequests`, `SharePointRequests`, `ResourceUnits`, `BytesReceived`, `BytesSent`, `Throttled` (throttled responses) and `ThrottledSeconds`, seconds waited for the budget (`BudgetWaitSeconds`; from 0.3.0 also split into `BudgetWaitForegroundSeconds` for what users wait for and `BudgetWaitBackgroundSeconds`; added up over requests), `Errors`, `MinutesRunning` (time asleep doesn't count). From 0.7.0: `ResourceUnitsByHour` (comma-separated, one value per UTC hour from `ResourceUnitsFromHour` on, so devices in other time zones add up), `PeakMinuteResourceUnits` and `PeakMinuteAt` (the device's busiest minute and when it ended, UTC); rows of earlier versions lack them. `User`, `Version` and the device are that day's (with roaming profiles on pooled AVD, the device the day was used on); the RowKey ends in a random suffix | The next day, 1 to 15 minutes after the start or after midnight; a day that doesn't arrive is kept and tried again an hour later, for up to a week after its first failed send |
| `devices` | Partition: device ID. `DeviceName`, `DeviceId`, `EntraDeviceId`, `Users` (signed in at the time), `Version`, `Action` (`install`, `update`, `uninstall`), `OccurredAt` | By the installer, at once |

Rows in `errors` and `statistics` also carry `DeviceName`, `DeviceId`, `EntraDeviceId`, `User` (UPN), `WindowsUser` (domain\name), `TenantId` and `Version`. The device ID is Windows' `MachineGuid`. Error messages contain file and folder paths and the addresses of failed requests; with `MonitoringDetail` set to `NoPaths` they contain a short hash per path instead, and addresses keep only their host. User and device names are in every row; statistics rows show per user in which hours VFS365 was used. `crash` rows come from the agent after it was started again, and from `vfs365-agent.exe` itself when it gave up ("not started again", with the last known UPN); `security` rows when another user on the PC holds the user's `\\VFS365\<user>` path, or when the name VFS365 resolves to an address other than 0.0.0.0 or this PC (see [The name VFS365 on the network](#the-name-vfs365-on-the-network)). Reporting is best effort: nothing waits for it and error rows are not retried. The first failure after a start is written to the local log once, with the reason (for example a missing table or a SAS without the Add permission).

Setup: the [Deploy to Azure button in the README](../README.md#setting-up-monitoring) (template `monitoring/azuredeploy.json`) does steps 1 and 2 and the dashboard's hosting, SAS and CORS, and shows `monitoringUrl` for step 3 among the deployment's outputs; deploying it again into the same resource group renews both SAS URLs. By hand:

1. A storage account (StorageV2) with three tables: `errors`, `statistics`, `devices` (portal: Storage browser, Tables, Add table; or `az storage table create --account-name <account> --name errors`).
2. A shared access signature (portal: Security + networking, Shared access signature): allowed services **Table**, allowed resource types **Object**, allowed permissions **Add**, HTTPS only, an expiry date. Copy the **Table service SAS URL**: `https://<account>.table.core.windows.net/?sv=...&ss=t&srt=o&sp=a&se=...&sig=...`. With Add only, the URL can't read or change rows. Every user on a PC can read the policy, so the agent refuses a SAS that can read, list, update or delete rows or reaches other services (it needs `ss=t`, `srt` including `o`, and `sp` with `a` and at most `c`); the log, `vfs365.exe settings` and `vfs365.exe machine-report` say why.
3. Set `MonitoringUrl` to it as a **computer** policy. Install, update and uninstall reports come from the installer, which only sees computer policy.
4. Renew the SAS before it expires: an expired one sends nothing, and the log says so once.

### Dashboard

`vfs365-monitoring.html` (in each release and in the repository's `monitoring` folder) shows the tables in a browser. It is one file; it reads straight from the storage account and sends nothing anywhere else.

- **What it shows.** What needs attention: crashes, sign-in and upload problems, drive letter conflicts, throttling, older versions, quiet devices, an expiring SAS. Totals compared with the period before, charts per day, and tables of devices, users, errors (grouped by cause), throttling (who, when and how much: episodes with exact times, or per user per day) and installs. Click a row for its details. **Headroom** compares the tenant's resource units with Microsoft's limits per app per tenant (per minute and per 24 hours, by the license count you enter; kept in that browser): the busiest hours and days, use per active user, and how many more users fit with a safety margin you choose. These are estimates: devices report hours, so minute peaks across devices don't show, and each device's busiest minute is its own. Every chart has a table view. The "Error reports" tile counts the rows reported (at most 20 an hour per user) and also shows the errors the agents counted. The last 3 days of statistics are marked as still arriving. Statistics rows for the same day, device and Windows user count once, the newest. Rows are not signed: anyone with the agents' SAS can add rows.
- **Refresh.** Every 5 minutes by default (1 minute to 1 hour, or off). A refresh reads only the last days again.
- **A SAS of its own.** Allowed services Table, resource type **Object**, permission **Read**, HTTPS only, an expiry date. That is all it needs: it reads the rows of the three tables and never lists tables (Container and List add nothing). Paste its Table service SAS URL or its connection string. The dashboard checks the SAS first (expired, not valid yet, missing permissions, more than reading) and explains what Azure refuses.
- **A CORS rule**, so a browser may read the storage account. Under Settings, Resource sharing (CORS), Table service: allowed origins `*`, methods GET and OPTIONS, allowed headers `*`, exposed headers `*`, max age 3600. A page opened from disk (`file://`) sends `Origin: null`, so it needs `*`; to allow one address only, host the page on https and allow that address. Exposed headers are always needed: Azure can split results before 1000 rows, and the page warns when it can't see the headers. Or run:
  `az storage cors add --account-name <account> --services t --methods GET OPTIONS --origins "*" --allowed-headers "*" --exposed-headers "*" --max-age 3600`
- **Addresses.** Only https table endpoints, and http only for Azurite on localhost; the page's Content-Security-Policy lets it connect only to Azure table endpoints (and Azurite).
- **Connecting.** Adding `#connect=<URL-encoded SAS URL>` to the page's address connects at once, for example on a wall screen; the page then removes it from the address bar, but the browser history, a bookmark or a shared link with it still contains the SAS: use it only on a computer you control or in a private window. "Remember" keeps the SAS in that browser; opened from disk, only for the tab (and a SAS an older version stored is removed). To remember it, host the page on https.

Azure Storage Explorer, Excel and Power BI (Azure Table Storage connector) read the tables too.

## What users get

- At logon the drive shows at once with the libraries and folders of the last session; discovery (5 to 90 minutes after sign-in when the last session is less than a week old, then daily while the PC stays signed in) and changes made elsewhere arrive in the background. Big folders show their first entries while the rest loads. A first start without network or sign-in waits and tries again by itself; the tray says what it waits for.
- Changes made in the browser or on another device appear in open Explorer windows, within about 20 s for a library in use (`ChangeCheckSeconds`). A file opened after the PC was idle or asleep, or after a restart, waits up to 3 s for those changes first, so it opens as the current version.
- When Microsoft 365 can't be reached, folders already seen still list and cached files still open.
- Tree walks (copying a folder tree, searching, backups) get up to `WalkPrefetchFolders` folders listed ahead of them, in their own order; the log reports per walk how many were used.
- Searching uses Microsoft 365 (`ExplorerSearch`). Explorer's own search lists every folder below the one searched: about 7 folders a second, 2 resource units each, so one search of a big library can take minutes and a large share of the tenant's budget. When a search shows on the drive and its words stopped changing for 1.5 s (Explorer searches as you type), VFS365 sends the window back to the folder, which ends Explorer's crawl, and opens SharePoint search in the browser with the same words, limited to that folder, library, site or OneDrive. Explorer itself opens the page, so the browser comes to the front, and a notification says the results are in the browser. "Search in Microsoft 365" in the VFS365 menu on folders and "Search Microsoft 365..." in the tray menu ask for the words in a small window. SharePoint search finds words in file contents too; it shows new files after a few minutes and leaves out libraries and sites hidden from search.
- Copies of many small files run at about disk speed: to the drive, new files upload right after they are closed (`BackgroundUploads`); out of the drive, the next small files are downloaded ahead (`ReadAheadFiles`). Measured with 300 small files and Microsoft Defender on: to the drive 3 s (all uploaded after 29 s), out of the drive 21 s with nothing cached.
- Apps that write a file in many small pieces, or append and close it again and again, upload it once when done or once per `RepeatSaveSeconds`, not on every close.
- A tray icon shows the status (up to date, changes waiting to upload, offline, Microsoft 365 limiting requests, sign-in needed) and notifies about conflict copies, uploads that wait, with the cause in plain words, and sign-in problems. Notifications within a few seconds are combined ("27 files are waiting to upload: offline"), and each kind shows at most once a minute; a refused delete says why. Its menu: Status, Sign in (when needed), Show files, Show log, Collect diagnostics (a [support bundle](#troubleshooting)), Refresh (reads all libraries and folders again; once a day, and only when the libraries were read more than an hour ago; a Refresh that fails says so and doesn't count; when greyed out, the item says from when, and pointing at it says why), Restart, Help (`HelpUrl`), and the version, which opens the JSolve website. A click opens the files; the menu also opens from the keyboard (Windows+B, then the menu key or Enter). Menu, tooltips and notifications are in Dutch on Dutch Windows, else in English.
- With the tray icon, VFS365 never shows a sign-in window by itself once it runs: when a silent sign-in fails (a password change, MFA, sign-in frequency), the tray shows "sign-in needed" and a notification; clicking it, or Sign in in the menu, opens the window. Files that can't upload meanwhile wait on the PC. With `TrayIcon` = `0` there is no Sign in, so the window opens by itself, once per problem.
- If the drive can't start (a problem only an admin solves, another user holding `\\VFS365\<user>`), the tray icon stays with the cause, Retry and Show log.
- Right-clicking a file or folder in the drive (the drive letter, `\\VFS365\<user>` and the `DriveMappings` letters) shows a VFS365 submenu, never on other locations. On Windows 11 it is in the first menu when the release is signed: the MSI then installs `vfs365shell.msix`, a package that only gives the menu its identity, and each user's agent registers it at start through Windows PowerShell (`Add-AppxPackage -ExternalLocation`; needs sideloading of trusted apps, allowed by default, and PowerShell not blocked for users). Without it, and on Windows 10, the menu is under "Show more options". Uninstall removes the package for all users. Open in browser, Open in desktop app (Word, Excel and PowerPoint through Office's URI schemes, so they edit the copy in Microsoft 365 and co-authoring works), Copy link (the item's address, for people who already have access: no sharing link is made, no permission changes), Version history (SharePoint's page), Share in browser (SharePoint has no documented address for its share dialog, so this opens the item where its Share button is). On conflict copies also Keep this version and Compare with the original. One item at a time. The entries are per user (HKCU) and go when the drive stops.
- Files in the drive show a status icon in Explorer: online only (cloud), on this device (check), uploading, downloading, or a problem (refused, or failed 3 times in a row). Folders show uploading or a problem when a file in them does. The MSI registers five overlay handlers (" VFS365 1 Problem" to " VFS365 5 OnlineOnly"); Windows uses only the first 15 handlers in name order, so with several sync clients installed (Dropbox, Google Drive) some VFS365 icons may not show. 64-bit apps on ARM64 PCs (emulated x64) show none in their file dialogs. Policy `OverlayIcons` turns them off.
- A conflict copy's notification names who saved the other version and when.
- `vfs365.exe pin <url>` adds a site, library or folder link for the user (`unpin`, `pins` to list); it shows after Restart.
- `vfs365.exe signout` stops the drive and removes local data (cached file copies, folder listings, discovery state; unsaved changes stay); the next start signs in again with the Windows account.

## Data on the device

Per user in `%LOCALAPPDATA%\VFS365`:

| Item | Contents | Protection |
|---|---|---|
| `cache\` | Copies of opened files, one per version | AES-256, key in `cache.key` wrapped by DPAPI (this user only); limited by `CacheSizeMB` |
| `metadata.bin` | Folder listings for a fast start | AES-GCM with the same key |
| `discovery.json` | Libraries shown, their drive IDs, the OneDrive | Plain (site and library names) |
| `discovery-report.json` | What the last discovery left out and why, for `vfs365.exe why` and support bundles | Plain (site and library names) |
| `status.json` | What the running agent reports for `vfs365.exe health`: state, drive letter, account, counts, last error, files waiting to upload | Plain (paths) |
| `support\` | Support bundles (Collect diagnostics, `vfs365.exe support-bundle`), the newest 5 | Plain zip, tokens and secrets removed |
| `pinned.json` | Locations the user added with `vfs365 pin` | Plain (URLs) |
| `staging\` | Unsaved changes until they upload | Plain, deleted after upload |
| `statistics.json` | Usage per day until it is sent to monitoring (only with `MonitoringUrl`) | Plain (counts) |
| `refreshed.txt` | When the user last used Refresh | Plain (a time) |
| `redirection.json` | Folders moved by `FolderRedirection` and where they were, while the drive is mounted (only with that policy) | Plain (paths) |
| `enrolled` | Marker: the device was in Intune at the last start, so leaving Intune wipes the cache | Plain (a time) |
| `agent.log` | Log of the current sign-in, starting with the settings in effect and where each comes from | Plain |
| `logs\` | Logs of earlier sign-ins (`agent-<time of the last line>.log`): kept 7 days, at most 50 files; a log that reaches 10 MB continues in a new file | Plain |

Wiped automatically: cached copies and folder listings on another account or tenant and when the device leaves Intune (MDM unenrollment); a library's copies when it is no longer shown; an item's copies when it is deleted. `vfs365.exe signout` removes the cache, the folder listings, the discovery state (with its report) and `cache.key`. Staging is never wiped.

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

## The name VFS365 on the network

`\\VFS365\<user>` is a local path only while the user's drive runs. Before it starts, after a crash or a refused start, after `vfs365 signout` or after an uninstall, Windows treats it like any other server name: SMB and WebDAV look for a server called VFS365, through DNS and then LLMNR and NetBIOS broadcasts on the local network. Whoever answers for that name gets the user's NTLM response (which can be cracked or relayed) and receives what apps save to remembered paths: Office's recent files, paths stored by legacy apps, a navigation pane entry left behind by a crash. Anyone on the same network can answer a broadcast, and in Active Directory any domain user can create a DNS record that doesn't exist yet.

VFS365's own network provider never asks the network: it checks with WinFsp directly whether the user's drive is mounted. Apps that open the path do go through SMB, so close the gap on the network side:

- **Turn off LLMNR and NetBIOS name resolution.** Group Policy or the Intune settings catalog: Network, DNS Client, **Turn off multicast name resolution** = Enabled, and on Windows 11 **Configure NetBIOS settings** = Disable NetBIOS name resolution (or NetBIOS over TCP/IP off per adapter, for example through DHCP).
- **Own the DNS name.** Create the record `VFS365` yourself in every DNS zone devices search (the AD domain and the DNS suffixes they append), so nobody else can. Point it to `127.0.0.1`: a path that falls through then fails at once on the device itself, and no credentials leave it. An address where nothing listens (for example `192.0.2.1`) also keeps credentials in, but apps wait for the connection to time out before they report an error.
- **Block NTLM for SMB.** Windows 11 24H2: Network, Lanman Workstation, **Block NTLM (LM, NTLM, NTLMv2)** = Enabled (or `Set-SmbClientConfiguration -BlockNTLM $true`), with exceptions for servers that still need NTLM. Older versions: Security Options, **Network security: Restrict NTLM: Outgoing NTLM traffic to remote servers**, with an exception list. Test before rolling out: SMB to servers by IP address or outside the domain uses NTLM.
- **Stop the WebClient service** (WebDAV) where nothing needs it; it also looks for VFS365 and signs in with NTLM.

At each start the agent checks whether the name VFS365 resolves. When it does, the log gets a `Warning:` line and monitoring a `security` row; a record that points to `127.0.0.1` (as above) or `0.0.0.0` isn't reported.

## Multi-session hosts (AVD)

- Every signed-in user runs their own agent and volume; nobody else can open it, and other users' requests are refused before the drive looks anything up. UNC paths are machine-wide, which is why they include the user name. When another user's drive already holds `\\VFS365\<user>`, VFS365 doesn't start, warns in the tray and reports a `security` row to monitoring; typed `\\VFS365` paths only resolve to the user's own drive.
- The stop signal lives in the user's own session, so other users can't block or stop someone's drive.
- Two accounts with the same Windows user name in different domains share `\\VFS365\<name>`, so only one of them can have the drive at a time; the other gets a message naming the account that holds it, instead of a generic security alert.
- Drive letters are per session: with `DriveLetter` set, every user has the same path (for example `M:\`). Set one when apps store file paths that other users open.
- Downloaded file copies go to `%LOCALAPPDATA%\VFS365\cache`, which FSLogix puts in the profile container by default; `CacheSizeMB` bounds it. Excluding `AppData\Local\VFS365\cache` in FSLogix `redirections.xml` keeps profiles smaller at the cost of downloading again in a new session. Keep `AppData\Local\VFS365\staging` (unsaved changes), `cache.key`, `metadata.bin` and `discovery.json` (fast start), and `pinned.json` (the user's pins).

## Health and Intune remediation

`vfs365.exe health` checks the device and the signed-in user's drive: exit code 0 when nothing needs a fix, 1 with the reasons, 2 for a wrong command line. `--json` gives the result as JSON with a one-line `Summary`. `--all-users`, as SYSTEM or an elevated administrator, checks every signed-in session (active or disconnected) instead of the current one.

- Device: WinFsp installed (the version is shown), `WinFsp.Np` and `VFS365.Np` in the network provider order.
- Per user: an agent runs in the session; its status (`%LOCALAPPDATA%\VFS365\status.json`, written when something changes and at least every 5 minutes) is less than 15 minutes old; the drive is mounted (an agent started less than 5 minutes ago may still be starting); no sign-in is needed.
- Shown but not counted: offline, throttled, files waiting to upload, changes kept on the PC, whether the event source is registered.

For Intune Remediations (Devices, Scripts and remediations, Create), use the scripts in the repository's `deploy/intune` folder (Windows PowerShell 5.1 or later):

- Detection `Detect-VFS365.ps1` runs `vfs365.exe health --all-users --json` from `C:\Program Files\VFS365` and prints its summary line, which Intune shows as the detection output. Exit 1 (remediate) when something is unhealthy, VFS365 is missing or health gives no report.
- Remediation `Remediate-VFS365.ps1` runs `vfs365.exe machine-setup` (VFS365.Np back in the provider order) and `vfs365.exe machine-start` (the agent in every signed-in session without one), waits up to a minute for a started agent to mount, and prints the health summary with what the steps did.
- Run this script using the logged-on credentials: **No** (SYSTEM). Run script in 64-bit PowerShell: **Yes**. Enforce script signature check: **No**, unless you sign the scripts. Schedule: hourly or daily.
- What remediation can't fix stays in the output: a user who must sign in (Sign in in the tray), a missing WinFsp (deploy the setup or the WinFsp MSI), a policy that sets the provider order without `WinFsp.Np` or `VFS365.Np` (fix that policy; it would replace the order again).

## Windows Event Log

The agent writes important events to the Application log under source `VFS365`, with the user's SID, so Microsoft Defender for Endpoint, Microsoft Sentinel (Azure Monitor Agent), Splunk and other collectors pick them up. The MSI registers the source (`HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\VFS365`, message file `%SystemRoot%\System32\EventCreate.exe`, whose message for each ID is the event's own text) and removes it at uninstall; without it, as in a development build, nothing is written. Tokens and secrets are removed from the text. The same message is written at most once an hour, and one ID at most 10 times an hour; the next event of that ID says how many were left out.

| ID | Type | Event |
|---|---|---|
| 100 | Information | The drive is mounted (letter, UNC path, Windows user) |
| 101 | Information | The drive stopped and unmounted |
| 102 | Information | The agent restarts (Restart in the tray, another account signed in) |
| 103 | Error | The drive didn't start, for a cause that needs a fix; tried again hourly and at Retry |
| 110 | Error | The agent stopped unexpectedly and is started again (`agent.log` has what Windows recorded) |
| 111 | Error | The agent stopped unexpectedly too often and is not started again |
| 200 | Warning | A sign-in is needed, with the cause in plain words and the AADSTS code |
| 300 | Error | Another account on the PC holds `\\VFS365\<user>`; the drive isn't started |
| 301 | Warning | The name VFS365 resolves on the network |
| 400 | Warning | A file was changed or locked in Microsoft 365; the user's version is saved as a conflict copy |
| 401 | Warning | A file is waiting to upload, with the cause; it stays on the PC and is tried again |
| 402 | Warning | Microsoft 365 refused a delete; the file is back |
| 403 | Error | Changed files were not saved to Microsoft 365 when the drive stopped; they stay on the PC (`Not saved:` in `agent.log`) |
| 404 | Information | Unsaved changes from an earlier run are being uploaded |
| 500 | Warning | A setting can't be used as set: out of range or unreadable, a refused `MonitoringUrl`, a `FolderRedirection` or `DriveMappings` line |
| 501 | Information | Settings changed since the start; they apply after Restart |
| 502 | Warning | A drive letter problem: taken, not valid, or shown as disconnected |
| 503 | Warning | A folder redirection or drive mapping problem |
| 504 | Warning | `VFS365.Np` is missing from the network provider order |
| 600 | Warning | A throttling episode ended: how long, how many responses |
| 700 | Warning | Discovery failed; the libraries of the last run stay |

Read them with `Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'VFS365' }`, or in Event Viewer (Windows Logs, Application, Filter Current Log, source VFS365).

## Troubleshooting

- Support bundle: Collect diagnostics in the tray menu, or `vfs365.exe support-bundle [--out <folder>]`, writes one zip to `%LOCALAPPDATA%\VFS365\support` (never onto the drive) and opens Explorer with it selected; nothing is uploaded. It holds `agent.log` and the two logs before it, the settings with their sources and warnings, how the tenant was chosen and the account last signed in, the last discovery (libraries shown, skipped with the reason, errors, hidden), drive letters and `DriveMappings`, `redirection.json`, the provider order, WinFsp, waiting and unsaved changes with their cause, the health check, and the Windows and VFS365 versions. Tokens, Authorization values, SAS signatures, download links, passwords and keys are removed; copies of files, folder listings (`metadata.bin`), the cache key, the content of unsaved changes and token caches are never in it. User, tenant, site and file names stay: send it to your helpdesk, don't publish it.
- `vfs365.exe why <link or name>` says why a site or library shows on the drive or not: where it shows; the `ExcludedSites`, `IncludedSites` or site template rule that hides it, with the pattern and where the setting comes from; why discovery skipped it (a system or catalog library, a hidden library, one the site owner excluded from offline clients, not a standard document library, a site that failed); a library hidden because search missed it two runs in a row; a pinned location and what it found; or that the last discovery never saw it (sites excluded from search, Restricted Content Discovery: pin it). Give a browser link of a site, library, folder or file (sharing links too) or part of a name. `--check` adds a few calls: whether the user can open it, which of its libraries would show, and whether search returns it. Exit code 0 when it shows, 1 when not.
- Log: `%LOCALAPPDATA%\VFS365\agent.log` (per user, this sign-in; Show log in the tray opens it). It starts with the settings that differ from the defaults and where each comes from (computer or user policy, marked Intune when Intune set it, or the config file); `vfs365.exe settings` shows them all. Earlier sign-ins are in `logs\`, 7 days back.
- `C:\Program Files\VFS365\vfs365.exe settings` shows the effective settings and where each comes from; `vfs365.exe discover` lists the libraries found for the signed-in user (`--audit` also checks what it might miss); `vfs365.exe health` checks the drive (see [Health and Intune remediation](#health-and-intune-remediation)); `vfs365.exe about` shows the version and notices.
- `vfs365.exe whoami` shows the signed-in account, tenant and OneDrive; `whoami --sign-in` shows Windows' sign-in window even when the Windows account would sign in silently, to test the prompt (Conditional Access, MFA).
- `vfs365.exe inspect <path>` shows what Graph has at a path on the volume (`--versions` counts versions); `vfs365.exe watch <folder>` shows push notifications for its library.
- `vfs365.exe machine-report install` sends an install row to the `devices` table, or says why it can't; `vfs365.exe settings` shows the monitoring account (never the SAS) and the API use settings.
- Unsaved changes wait in `%LOCALAPPDATA%\VFS365\staging` and upload at the next start. Changes that were still being written when the PC stopped upload as a `<name> (recovered <time>)` copy next to the file, never into it.
- A file SharePoint refuses (HTTP 400, a name or request it doesn't accept) is not retried: it stays on the PC, is notified once and is listed in a `Not saved:` log line when the drive stops, until it is saved or renamed again. The app gets "The filename, directory name, or volume label syntax is incorrect".
- The tray says what a start waits for: "waiting for the network", "waiting for sign-in" (choose Sign in), or "not started" with the cause (Retry tries again, Show log has the details). A "Security warning" means another user on the PC holds `\\VFS365\<user>`; the drive stays off so nothing is saved there, and monitoring gets a `security` row. A `security` row or `Warning:` line saying the name VFS365 resolves means something on the network answers for it: see [The name VFS365 on the network](#the-name-vfs365-on-the-network).
- Crashes: `vfs365-agent.exe` runs the agent as a child process (`--child`) and starts it again when it crashes, at most 3 times in 15 minutes; after that it stops, logs "not started again", reports that to monitoring and shows a tray icon with Retry and Show log (unless `TrayIcon` is `0`). The log shows "stopped unexpectedly", the exit code and what Windows recorded: the faulting module and exception code, and for .NET exceptions the exception and stack (also in Event Viewer, Application log, sources .NET Runtime and Application Error; read the same way on Windows in any language). Please send those lines with a bug report.
- The log notes sleep and resume, the network going and coming back, the signed-in account and tenant, and setting changes that wait for a Restart.
- "Disconnected Network Drive" (Dutch "Niet-verbonden netwerkstation") on VFS365's letter while the drive works: Explorer labels a network drive by asking Windows' network providers who owns the letter, and shows it as disconnected when none answers or when a remembered mapping claims the letter. The agent checks this at the start and every 5 minutes and logs a `Warning:` line with the cause and the fix:
  - another share remembered on the same letter (`HKCU\Network\<letter>`, from a drive mapping policy, a logon script or `net use /persistent:yes`): remove that mapping from the policy or script, or delete the registry key. Don't use `net use <letter> /delete`: it disconnects VFS365.
  - no provider reports the letter: WinFsp.Np is missing from `HKLM\SYSTEM\CurrentControlSet\Control\NetworkProvider\Order`, often because a policy or another installer replaced the order. Add it back (with VFS365.Np after it), or run `vfs365.exe machine-setup` as an administrator.
  - the letter is gone (removed by another tool, for example `net use * /delete` in a logon script): the drive stays reachable at `\\VFS365\<user>`; Restart in the tray assigns the letter again.
  - Explorer can also keep showing an old state after the agent restarted; F5 in This PC refreshes it.
- Elevated apps ("Run as administrator") don't see VFS365's drive letter: Windows gives an elevated process its own set of drive letters. Use the UNC path `\\VFS365\<user>` there.
- "SharePoint has the library read-only for now" in the log: SharePoint answered `403 serviceReadOnly` ("Database Is Read Only"), usually during maintenance or while a site is moved, and it passes. The change check backs off (up to 30 minutes), saves into that library wait and retry, and the log says when it works again. If it lasts for days, check the site in the SharePoint admin center (a read-only lock or an archived site).
- A file fails to open once with "The volume for a file has been externally altered so that the opened file is no longer valid", and the log has a line "... bytes listed, ... served": Microsoft 365 served the file with another length than it listed. The file changed after the folder was listed, or SharePoint wrote library properties into an Office file as it served it. Opening it again works. Downloads that break off are tried twice more; only when all three attempts fail does the log get an `error: reading <path>` line (which monitoring reports).
- Known WinFsp 2.1 issue: scripts that enumerate deep trees recursively (`Get-ChildItem -Recurse`, .NET `AllDirectories`) can fail with "The network path was not found". Explorer is not affected. Fixed in WinFsp 2026 (2.2), which a later release will bundle.

## Known limitations

- **Searches VFS365 can't see** still read every folder: searches in programs' Open and Save dialogs, and searches in This PC or Home (they include the drive). Use "Search in Microsoft 365" in the VFS365 menu there; in Open and Save dialogs it is in the right-click menu too.
- **Explorer's search filters** that SharePoint doesn't know (`kind:`, `datemodified:`, `size:`) aren't passed on; `ext:` and `type:` become `filetype:`.
- **x64 apps on ARM64 devices** can open and save files on the drive, but typing or pasting a `\\VFS365\<user>` path into their file dialogs or address bars doesn't resolve. Windows loads network providers from System32 into x64 processes too, and VFS365's provider there is ARM64 only; serving both needs an ARM64X build, which its C toolchain (Zig) can't make yet. Use the drive letter or the navigation pane entry in those apps; ARM64 and 32-bit apps aren't affected.
- **`winfsp-msil.dll` is unsigned** in signed releases (see [What a release contains](#what-a-release-contains)): allow it by hash where App Control or WDAC requires signed code.
- **`\\VFS365` on the network** while the drive isn't running: see [The name VFS365 on the network](#the-name-vfs365-on-the-network).
