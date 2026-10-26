# Changelog

## 0.7.1

- New default: `Scope` All, so the drive shows OneDrive and Sites (and Shared with me) at its root. Without a `Scope` policy, paths change from `M:\Finance\Documents` to `M:\Sites\Finance\Documents`, also in `DriveMappings`, `FolderRedirection` and paths apps remember. Set `Scope` to `SharePoint` to keep the earlier layout.
- A "Shared with me" folder at the root (Scope All): files and folders others shared with you by name. Shared folders work like library folders (read-only when you may only view them); shared files directly in it open read-only (Open in desktop app edits them in Microsoft 365). Files you only opened through an "anyone" or organisation link aren't listed. Policy `SharedWithMe` (default on). `vfs365 shared` lists what it shows.
- Windows 11: the VFS365 menu is in the first right-click menu instead of under "Show more options", and only on the drive's files and folders. Signed releases only: a small package gives the menu its identity and each user's agent registers it at start.
- Status icons in Explorer on the drive's files: online only, on this device, uploading, downloading, or a problem. Folders show uploading or a problem when a file in them does. Policy `OverlayIcons` (default on) turns them off.

## 0.7.0

Result of a performance review, and new tools for users, helpdesks and admins.

New
- Right-click a file or folder on the drive for a VFS365 menu (Windows 11: Show more options): Open in browser, Open in desktop app (Word, Excel and PowerPoint edit the copy in Microsoft 365, so co-authoring works), Copy link (for people who already have access; no sharing link is made), Version history and Share in browser.
- Conflict copies name who saved the other version and when. On a conflict copy the menu offers Keep this version (it becomes a new version of the original; the colleague's save stays in its history) and Compare.
- `vfs365 support-bundle` and Collect diagnostics in the tray: one zip with logs, settings, discovery and drive state for the helpdesk, kept on the PC. Tokens, keys and SAS signatures are removed; cached content never goes in.
- `vfs365 health [--json] [--all-users]` with exit codes, and Intune Remediations scripts in `deploy/intune`.
- `vfs365 why <link or name> [--check]` explains why a site or library shows on the drive or not.
- Events in the Windows Application log (source VFS365) for sign-in problems, security warnings, conflicts, waiting changes, setting problems, crashes and throttling, for Defender, Sentinel or Splunk. The installer registers the source.
- Monitoring: a Headroom tab shows how close the tenant runs to SharePoint's per-app limits and how many more users fit; statistics rows carry RU per hour and the busiest minute.

Faster
- Opening, creating and deleting files no longer waits while another file's slow operation runs, and reads of files still downloading no longer hold up the rest of the drive.
- A download's final rename and a folder rename over files still uploading return at once; the uploads go on. Word and Excel saves still wait for their upload, on purpose.
- Uploads use larger parts on fast connections; folders with many items list, refresh and delete with far fewer requests; push notifications for libraries nobody looks at cost no reads.
- The monitoring dashboard loads and refreshes with fewer requests and much less memory, and draws nothing in a hidden tab.

Saving and data safety
- An upload whose answer got lost is found by its hash and never becomes a conflict copy or a second upload.
- A delete never takes a colleague's newer version along.
- A file several programs write at once uploads when the last one closes it.
- Sites and libraries with the same title keep their folder names whatever order discovery finds them in.
- Uninstalling, or a crash before the drive mounted, puts redirected Windows folders back.

Reliability
- Sign-in calls that Windows doesn't answer give up after 30 seconds; problems no sign-in window solves are shown once and retried later.
- Offline, files and folders opened before answer at once instead of after a timeout.
- Empty new files upload at their close, not at unmount.

## 0.6.1

- Monitoring in one step: a Deploy to Azure button (README, Setting up monitoring) creates the storage account and tables, hosts the dashboard on the account's static website with CORS for that address only, and hands out the devices' `MonitoringUrl` and a ready dashboard link. Deploying again renews both.

## 0.6.0

- Folder redirection (policy `FolderRedirection`): Decide if Windows' user folders (Desktop, Documents, Downloads, Pictures and the others, or any known folder by ID) move into VFS365 while the drive is mounted, and back at unmount. Paths take environment variables, `%UPN%` and `%UPNNAME%`, and wildcards (`Sites\Project*\Documents`); missing folders are made inside the library; `|move` moves the local content in. Folders moved by something else (OneDrive's folder backup, a Folder Redirection policy) are left alone and reported.
- Drive letters for folders (policy `DriveMappings`): `Z:=Sites\Finance\Documents|Finance` gives a library or folder a letter of its own, as OneDriveMapper did, with the same placeholders and wildcards. A letter still held or remembered by a SharePoint WebDAV mapping (OneDriveMapper, `net use`) is taken over. The network provider reports these letters to Explorer as connected.

