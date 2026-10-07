# Changelog

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
