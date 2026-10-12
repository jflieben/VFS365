# Architecture

## Shape

```
Legacy app / Explorer
        |  Win32 file I/O on \\VFS365\<user> or a drive letter
WinFsp driver (kernel, network mode)
        |
vfs365-agent.exe, one per user session, started at logon
  Drive.WinFsp   file system callbacks to engine calls
  Core           namespace, metadata cache, content cache, upload queue, change feed
  Graph          HTTP, RU governor, retries
  Agent          WAM sign-in, policy, mounts, status
        |
Microsoft Graph (delegated) to SharePoint Online and OneDrive
```

**Not a sync client**: nothing is mirrored up front. Cost scales with what users open, not with library size.

## Projects

| Project | Role |
|---|---|
| src/Vfs365.Core | Engine. Platform neutral, no front-end types |
| src/Vfs365.Graph | Thin Graph and SharePoint REST client, RU governor. Gets tokens from the host, no MSAL |
| src/Vfs365.Drive.WinFsp | WinFsp front end |
| src/Vfs365.Agent | Agent logic: settings, sign-in, commands, Explorer integration |
| src/Vfs365.Cli | `vfs365.exe`, console tool |
| src/Vfs365.Background | `vfs365-agent.exe`, no window, started at logon |
| src/Vfs365.Np | `vfs365np.dll`, network provider in C so typed `\\VFS365` paths resolve |
| installer/ | WiX 5 MSI (`installer/bundle`: setup with WinFsp); policy/ has the ADMX |
| tests/Vfs365.Tests | Unit tests |

A Cloud Files API front end, if ever built, is another `Drive.*` project over the same Core.

## Namespace

```
\\VFS365\<user>  (optionally a drive letter too)
  OneDrive\
  Sites\<site>\<library>\   what discovery finds plus pinned locations, Teams and channel sites included
```

That is `Scope` = `All`. With `SharePoint` (the default) the site folders are the root of the drive; with `OneDrive` the OneDrive root is. Drive letter (default none), name and filters come from policy ([DEPLOYMENT.md](DEPLOYMENT.md)).

One volume per user session. UNC prefixes are machine-wide (a second volume with the same prefix fails with `0x80070050`), so the share is the Windows user name; drive letters are per session. Each volume's security descriptor grants only its user and SYSTEM. When the prefix is taken, `DriveHost` reads the holder's owner: another user's volume (`MountConflictException.OwnedByAnotherUser`) is reported as a security problem and not retried; it would otherwise receive saves from apps that stored `\\VFS365\<user>` paths.

Typed paths: Explorer's address bar, file dialogs and Run resolve `\\server\share` through the network providers' `NPGetResourceInformation`, which WinFsp's provider lacks; file I/O on the same path works regardless. `vfs365np.dll` (`src/Vfs365.Np`) answers it for `\\VFS365` when the volume root's owner is the calling user, and declines everything else; it sits right after WinFsp's provider in the provider order. 32-bit processes load the SysWOW64 copy; x64 processes on ARM64 load none.

- Short names for sites and libraries: Office and Explorer stop near 260 characters, SharePoint allows 400. Name cleanup as in M365AutoLink (`Marketing - Documents` becomes `Marketing`).
- Site and library titles are cleaned up for Windows (`DriveNamespace.SafeName`); item names pass through as they are, since SharePoint already refuses the characters Windows doesn't allow.
- `~$*`, Office temp files and `desktop.ini` stay local and are never uploaded.
- A navigation pane node named after `Label` (default "VFS365", HKCU delegate folder) targets the drive.

## Operation mapping