## 0.5.1

- The installer runs its machine steps without console windows and not on a repair; a rebuilt release of the same version replaces the installed one.

## 0.5.0

Result of a second full review of the code, and a new sign-in.

Sign-in
- VFS365 signs in through Windows' own Web Account Manager, called directly. Microsoft's authentication library and its broker runtime (msalruntime.dll, which may not be redistributed) are no longer included. Sign-in works as before: silently with the Windows account, a window only from Sign in in the tray or at the start. `vfs365 whoami --sign-in` shows the sign-in window, to test it.
- With the tray icon off (TrayIcon 0), a needed sign-in opens the window by itself, once per problem.

Saving and data safety
- After your own save, a colleague's later version is no longer hidden for an hour; your next save builds on what you read, or ends in a conflict copy.
- Files named like temp files (starting with ~, ending in .tmp, or 8 digits such as 20261024) save like any other file when they exist in Microsoft 365, and new ones upload when still there 5 minutes after their close. Before, their saves stayed on the PC and were lost at the next start. Office's own temp and lock files still never upload.
- A cached file keeps the length it was served with, also after a restart: Office files that SharePoint added properties to no longer show as damaged.
- A folder renamed elsewhere is noticed even when only a folder below it was opened, so opens and saves can't land in the renamed folder.
- A file opened after the drive was idle, after sleep or right after a start is the version now in Microsoft 365.
- Renaming or moving a folder takes files still waiting to upload along.
- Word saves of a file with changes not uploaded yet, and backup copies made during a save, can no longer delete or move the saved document.
- A metadata change in SharePoint (a column, a label, a flow) no longer turns a save into a conflict copy.
- After a crash, changes that were still being written upload as a "(recovered)" copy next to the file instead of into it.
- A download no longer mixes two versions when a colleague saves the same size meanwhile.
- When Microsoft 365 can't be reached, files and folders opened before keep opening.

Reliability
- A connection that stops answering no longer hangs the drive, folder listings, change checks, discovery or downloads: they fail after a minute without progress and are tried again. Slow connections finish their uploads: parts get smaller and resume where they stopped.
- A failed or incomplete library search no longer leaves the drive without SharePoint libraries for a day; it is tried again within the hour, within minutes on a first start. A start after an interrupted first start reads the libraries at once.
- Deleting many files no longer holds up saving: deletes run in the background.
- The agent restarts after any unexpected exit; when it gives up, a tray icon offers Retry and Show log.
- Notifications: at most one per kind a minute. Refused deletes say why in plain words.
- Policy values written as text (yes, on, enabled) or as QWORD are read; unreadable ones keep the default and are named in the log.

API use
- Library details are checked weekly instead of every two days. Change checks slow down in busy libraries, push reconnects are spread out, and a folder loading in the background is served first when someone waits for it.

Security
- The network provider no longer asks the network about \\VFS365 when the drive isn't mounted. DEPLOYMENT.md explains how to keep that name from resolving on the network; the agent warns (log and monitoring) when it does.
- The agent refuses a monitoring SAS that can read, change or delete rows, since every user can read it from the policy. Add-only SASes keep working.
- The installer runs its machine steps without console windows and not on a repair; a rebuilt release of the same version replaces the installed one.
- Uninstall cleans up very deep folder trees and does the machine-wide steps first; stopping agents signals only each session's own agent.
- Releases are built and signed in separate jobs, only from main, and every signature is checked before publishing.

Monitoring
- Statistics are sent as the device they were used on (pooled AVD with roaming profiles), get a random row key, and are retried for a week. Changes to MonitoringUrl and MonitoringDetail apply without a restart.
- Dashboard: changing the connection or period during a load no longer mixes data; today's throttling counts; the last 3 days are marked as still arriving; keyboard access and contrast are better; duplicate rows are left out. The dashboard's SAS needs only Read. Opened from disk, it no longer stores the SAS beyond the tab.

