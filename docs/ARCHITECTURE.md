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

One volume per user session. UNC prefixes are machine-wide (a second volume with the same prefix fails with `0x80070050`), so the share is the Windows user name; drive letters are per session. Each volume's security descriptor grants only its user and SYSTEM.

Typed paths: Explorer's address bar, file dialogs and Run resolve `\\server\share` through the network providers' `NPGetResourceInformation`, which WinFsp's provider lacks; file I/O on the same path works regardless. `vfs365np.dll` (`src/Vfs365.Np`) answers it for `\\VFS365` and declines everything else; it sits right after WinFsp's provider in the provider order. 32-bit processes load the SysWOW64 copy; x64 processes on ARM64 load none.

- Short names for sites and libraries: Office and Explorer stop near 260 characters, SharePoint allows 400. Name cleanup as in M365AutoLink (`Marketing - Documents` becomes `Marketing`).
- Names that are illegal on Windows are encoded reversibly.
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

The agent mounts with what the last run knew: libraries, their drive IDs and the OneDrive from `discovery.json`, and the folder listings the change feeds kept current from `metadata.bin` (sealed). Explorer gets the drive in about 0.1 s without a network call; discovery then runs in the background and new or removed libraries appear live. With a state under a week old it waits a random 5 to 90 minutes first (spreading a tenant's morning sign-ins), then searches and reads the details only of libraries checked more than 48 hours ago (`LibraryEntry.CheckedAt`). Refresh in the tray runs it at once and reads every library's details (`MetadataMaxAge` zero), once a day and only when the last run is more than an hour old (`RefreshRule`); it also marks every cached folder listing stale (`DriveEngine.ExpireListings`), so folders are read again when opened. The first run mounts with the OneDrive and fills in the sites when discovery finishes.

## Agent process

- `vfs365-agent.exe` started at sign-in is a supervisor (`AgentSupervisor`): it runs the agent as a child (`--child`) and starts it again after a crash (an NTSTATUS error exit; not 0, 1, 2, a kill or Ctrl+C), at most 3 times in 15 minutes and not while the session ends. A restart from the tray starts a new supervisor; the old one ends with its child. After a crash the supervisor logs what Windows recorded (Application Error 1000 and .NET Runtime 1026, read with `wevtutil`), and the new child gets `--restarted <code>` and reports it to monitoring.
- Drive letter check (`DriveLetterReport`): at the start and every 5 minutes, from the agent's own sign-in, it reads the letter's DOS device, `WNetGetConnection` (what Explorer asks), a remembered mapping in `HKCU\Network\<letter>` and the network provider order. Problems are logged with the fix when they change.

## Tray

`Tray.cs` runs plain `Shell_NotifyIcon` on its own thread.

- Icons come from `tray\tray-{light,dark}-{normal,waiting,error}.ico` (made by `tools/New-TrayIcons.ps1`). The set follows the taskbar theme and changes with it; the size matches the system DPI.
- Show files opens the navigation pane entry (`shell:::{CLSID}`), else the drive letter or UNC path. Explorer can't parse `\\VFS365` until it has loaded the network provider.
- Restart starts the same exe with the same arguments plus `--after <pid>`. The new agent waits for the old one to exit, then mounts.
- Other items: status ("Status: up to date"; the icon's tooltip says "VFS365: up to date"), Show log, Refresh (greyed out with a hint saying why: a plain popup window painted with user32 and gdi32, shown on `WM_MENUSELECT`, because standard menus have no tooltips and the common controls tooltip crashed the process on ARM64), Restart, Help (`HelpUrl`), and the version line, which opens the JSolve website.

## Caches and transfers

- Metadata: listings per folder in memory, loaded page by page and readable while they load: the first Graph page has 200 items, the rest 1000, and the file system answers each directory query with what has arrived. Listings the drive's change feed covers stay current without being listed again; others show at once when stale and are refreshed in the background, which reports what changed.
- Tree walks: a walk is 12 folder loads within 10 s that also go two levels down (a copy, search or backup; Explorer opening sibling folders for thumbnails is not one). The engine then loads the walk's next folders inside the walked subtree, depth first (children of the folder just listed, first name on top), 2 at a time, and never more than `WalkPrefetchFolders` (default 10) that the walk hasn't opened yet. The walk lists those folders anyway, so a walk that stops early wastes at most that many listings. Loading ahead stops 10 s after the walker does and pauses after throttling; the log reports per walk how many folders were loaded ahead and how many the walk used.
- Content (`StreamingContent`): downloads into a sparse cache file in 1 MiB blocks. A read waits only for its own blocks; one ranged stream stays up to 8 blocks ahead of a sequential reader, a read elsewhere gets its own stream, idle streams stop. `downloadUrl`s are fetched per stream and never stored.
- Copies out of the drive (`DriveEngine.ReadAhead.cs`): when 3 files of a drive start being read within 10 s, the next small files (up to 4 MB) of the folder being read are downloaded ahead in listing order, 4 at a time, and never more than `ReadAheadFiles` (default 8) that weren't opened yet. Measured: 300 small files copied out of a cold drive in 21 s instead of 115 s, with the same requests and every file downloaded ahead used.
- Cache: `cache\<drive>\<item>-<version>`, AES-256 in counter mode with a per-user key wrapped by DPAPI (`cache.key`), so any byte range reads and writes on its own. Least recently used files go above `CacheSizeMB`; older versions go when a new one lands. Wiped on `vfs365 signout`, another account or tenant, and Intune unenrollment; a library that disappears from discovery and items the feed reports deleted lose their content. Staging files (unsaved changes) are plain and transient.
- Uploads: a file over 4 MiB whose size is set before it is written (Windows' copy engine does) uploads in 5 MiB fragments while it is written; each write waits while the previous fragment is in flight, so copy progress follows the network and the close sends only the last fragment. Rewriting bytes already sent falls back to a whole upload on close.
- Journal: a record next to each staging file with unsaved changes; the next start re-uploads them into the original item (If-Match, so a server change since then gives a conflict copy).

## Change feed

`DriveEngine.Changes.cs`:

- A drive gets a feed when first used: `GET /drives/{id}/root/delta?token=latest` returns a delta link for "from now on" without listing anything.
- Reading it returns changed items with their parent's ID; they are applied to cached listings by item ID (adds, edits, deletes, moves; a renamed folder drops its cached subtree). Echoes of the engine's own uploads change nothing. An expired link (410) drops the drive's cache and starts over.
- Wake-ups: a socket.io push channel per drive used in the last 10 minutes (`GET .../root/subscriptions/socketIo`, Engine.IO 3 over a WebSocket; a notification carries no details, so it triggers a delta read). Without push: on use every 15 s and in the background every 60 s while active. With push: a safety read every 15 minutes.
- Hot libraries: a drive used in the last 2 minutes is also read every `ChangeCheckSeconds` (default 20 s), so a change shows within about 20 s even when SharePoint's push is slow. Paused for 15 minutes after any 429 or 503.
- Failed reads back off per drive from 1 to 30 minutes (pushes and hot checks wait too); the log says what went wrong once per problem and again when reads work. Meanwhile listings are read again when opened (`ListingTtl`). A `403` with `serviceReadOnly` ("Database Is Read Only": SharePoint maintenance or a site move) is `RemoteError.ReadOnly`: writes into such a library wait and retry, and failed uploads and deletes back off from 30 s to 15 minutes.
- Changes reach Windows through WinFsp's `Notify` (Explorer refreshes, `ReadDirectoryChangesW` watchers see them). Paths are sent upper-cased, matching the names WinFsp keeps when no normalized name is returned.
- Push latency is SharePoint's: 7 to 31 s measured from a change elsewhere to the notification, usually about 25 s, often two notifications per change.

## Discovery

M365AutoLink's user-version method, proven in production:

1. `GET /me/drive` gives the OneDrive URL; dropping `-my` from its host gives the tenant's SharePoint root.
2. SharePoint Search, security-trimmed to the user: `GET <root>/_api/search/query?querytext='contentclass:STS_List_DocumentLibrary'&trimduplicates=false&rowlimit=500&startrow=<n>&selectproperties='Title,Path,ListId,SiteId,WebId,SPWebUrl,SPSiteUrl,SiteName'`, paged until a page has fewer than 500 rows. SharePoint-audience token.
3. Per site: include/exclude URL wildcards (policy), then `<web>/_api/site` for `WriteLocked` / `ReadOnly`.
4. Per library: `<web>/_api/lists/GetById('<listId>')`, retried on the site collection URL on 404. Skipped: `Hidden`, `BaseTemplate` other than 101, `IsCatalog`, `IsSystemList`, system libraries by internal name (`EntityTypeName`: `SiteAssets`, `Style Library`, `FormServerTemplates` and so on, the same in every site language), M365AutoLink's template feature IDs, `ExcludeFromOfflineClient` (admin opted the library out of offline clients). Never by title: titles are localized. `ForceCheckout` libraries show read-only.
5. Library key `siteId|webId|listId`. The Graph drive is resolved lazily on first open (`/sites/{host},{siteId},{webId}/lists/{listId}/drive`) and cached.

Differences from M365AutoLink: no item-count limits (nothing is synced), and removal means hiding the folder. The deletion circuit breaker stays: when a search page fails, or the result shrinks by more than 40% against the last run, nothing is hidden that run. Libraries hidden by policy (site URL patterns and site templates) don't count: they go whatever their share. Access is enforced by SharePoint anyway, so a stale folder only costs a 403.

Cost: site and library metadata are cached on the device with their static exclusions and reused for 48 hours per library, so a routine run is the search pages plus lookups for new libraries and those checked more than 48 hours ago (measured: 1 search call, 1.4 s; a full refresh is about 90 calls). SharePoint publishes no RU cost for REST search. `vfs365 discover --audit` checks recall against site search, followed sites and hubs.

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

Per user: 3,000 requests per 5 minutes, shared with the user's other apps. On 429 or 503 every request pauses and `Retry-After` is honoured. User-Agent `ISV|JSolve|VFS365/<version>`. JSON batches of up to 20, each sub-request priced on its own.

Own budget (`RequestBudget`): every request is priced by method and URL (`ResourceUnits.Estimate`: listings and SharePoint REST 2, item and delta reads 1, writes 2, pre-authenticated transfer URLs 0) and taken from a per-user token bucket of `ApiBudgetPerMinute` units (default 600) that holds one minute's worth. Work marked background (`RequestPriority`: change feed, lookahead, refreshes, discovery, push setup) only takes from the upper half, so a request a user waits for gets units first. After a 429 or 503 background work also stops for `ThrottlePauseMinutes` (default 15).

## Monitoring

`Monitoring.cs`: with policy `MonitoringUrl` (a table service SAS URL) the agent adds entities to the customer's tables over the Table REST API (`POST /<table>?<sas>`, JSON, `Prefer: return-no-content`): `errors` at once (rate-limited), `statistics` per day from counters kept in `statistics.json` (`UsageStatistics`, fed every minute from `M365Client.Usage`), and `devices` from the installer (`vfs365.exe machine-report` as SYSTEM). Fire and forget on a separate `HttpClient` with a 15 s timeout; no retries; the first failure is logged once. The SAS is never logged, only the account.

## Identity

MSAL.NET with the WAM broker as a multi-tenant public client, JSolve's app unless policy names the customer's own: silent SSO with the Windows account, CAE (`cp1`): a 401 with an `insufficient_claims` challenge is retried once with the claims, silently or with a sign-in. Two audiences: Graph for files, SharePoint for discovery.

Only member accounts in their own tenant: a token whose account's home tenant differs from the token's tenant (a guest, or an account synced in from another tenant) is refused.

Tenant: forced by policy or config, else the device's Entra join or registration, else `organizations` and the sign-in decides. Nothing tenant-specific is configured by default; the SharePoint host comes from the OneDrive URL. Delegated scopes only ([APP-REGISTRATION.md](APP-REGISTRATION.md)); tenants on default consent settings need admin consent (MC1097272). WAM needs an interactive session, so the agent runs per user, not as a service.

## Data safety rules

- Acknowledge a write only when it is durably queued.
- Delete server content only on an explicit user delete, never because a local file is missing or an I/O call failed.
- Upload saves to the existing item ID with `If-Match`. A mismatch gives a conflict copy, never an overwrite.
- Multi-user database files (`.accdb`, `.mdb`, `.qbw`, SQLite) can be read-only or blocked by policy `DatabaseFiles`: byte-range locks don't cross machines. Default `Allow`.

## Write path

Write-through, built in `DriveEngine.Writes.cs`. Application save patterns reach SharePoint as one new version of the same item:

- A file being changed gets a local staging copy (from the content cache). Reads and writes go to it.
- When the handle that wrote closes, the upload runs before the close returns. A close can't report an error, so a 412 or 423 there ends in a conflict copy next to the file.
- New files (`BackgroundUploads`, on by default) are the exception: they are journaled when created, so their close returns at once and they upload right after, 4 at a time. Copying 300 small files to the drive: 3 s instead of 171 s, all uploaded after 29 s.
- A file closed again within `RepeatSaveSeconds` (default 30) of its last upload uploads once, at the end of that time: an app that opens, appends and closes a log uploads it once per window instead of on every append.
- The write gate orders changes to local state. Uploads let it go while they transfer, so other files go on; operations on the same file wait for its upload. A file written to while it uploaded uploads again.
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