| Windows | Graph | RU |
|---|---|---|
| First listing of a folder | `GET .../children?$select=...&$top=1000`, paged | 2 per page |
| Open a path in a folder never listed | `GET /drives/{d}/root:/{path}` | 1 |
| Read | `downloadUrl`, then ranged GETs against it; blocks cached | 1 |
| Create, write, close | PUT content up to 4 MiB, else an upload session (5 MiB fragments, multiples of 320 KiB); `conflictBehavior=fail` | 2 |
| Overwrite, rename-over, `ReplaceFile` | Upload to the existing item ID with `If-Match: <eTag>` | 2 |
| Create folder | `POST .../children`, `conflictBehavior=fail` | 2 |
| Delete | `DELETE` (recycle bin, never `permanentDelete`) | 2 |
| Rename, move within a drive | `PATCH` name / parentReference | 2 |
| Move across drives | Copy (async monitor), verify, delete source; presented as a copy | 2+ |
| Set timestamps | `PATCH` fileSystemInfo | 2 |

Error mapping: 404 to `ERROR_FILE_NOT_FOUND` / `ERROR_PATH_NOT_FOUND`, 403 to `ERROR_ACCESS_DENIED`, 409 to `ERROR_FILE_EXISTS`, 423 to `ERROR_FILE_CHECKED_OUT` where the call can still fail, 412 to a conflict copy.

## Start