## 0.4.2 (2026-10-22)

- Signed binaries!

## 0.4.1 (2026-10-15)

- Monitoring: a day's statistics carry the signed-in account and the version of that day. Before, an agent that sent them before its first sign-in of the day (statistics go out 1 to 15 minutes after the start) left the account out, and after an update the old day was labelled with the new version.
- Throttling is reported per episode (from the first throttled response until 2 quiet minutes): when it started and ended, how many responses, how long requests were held back, and whether it hit SharePoint or Graph, background work or user actions. The log gets the same line.
- Dashboard: a Throttling tab shows who got throttled, when and how much: episodes with exact times (agents from 0.4.1), or per user per day from the daily statistics. The Throttled tile opens it, the insight names the users most affected, and user and device details list their throttling. Throttling rows are not counted as errors.
- Dashboard: a PC rebuilt from an image (AVD hosts get a new Windows installation, and with it a new machine ID) counts as one device, found by its name; its details list the installations. Charts and totals start at the first report when monitoring began within the period, and errors are compared with the period before only when there was one. Yesterday's statistics, which devices send during today, show lighter and "still arriving". A host that reports two versions on one day counts as the newer. Insights name the user behind most reports and explain a cancelled sign-in, and a note counts statistics that came without the signed-in account.

## 0.4.0 (2026-10-12)

Result of a full review of the code (security, data safety, reliability, API use, user experience).

Saving and data safety
- A save that fails for any reason, sign-in problems included, is retried with back-off until it uploads; you are told once. Before, some failures (a file deleted in Microsoft 365 meanwhile, no access, sign-in needed) were never retried.
- A file deleted in Microsoft 365 while you edited it is saved again under its own name, or as a conflict copy when the name is taken meanwhile. Conflict copies get unique names (seconds, and a number when needed).
- A file that can't be deleted in Microsoft 365 (locked, or you may edit but not delete it) shows again with a notification. Before, it held up every other pending upload until the agent stopped.
- A program that keeps a file open after a conflict copy keeps writing into the copy, never into the colleague's version; reads after a save show the saved content.
- A file changed in Microsoft 365 while it was being read is no longer stored as a mix of two versions; the read fails once and opening it again gives the new version.
- A folder renamed, moved or deleted elsewhere is noticed even when its parent folder was never opened, so saves and deletes can't land in the wrong folder. Changes made elsewhere while a large folder is being read are no longer missed.
- Unsaved changes are flushed to disk before a close returns, and programs that flush a file get it written to disk. After a crash, files that had already uploaded are not uploaded again as conflict copies.
- Saves and other changes are no longer repeated automatically after a server error, which could make false conflict copies.
- Deleting a temp copy during a Word save can no longer delete the saved document.

Security
- Uninstall no longer follows links (junctions) in user profiles when it removes cached data as SYSTEM; links and folders owned by another account are skipped and named in the uninstall log. Before, a user could make the uninstall delete files elsewhere.
- Multi-session hosts: other users' requests to someone's drive are refused before the drive looks anything up; VFS365 doesn't start, warns and reports when another user's drive holds `\\VFS365\<user>`, and typed `\\VFS365` paths only resolve to your own drive; other users can no longer block or stop someone's drive through its stop signal.
- Sign-in tokens are only sent over https to Microsoft Graph and the tenant's own SharePoint addresses.
- Cached content is encrypted and authenticated per block with a random key stream per file; a damaged cache file is downloaded again. The cache is emptied once after this update.
- Releases carry build provenance attestations and can be code signed; build dependencies and GitHub Actions are pinned.

The drive no longer disappears or hangs
- VFS365 no longer opens a sign-in window by itself while it runs, and files no longer wait behind one. When a sign-in is needed the tray says so: click the notification or choose Sign in.
- Without network or sign-in at the first start, VFS365 waits and tries again by itself, and the tray says what it waits for. Other start problems keep the tray icon with the cause, Retry and Show log.
- A drive letter that is in use or not valid no longer stops the drive: it starts without a letter, and the tray says why. `M`, `m`, `M:` and `M:\` all work.
- Restart from the tray waits for pending uploads, however long they take. A cancelled shutdown keeps the drive; while changes upload, Windows shows why VFS365 delays the shutdown.
- Upgrades start VFS365 again right away for every signed-in user. New Start menu entry "VFS365" to start it again after it was closed.
- Opening or saving a large file no longer holds up other files and folders on the drive.
- Programs that read files in many places at once (databases, CAD, mail archives) no longer pause for 30 seconds or more. A download that failed is tried again by the next open.
- Fixed a rare hang of the whole drive when opening an empty file while the cache was full.
- The agent keeps memory in check when a backup or search tool walks a very large library, and the cache stays within `CacheSizeMB`.

Fewer Microsoft 365 calls
- Discovery reads all document libraries of a site with one request instead of one per library, and spreads the 48-hour re-checks over several days.
- A library missing from one search run stays until it is missing twice in a row, so a hiccup in SharePoint Search no longer empties its cached files. Search pages through results in a stable order, no longer returns colleagues' OneDrive libraries, and covers every geo of a multi-geo tenant.
- Libraries are discovered again daily while the PC stays signed in. Hidden libraries that are made visible again show up within two days. Pinned links that point to nothing are checked once every two days.
- After Microsoft throttles, all background work (discovery, refreshes, change checks, loading ahead) waits `ThrottlePauseMinutes`; what you open and save goes on as soon as Microsoft allows. Waits vary a little per device and queued requests resume gradually, so a tenant's devices don't all come back at once.
- Folders known from the last session are trusted for a day, change checks slow down while nothing changes, a library only glanced at in Explorer gets no change checks, push notifications are combined per library, opening many files of one folder lists the folder once, and downloads need one call less.

Tray and notifications
- Notifications are combined ("27 files are waiting to upload: offline") and give plain causes. New `NotifyUploads` policy mutes "Uploaded".
- The tray shows offline and throttled states, has access keys, opens from the keyboard, opens one Explorer window per double-click, and shows when Refresh works again in the item itself. Refresh says when it failed, and a failed refresh doesn't use up the daily one.
- The tray, its notifications and the Refresh hint are in Dutch on Dutch Windows.
- OneDrive-only drives start without the network from the second start on.

Monitoring and log
- New `MonitoringDetail` policy: `NoPaths` keeps file and folder paths out of error reports.
- Statistics that couldn't be sent are kept and sent later, and time asleep no longer counts as running. A device where VFS365 stopped restarting after repeated crashes reports it, and crash details are read correctly on non-English Windows. The dashboard warns about both and removes `#connect=` from the address bar.
- The log no longer loses lines during a restart. It records sleep and resume, network changes, the signed-in account, and values outside their range. Policy changes are noticed and apply after Restart.
- `vfs365 signout` waits until pending uploads finish before removing local data.

Installer and policy
- The x64 package refuses to install on ARM64 devices and points to the arm64 package.
- Navigation pane entries of users who were signed out during an uninstall are removed at their next sign-in.
- The `DriveLetter` policy template no longer suggests `M` (the default is `None`).

## 0.3.2  (2026-10-09)

- Sites by template: the new `IncludedSiteTemplates` and `ExcludedSiteTemplates` policies show or hide libraries by their site's template, for example only `GROUP` (team sites with a Microsoft 365 group, Teams) or everything but `TEAMCHANNEL` (private and shared channel sites). The template comes with the search results, so it costs no extra calls. `vfs365 discover` shows each library's template.
- Fixed: excluding sites by policy now takes effect even when it hides more than 40% of the libraries. Before, discovery's safety check (nothing is hidden when the set shrinks that much, against search hiccups) kept them all visible.

## 0.3.1  (2026-10-08)

- Downloads: a file that Microsoft 365 serves with another length than it lists (changed since the folder was listed, or an Office file SharePoint writes library properties into) no longer fails three times with "Unable to read beyond the end of the stream", and a longer one is no longer cut off without notice. The read fails once, the drive takes the length served, and opening the file again works; the log names the file.
- Downloads that break off are tried again after 1 and 2 s. Only when all three attempts fail is the error logged, now with the file's path, and reported to monitoring.

## 0.3.0 (2026-10-08)