The agent mounts with what the last run knew: libraries, their drive IDs and the OneDrive from `discovery.json`, and the folder listings the change feeds kept current from `metadata.bin` (sealed). Explorer gets the drive in about 0.1 s without a network call; discovery then runs in the background and new or removed libraries appear live. With a state under a week old it waits a random 5 to 90 minutes first (spreading a tenant's morning sign-ins), then searches and reads the details only of libraries checked more than 48 hours ago (`LibraryEntry.CheckedAt`). Refresh in the tray runs it at once and reads every library's details (`MetadataMaxAge` zero), once a day and only when the last run is more than an hour old (`RefreshRule`); it also marks every cached folder listing stale (`DriveEngine.ExpireListings`), so folders are read again when opened. The first run mounts with the OneDrive and fills in the sites when discovery finishes, and saves the state at once (OneDrive-only drives too), so the next start needs no network. While the agent runs, discovery repeats about every 24 hours (plus up to 2 hours at random; an hour after a failure). Settings are read once at the start; a policy change is noticed within 5 minutes and logged as applying after Restart. A different account at the next discovery restarts the agent cleanly with an empty cache.

A first start that can't reach Microsoft 365 (offline, sign-in not ready) waits and retries with back-off, 15 s up to 5 minutes, and at once when the network returns or the user signs in; the tray says what it waits for. Other start problems keep the tray icon with the cause, Retry and Show log, and retry hourly. A drive letter in use or not valid mounts the drive without a letter (UNC path and navigation pane) and says so.

## Agent process

- `vfs365-agent.exe` started at sign-in is a supervisor (`AgentSupervisor`): it runs the agent as a child (`--child`) and starts it again after a crash (an NTSTATUS error exit; not 0, 1, 2, a kill or Ctrl+C), at most 3 times in 15 minutes and not while the session ends. A restart from the tray starts a new supervisor; the old one ends with its child. After a crash the supervisor logs what Windows recorded (Application Error 1000 and .NET Runtime 1026, read with `wevtutil /f:xml` by event data, so any Windows language), and the new child gets `--restarted <code>` and reports it to monitoring. When the supervisor gives up it reports that itself.
- `agent.log` is written through an append-only handle, one write per line, so the supervisor, the agent and an agent being replaced never overwrite each other's lines. A second agent in a session leaves the running one's log alone.
- Session end: the agent stops on `SessionEnded`, so a cancelled shutdown keeps the drive; while uploads are pending the tray sets a shutdown block reason. Restart from the tray waits, without a time limit, until the old agent has uploaded and unmounted.
- Stop signal: the event `Local\VFS365.Stop` in the user's session; only that user, SYSTEM and Administrators can create objects there. `machine-stop` (SYSTEM, session 0) reaches it as `Global\Session\<id>\VFS365.Stop`. `machine-start` starts the agent again in every active and disconnected session after an upgrade.
- Drive letter check (`DriveLetterReport`): at the start and every 5 minutes, from the agent's own sign-in, it reads the letter's DOS device, `WNetGetConnection` (what Explorer asks), a remembered mapping in `HKCU\Network\<letter>` and the network provider order. Problems are logged with the fix when they change.

## Tray

`Tray.cs` runs plain `Shell_NotifyIcon` on its own thread.

- Icons come from `tray\tray-{light,dark}-{normal,waiting,error}.ico` (made by `tools/New-TrayIcons.ps1`). The set follows the taskbar theme and changes with it; the size matches the system DPI.
- Show files opens the navigation pane entry (`shell:::{CLSID}`), else the drive letter or UNC path. Explorer can't parse `\\VFS365` until it has loaded the network provider.
- Restart starts the same exe with the same arguments plus `--after <pid>`. The new agent waits for the old one to exit, then mounts.
- Other items: status ("Status: up to date", or offline, throttled, waiting for sign-in; the icon's tooltip says "VFS365: up to date"), Show log, Sign in (only when a sign-in is needed), Retry (only before the drive started), Refresh (greyed out with a hint saying why, and the time it works again in the item text: a plain popup window painted with user32 and gdi32, shown on `WM_MENUSELECT`, because standard menus have no tooltips and the common controls tooltip crashed the process on ARM64), Restart, Help (`HelpUrl`), and the version line, which opens the JSolve website. Every item has an access key.
- `NOTIFYICON_VERSION_4`: one Explorer window per double-click; the menu opens at the icon from the keyboard too. `WM_ENDSESSION` is handled.
- Texts are English or Dutch by the Windows display language (`UiText`); log lines and monitoring stay English.
- Notifications are grouped (`NoticeBatcher`): what arrives within 4 s is one toast per kind ("27 files are waiting to upload: offline"), with plain causes (`PlainCause`). Policy `NotifyUploads` mutes "Uploaded".

## Caches and transfers

- Metadata: listings per folder in memory, loaded page by page and readable while they load: the first Graph page has 200 items, the rest 1000, and the file system answers each directory query with what has arrived. Listings the drive's change feed covers stay current without being listed again (re-listed after 24 hours as a safety net, counted from the real fetch time, also for listings restored from `metadata.bin`) and answer lookups however old they are; others show at once when stale and are refreshed in the background, reusing the known folder ID, which reports what changed. Changes the feed applied while a listing loaded or refreshed are replayed onto it when it finishes. At most `MaxCachedItems` (250,000; a listing counts its items plus one) stay in memory: least recently used listings go first, never ones loading or used in the last 2 minutes; expired lookups are pruned every 30 s. The 5th lookup under one unlisted folder within 10 s lists that folder instead. `metadata.bin` is saved only when listings changed (`DriveEngine.MetadataVersion`).
- Tree walks: a walk is 12 folder loads within 10 s that also go two levels down (a copy, search or backup; Explorer opening sibling folders for thumbnails is not one). The engine then loads the walk's next folders inside the walked subtree, depth first (children of the folder just listed, first name on top), 2 at a time, and never more than `WalkPrefetchFolders` (default 10) that the walk hasn't opened yet. The walk lists those folders anyway, so a walk that stops early wastes at most that many listings. Loading ahead stops 10 s after the walker does and pauses after throttling; the log reports per walk how many folders were loaded ahead and how many the walk used.
- Content (`StreamingContent`): downloads into a sparse cache file in 1 MiB blocks. A read waits only for its own blocks; one ranged stream stays up to 8 blocks ahead of a sequential reader, a read elsewhere gets its own stream (at most 3; the one idle longest makes room), idle streams stop. Every stream asks for the listed version (`contentTag`); another version or another length (`ContentChangedException`) fails the read once, the listing takes the item as served, and the next open reads it whole. A download that failed 3 times is dropped, so the next open starts fresh; handles still holding it retry after 10 s. A download read ahead in the background is promoted to foreground priority when a user reads it. `downloadUrl`s come with a `$select`ed item GET and are reused per item version for 2 minutes, in memory only.
- Copies out of the drive (`DriveEngine.ReadAhead.cs`): when 3 files of a drive start being read within 10 s, the next small files (up to 4 MB) of the folder being read are downloaded ahead in listing order, 4 at a time, and never more than `ReadAheadFiles` (default 8) that weren't opened yet. Measured: 300 small files copied out of a cold drive in 21 s instead of 115 s, with the same requests and every file downloaded ahead used.
- Cache: `cache\<drive>\<item>-<version>`, AES-256-GCM in 64 KiB chunks with a per-user key wrapped by DPAPI (`cache.key`): a random nonce per file, each chunk authenticated with the file's drive, item version, length and chunk number, so any range reads on its own and a damaged or altered file (`CacheDamagedException`) is dropped and downloaded again. Layout: content, then the 16-byte tags, then a 24-byte footer. The key marker includes the format, so an older cache is emptied once. Least recently used files go above `CacheSizeMB` (sizes tracked per file); older versions go when a newer one lands. Wiped on `vfs365 signout`, another account or tenant, and Intune unenrollment; a library that disappears from discovery and items the feed reports deleted lose their content. Staging files (unsaved changes) are plain and transient.
- Uploads: a file over 4 MiB whose size is set before it is written (Windows' copy engine does) uploads in 5 MiB fragments while it is written; each write waits while the previous fragment is in flight, so copy progress follows the network and the close sends only the last fragment. Rewriting bytes already sent falls back to a whole upload on close.
- Journal: a record next to each staging file with unsaved changes; the next start re-uploads them into the original item (If-Match, so a server change since then gives a conflict copy).

## Change feed

`DriveEngine.Changes.cs`:

- A drive gets a feed on real use (a subfolder, a deeper path, a file open, a write, a second look at its top 10 s or more later), not when Explorer only glances at a library's top: `GET /drives/{id}/root/delta?token=latest` returns a delta link for "from now on" without listing anything; the baseline time is taken when it answers.
- Reading it returns changed items with their parent's ID; they are applied to cached listings by item ID (adds, edits, deletes, moves), through an item ID index per listing. Each change is also checked against the folder IDs of cached listings: a folder deleted, renamed or moved elsewhere drops its cached subtree even when its parent isn't cached, and two listings claiming one folder ID are both dropped. Echoes of the engine's own uploads change nothing. An expired link (410) drops the drive's cache and starts over; a catch-up longer than 10 pages for a drive with fewer cached listings than that starts over too.
- Wake-ups: a socket.io push channel per drive used in the last 10 minutes (`GET .../root/subscriptions/socketIo`, Engine.IO 3 over a WebSocket; a notification carries no details, so it triggers a delta read, 3 s plus up to 10 s at random after it, joining further notifications, and at least 30 s apart per drive). Without push: on use every 15 s and in the background every 60 s while active. With push: a safety read every 15 minutes.
- Hot libraries: a drive used in the last 2 minutes is also read every `ChangeCheckSeconds` (default 20 s), so a change shows within about 20 s even when SharePoint's push is slow. With push up, each empty read in a row doubles the interval (20, 40, 80, 160 s); a change or a local write resets it. Paused while background work is paused after throttling.
- Failed reads back off per drive from 1 to 30 minutes (pushes and hot checks wait too); the log says what went wrong once per problem and again when reads work. Meanwhile listings are read again when opened (`ListingTtl`). A `403` with `serviceReadOnly` ("Database Is Read Only": SharePoint maintenance or a site move) is `RemoteError.ReadOnly`: writes into such a library wait and retry, and failed uploads and deletes back off from 30 s to 15 minutes.
- Changes reach Windows through WinFsp's `Notify` (Explorer refreshes, `ReadDirectoryChangesW` watchers see them). Paths are sent upper-cased, matching the names WinFsp keeps when no normalized name is returned.
- Push latency is SharePoint's: 7 to 31 s measured from a change elsewhere to the notification, usually about 25 s, often two notifications per change.

## Discovery

M365AutoLink's user-version method, proven in production:

1. `GET /me/drive` gives the OneDrive URL; dropping `-my` from its host gives the tenant's SharePoint root.
2. SharePoint Search, security-trimmed to the user: `GET <root>/_api/search/query?querytext='contentclass:STS_List_DocumentLibrary -Path:"https://<tenant>-my.sharepoint.com*"'&sortlist='docid:ascending'&trimduplicates=false&rowlimit=500&selectproperties='...'&Properties='EnableMultiGeoSearch:true'&ClientType='VFS365'`, paged in a stable order with `IndexDocId>` the last DocId (row-number paging when a page has no DocId) until a page has fewer than 500 rows. Colleagues' OneDrives are left out; multi-geo tenants get every geo. SharePoint-audience token.
3. Per site: include/exclude URL wildcards and site templates (policy), then `<web>/_api/site` for `WriteLocked` / `ReadOnly`.
4. Per site, when 2 or more of its libraries need reading: `<web>/_api/web/lists?$filter=BaseType eq 1` answers all of them at once (and refreshes the others for free); a library missing from that answer, or a 403/404 on the web, falls back to `<web>/_api/lists/GetById('<listId>')`, retried on the site collection URL on 404. Skipped: `Hidden`, `BaseTemplate` other than 101, `IsCatalog`, `IsSystemList`, system libraries by internal name (`EntityTypeName`: `SiteAssets`, `Style Library`, `FormServerTemplates` and so on, the same in every site language), M365AutoLink's template feature IDs, `ExcludeFromOfflineClient` (admin opted the library out of offline clients). Never by title: titles are localized. `ForceCheckout` libraries show read-only.
5. Library key `siteId|webId|listId`. The Graph drive is resolved lazily on first open (`/sites/{host},{siteId},{webId}/lists/{listId}/drive`) and cached.

Differences from M365AutoLink: no item-count limits (nothing is synced), and removal means hiding the folder. A library is hidden only when 2 runs in a row didn't find it (`DiscoveryState.Misses`), so one incomplete search answer doesn't wipe its cached files. The deletion circuit breaker stays: when a search page fails, or the result shrinks by more than 40% against the last run, nothing is hidden that run and no misses count. Libraries hidden by policy (site URL patterns and site templates) or now excluded by the library rules don't count: they go at once, whatever their share. Access is enforced by SharePoint anyway, so a stale folder only costs a 403. The last run's state is matched by tenant ID, so a user moved to another geo keeps it.

Cost: site and library metadata are cached on the device and reused for 48 hours per library, so a routine run is the search pages plus lookups for new libraries and those checked more than 48 hours ago (measured: 1 search call, 1.4 s; a full refresh is about 90 calls). A library read for the first time gets a check time up to 24 hours back (fixed per library), and a run re-reads at most half the sites (at least 10) with expired metadata, oldest first, so the re-checks spread over several days. Exclusions are cached the same way (`DiscoveryState.Exclusions`) and re-checked with the metadata, so a hidden library shown again appears within two days. Pinned URLs that resolve to nothing are remembered for the metadata age (`EmptyPins`). SharePoint publishes no RU cost for REST search. `vfs365 discover --audit` checks recall against site search, followed sites and hubs.

Not found by search: sites excluded from search, Restricted Content Discovery sites, sites newer than the index, the root site (outside the default include patterns), and other tenants (guests can't search the host tenant).

Pinned locations cover these within the tenant (other tenants are out of scope): URLs from policy `PinnedLocations` and `vfs365 pin <url>` (`%LOCALAPPDATA%\VFS365\pinned.json`).

- The web is the longest path prefix that answers `_api/web`, never above `/sites/x`. Non-JSON answers count as "not a web": SharePoint returns a page for paths below a page.
- A site URL adds its document libraries; a library, folder or view URL adds the list `GetList` finds at the longest prefix.
- Site templates come with each search hit (`SiteTemplate`, the site collection's; no configuration number), so template rules cost no calls.
- Pinned libraries pass the library rules but not the site patterns or templates, show under their site, reuse metadata for 48 hours and keep their last result when a lookup fails.

## Throttling

Per app per tenant, shared by every VFS365 user in that tenant (values in `src/Vfs365.Graph/ResourceUnits.cs`):

| Licenses | RU per minute | RU per 24 h |
|---|---|---|
| up to 1,000 | 1,250 | 1,200,000 |
| 1,001 to 5,000 | 2,500 | 2,400,000 |
| 5,001 to 15,000 | 3,750 | 3,600,000 |
| 15,001 to 50,000 | 5,000 | 4,800,000 |
| over 50,000 | 6,250 | 6,000,000 |

Per user: 3,000 requests per 5 minutes, shared with the user's other apps. On 429 or 503 every request pauses and `Retry-After` is honoured, clamped to 0 to 5 minutes, plus 0 to 50% at random; when the pause ends, waiting requests go one by one (foreground 100 ms apart, background 500 ms), so a tenant's devices don't all come back at once. Reads are retried on 500, 502 and 504 with jittered back-off; writes (PUT, POST, PATCH, DELETE) only on 429 or 503 with `Retry-After`, since a write that failed with a 5xx may have happened (a retry would give a false conflict). `RateLimit-Remaining` at or below 20% of `RateLimit-Limit` holds background requests until `RateLimit-Reset`, and `x-ms-resource-unit` replaces the estimate, when SharePoint sends them. User-Agent `ISV|JSolve|VFS365/<version>`. JSON batches of up to 20, each sub-request priced on its own.

Own budget (`RequestBudget`): every request is priced by method and URL (`ResourceUnits.Estimate`: listings and SharePoint REST 2, item and delta reads 1, writes 2, pre-authenticated transfer URLs 0) and taken from a per-user token bucket of `ApiBudgetPerMinute` units (default 600) that holds one minute's worth. Work marked background (`RequestPriority`: change feed, lookahead, refreshes, discovery, push setup) only takes from the upper half, so a request a user waits for gets units first; work a user joins (a download read ahead, then opened) is promoted through its `PriorityHolder`. After a 429 or 503 every background request waits `ThrottlePauseMinutes` (default 15) in the client; foreground requests only wait for `Retry-After`. Resource units in the statistics count every attempt sent, as Microsoft counts them.

Tokens go only over https on the default port to Microsoft Graph and the tenant's own SharePoint hosts (`TrustedHosts`: root, `-my` and multi-geo locations); any other URL, for example from a pin, is refused before a token is requested.

## Monitoring

`Monitoring.cs`: with policy `MonitoringUrl` (a table service SAS URL) the agent adds entities to the customer's tables over the Table REST API (`POST /<table>?<sas>`, JSON, `Prefer: return-no-content`): `errors` at once (rate-limited), `statistics` per day from counters kept in `statistics.json` (`UsageStatistics`, fed every minute from `M365Client.Usage`), and `devices` from the installer (`vfs365.exe machine-report` as SYSTEM). Fire and forget on a separate `HttpClient` with a 15 s timeout; no retries, except that a day's statistics stay in `statistics.json` until a send succeeds (tried again an hour later); the first failure is logged once. The SAS is never logged, only the account. `MonitoringDetail=NoPaths` replaces file and folder paths in error rows with `[path <hash>]` and keeps only the host of URLs. Sources include `security` (another user's volume holds this user's UNC path) and the supervisor's own "not started again" crash row.

## Identity

MSAL.NET with the WAM broker as a multi-tenant public client, JSolve's app unless policy names the customer's own: silent SSO with the Windows account, CAE (`cp1`): a 401 with an `insufficient_claims` challenge is retried once with the claims. Two audiences: Graph for files, SharePoint for discovery.

Cached tokens are returned without a lock; silent sign-in takes one lock per audience. The background agent never opens a sign-in window by itself while it runs: a request that needs one fails at once (and for 30 s without trying again), sets `SignInProblem` with plain words, and the tray offers Sign in. A window opens only from the tray's Sign in, a click on the notification, or a first start without saved state, never while a lock is held, one at a time; after a cancel, nothing opens by itself for 15 minutes. The account and tenant are logged at the first token and on a change.

Only member accounts in their own tenant: a token whose account's home tenant differs from the token's tenant (a guest, or an account synced in from another tenant) is refused.

Tenant: forced by policy or config, else the device's Entra join or registration, else `organizations` and the sign-in decides. Nothing tenant-specific is configured by default; the SharePoint host comes from the OneDrive URL. Delegated scopes only ([APP-REGISTRATION.md](APP-REGISTRATION.md)); tenants on default consent settings need admin consent (MC1097272). WAM needs an interactive session, so the agent runs per user, not as a service.

## Data safety rules

- Acknowledge a write only when it is durably queued.
- Delete server content only on an explicit user delete, never because a local file is missing or an I/O call failed.
- Upload saves to the existing item ID with `If-Match`. A mismatch gives a conflict copy, never an overwrite. Conflict names carry the time to the second and a counter when taken.
- Flush before acknowledging: staging files are flushed before a deferred or background close returns, the journal is forced to disk before it is renamed into place, and an application's flush reaches the disk (`FlushFileBuffers`).
- Only the volume's user and SYSTEM get in: the volume's security descriptor grants only them, and before any lookup the file system checks the requester's token (from WinFsp's request) and refuses others, so another user on the host can't probe names or spend the user's budget. If the token can't be read the check fails open and WinFsp's own descriptor check decides. The network provider only vouches for `\\VFS365\<user>` when the volume root's owner is the caller.
- Multi-user database files (`.accdb`, `.mdb`, `.qbw`, SQLite) can be read-only or blocked by policy `DatabaseFiles`: byte-range locks don't cross machines. Default `Allow`.

## Write path

Write-through, built in `DriveEngine.Writes.cs`. Application save patterns reach SharePoint as one new version of the same item:

- A file being changed gets a local staging copy (from the content cache). Reads and writes go to it.
- When the handle that wrote closes, the upload runs before the close returns. A close can't report an error, so a 412 or 423 there ends in a conflict copy next to the file.
- New files (`BackgroundUploads`, on by default) are the exception: they are journaled when created, so their close returns at once and they upload right after, 4 at a time. Copying 300 small files to the drive: 3 s instead of 171 s, all uploaded after 29 s.
- A file closed again within `RepeatSaveSeconds` (default 30) of its last upload uploads once, at the end of that time: an app that opens, appends and closes a log uploads it once per window instead of on every append.
- The write gate orders changes to local state. Every transfer lets it go (uploads, the download that stages a file for writing, rename uploads and replaces), so other files go on; operations on the affected paths wait for it. A file written to while it uploaded uploads again; each staged file has a reader/writer lock so a write in progress always ends before its upload is judged unchanged.
- Handles follow their content (`DriveEngine.Follow`): through renames, into the item a save became (new files too), and into the conflict copy after a conflict, never into the colleague's version. Writes to a file deleted or replaced meanwhile fail with `STATUS_FILE_INVALID`.
- A failed upload never ends unscheduled: it retries with back-off (sign-in problems included) and notifies once. A 404 or 410 on the existing item (deleted in Microsoft 365 while edited) uploads the file anew under its name, or as a conflict copy when the name is taken.
- A delete Microsoft 365 refuses (locked, or no permission to delete) shows the file again and notifies; one refused item never holds up other work.
- After a crash, a journaled file is compared with the server item before it is uploaded again; the journal is rewritten whenever its base or path changes.
- Folders the drive creates are known to be empty, so files created in them need no lookups.
- A rename of a staged file onto a real name is the commit point of Office-style saves: the content goes into the item that owns the destination, and a 412 or 423 fails the rename, so the application reports it.
- Deletes, and renames of a file to a temp name, stay local for a 10 s settle window. A save that lands on that path in the meantime goes into the original item (Word renames the original away, Excel deletes it). Unclaimed renames are undone, deletes are applied.
- Temp and lock files never upload: names starting with `~`, ending in `.tmp`, Excel's 8-hex-digit names, `desktop.ini`, `Thumbs.db`.
- Moves between libraries return "not same device": Explorer then copies and deletes itself.
- Memory-mapped writes arrive as paging I/O, often with no close after them: the file uploads 3 s after the last one.
- Not yet: client timestamps are not sent, a user renaming a file to `.tmp` sees it revert.

## Open design questions
- Metadata store format (SQLite or own).
- Which Win32 errors legacy apps handle best when offline or signed out.
- Cross-drive moves in Explorer: allow as copy plus delete, or refuse.