- Fewer calls at sign-in, spread over time: when the libraries of the last session are less than a week old, the drive shows them at once and discovery waits a random 5 to 90 minutes, so a tenant's morning sign-ins don't all discover at the same moment. Library details are then read again only for libraries checked more than 48 hours ago (each library by its own age; before, all of them every 24 hours). A first start, or a state older than a week, discovers right away.
- Tray: Refresh reads all libraries and folders again (folders as they are opened). It works once a day, and only when the libraries were read more than an hour ago; when it is greyed out, pointing at it says why and when it works again.
- Log: every sign-in starts a new `agent.log`, which begins with the settings that differ from the defaults and where each comes from (computer or user policy, marked Intune when Intune set it, or the config file). Earlier sign-ins move to `logs\`, kept for 7 days (at most 50 files); a log that reaches 10 MB continues in a new file. A restart from the tray keeps writing to the same log.
- `vfs365 settings` shows every setting with its source.
- Stability: `vfs365-agent.exe` now watches the agent and starts it again within seconds when it crashes (at most 3 times in 15 minutes), so the drive comes back without a new sign-in. The log says what Windows recorded about the crash (faulting module, exception, stack), and monitoring reports it.
- Drive letter checks: at the start and every 5 minutes the agent checks what Windows tells Explorer about its drive letter, and logs when something is off, with the cause and the fix: a remembered mapping to another share on the same letter, no network provider reporting the letter (WinFsp.Np missing from the provider order), the letter removed by another tool, or VFS365.Np missing (typed `\\VFS365` paths).
- Refresh in the tray shows one notification when it is done (the status reads "refreshing" meanwhile).
- Monitoring: daily statistics split the time waited for the API budget into user actions (`BudgetWaitForegroundSeconds`) and background work (`BudgetWaitBackgroundSeconds`); `BudgetWaitSeconds` stays the total. The log line at unmount shows both.
- Monitoring dashboard: `vfs365-monitoring.html`, one file that shows the monitoring tables in a browser: what needs attention, trends per day (including what users and background work waited for, with an explanation), devices, users, errors grouped by cause and installs, with details per row. It reads with a Read and List SAS, refreshes on a schedule and explains SAS and CORS problems. See DEPLOYMENT.md.

## 0.2.0 (2026-10-07)

- Libraries that SharePoint keeps read-only for a while (maintenance, a site or geo move: `403 serviceReadOnly`, "Database Is Read Only"): the change check no longer retries every 20 s and logs every attempt. It backs off from 1 to 30 minutes, logs the problem once in plain words and logs again when it works; folders are read again when opened meanwhile. Saves into such a library wait and retry (shown as waiting in the tray) instead of failing.
- Uploads and deletes that fail for a passing reason retry less often over time: 30 s, then twice as long each time, up to 15 minutes.
- Change feed log lines name the library's path, not just its drive ID.

## 0.1.9 (2026-10-07)

- Copying many small files to the drive is much faster: new files upload right after their close, four at a time, instead of the close waiting for each upload (new `BackgroundUploads` policy, on by default). 300 small files: the copy takes 3 s instead of 171 s, and all are uploaded after 29 s, with half the requests. Saves into existing files still upload before the close returns.
- Copying many small files out of the drive is faster: the next small files of the folder are downloaded ahead, eight at most (new `ReadAheadFiles` policy). 300 small files with nothing cached: 21 s instead of 115 s, same number of requests.
- Apps that write a file in small pieces, or open, append and close it again and again (log files), no longer upload the whole file on every close: a file closed again within 30 s of its last upload uploads once at the end of that time (new `RepeatSaveSeconds` policy). 50 appends to a log: 5 requests instead of 52.
- Uploads of different files no longer wait for each other, and a large upload no longer holds up work on other files.
- Files created in a folder the drive just made need no lookups.
- Antivirus guidance in DEPLOYMENT.md: what Microsoft Defender scans on the drive, and the only exclusion worth considering.
- Source repository ready for GitHub: releases are built by GitHub Actions when `VERSION` changes, contribution and support guidelines (support is provided by JSolve B.V., see SUPPORT.md).

## 0.1.8 (2026-10-07)

- Monitoring: with the new `MonitoringUrl` policy (a table service SAS URL), errors, daily usage statistics and installs, updates and uninstalls go to your own Azure table storage. Best effort, never retried; off by default. See DEPLOYMENT.md.
- API use set by policy: `ApiBudgetPerMinute` (default 600 resource units per user per minute; background work only uses the second half), `ThrottlePauseMinutes` (default 15) and `WalkPrefetchFolders` (default 10).
- Tree walks load at most 10 folders ahead of the app, in the app's own order (depth-first or breadth-first), so a walk that stops early wastes at most 10 listings. Opening many folders side by side (Explorer drawing folder thumbnails) no longer counts as a walk. The log reports per walk how many folders were loaded ahead and used.
- Guest (B2B) accounts and accounts from another tenant are refused with a clear message: VFS365 works with member accounts in their own tenant.
- Drive letter only: with `NavigationPane` off, a navigation pane entry left by an earlier run is removed. The policy texts explain the drive-letter-only setup.
- The log line at unmount shows resource units, throttling and time waited for the budget.

## 0.1.7 (2026-10-07)

- Tray: new icons that follow the taskbar theme, with an amber or red dot for waiting uploads and problems. The menu has Show files (opens VFS365; it opened OneDrive before), Show log, Restart, Help (new `HelpUrl` policy, default https://jsolve.nl) and the JSolve website.
- Changes made in the browser or elsewhere show faster: libraries used in the last 2 minutes check for changes every 20 s (new `ChangeCheckSeconds` policy). The check pauses after any throttling.
- Tree walks (copying, searching or backing up a folder tree) list the folders ahead: 1,210 folders in 115 s instead of 255 s, with no extra calls.
- Pinned locations: the new `PinnedLocations` policy and `vfs365 pin <url>` show sites and libraries that search doesn't find, such as the root site and sites with Restricted Content Discovery.
- Multi-user database files (Access, QuickBooks, SQLite) can be made read-only or blocked with the new `DatabaseFiles` policy. The default is unchanged (allowed).
- Conditional Access changes apply within minutes (CAE claims challenges).
- Fixed: a memory-mapped file that was changed and then closed now uploads. Before, the change waited until the next start.
- The log starts with a line naming JSolve.

## 0.1.6 (2026-10-06)

- Faster start: the drive mounts at once with the libraries and folders of the last session; discovery runs in the background and reuses library details for a day (1 call instead of about 90).
- Changes made elsewhere show up by themselves: a change feed per library in use (delta plus push notifications) updates open Explorer windows and apps watching a folder, and cached folders aren't listed again.
- Big folders show their first entries while the rest loads (0.4 s instead of 1.9 s for 7,733 items).
- Cache encrypted with a per-user key, limited by the new `CacheSizeMB` policy (default 2 GB), and wiped on sign-out, another account or tenant, and Intune unenrollment. The first start of this version removes the old, unencrypted cache.
- Tray icon with status and notifications for conflict copies, waiting uploads and sign-in problems (`TrayIcon` policy).
- New commands: `signout`, `inspect`, `put`, `watch`, `discover --audit`, `discover --full`.

## 0.1.5 (2026-10-06)

- Network provider `vfs365np.dll`: `\\VFS365\...` paths typed or pasted into Explorer, file dialogs or Run open (they failed before, while browsing worked).
- `vfs365.exe machine-setup` puts it in Windows' provider order; uninstall takes it out again.

## 0.1.4 (2026-10-06)

- Multi-session hosts (AVD): the UNC path is now `\\VFS365\<user>`, so concurrent users each mount their own volume. Before, the second user of a tenant on a host failed with `0x80070050`.
- A clear error when the UNC path is already in use.
- Explorer drive names left by older versions are removed.

## 0.1.3 (2026-10-06)

- No app registration needed: JSolve's multi-tenant app is the default `ClientId`; an admin consents once per tenant. Consent errors log the consent link.
- New defaults: `Scope` SharePoint, `DriveLetter` None.

## 0.1.2 (2026-10-06)

- GPLv3. Setup programs per architecture that install WinFsp (bundled, unmodified) and VFS365.
- Uninstall stops every user's agent cleanly, removes per-user caches and Explorer entries, and keeps unsaved changes.
- Agents remove their navigation pane entry at logoff.
- `vfs365.exe about` and `machine-stop`.

## 0.1.1 (2026-10-06)

First release build: x64 and ARM64 MSIs with policy templates.

- Microsoft 365 as a drive and a navigation pane entry: OneDrive and SharePoint libraries, discovered per user (M365AutoLink's method).
- Reads stream; saves upload into the original item (write-through), large copies upload while they are written; unsaved changes survive a crash.
- Settings by policy: drive letter (or none), name, OneDrive or SharePoint only, site filters, navigation pane.
