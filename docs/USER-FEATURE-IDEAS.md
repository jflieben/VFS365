# User-facing feature ideas

VFS365 today is mostly about doing the plumbing right: safe saves, conflict copies, change
feeds, discovery, offline reads of folders already seen, throttling and monitoring. The
**interactive surface users touch is still thin** — a tray menu (Status, Sign in, Show files,
Show log, Refresh, Restart, Help), a handful of notifications, and a command-line tool.

This is a menu of **ideas for things users could do from the drive**, grouped by theme, to
pick from. They are **not commitments or a roadmap**, and each needs its own design. Admin,
deployment and policy features are deliberately left out here; this is the end-user side only.

A few things worth knowing up front:

- Most of these reuse Microsoft 365 capabilities VFS365 already reaches with the signed-in
  user's delegated permissions (versions, sharing links, the recycle bin, search, recent
  files, thumbnails, columns, check-out). They mostly need a UI and some wiring, not a bigger
  footprint — though any new permission scope would need a one-time admin consent.
- The biggest lift is **Explorer integration**: overlay icons, a status column, right-click
  menu items, thumbnails and property pages each need a Windows shell extension, and none
  exist yet. A tray window or a small standalone window is a much cheaper way to ship many of
  the same features first.
- The cheapest early wins reuse things that are nearly there already: version history
  (`ListVersionsAsync` exists but is diagnostics-only today), restoring from the recycle bin
  (deletes already go there), "copy link" / "open in browser", and a small settings/pins
  window.

## A. Versioning, history & recovery

1. **Restore a previous version.** Right-click → *Version history*: list a file's versions
   with their date, author and size, and restore or save-as one.
2. **Browse & restore the recycle bin.** A "Recently deleted" view (or tray action) to put
   back items that were deleted; today deletes go to the recycle bin with no way back from the
   drive.
3. **Undo last action.** A tray or Explorer *Undo* for the most recent delete, rename or move
   made through the drive.
4. **Restore a folder to an earlier point in time.** Roll a folder's files back to a chosen
   date, for recovery after a mass accidental edit or ransomware.
5. **See a file's recent activity.** Who changed it and when, shown in its properties or a
   small panel, without opening the browser.
6. **Smarter conflict resolution.** When a conflict copy is made, offer *Open both*,
   *Compare*, or *Keep mine / keep theirs* instead of only a notification.
7. **Recover unsaved changes on demand.** Surface the "(recovered)" copies the agent already
   writes after a crash as a visible "unsaved changes" list to review and keep or discard.

## B. Sharing & collaboration

8. **Copy sharing link.** Right-click → *Copy link* (view or edit, organisation-wide or
   specific people), so people stop mailing attachments.
9. **Share with specific people.** A share dialog to invite colleagues with view or edit, an
   optional message and an expiry.
10. **Manage & stop sharing.** See who has access to a file or folder and revoke links or
    people.
11. **Open in browser / View in SharePoint.** Right-click to open the item's web address in
    the browser.
12. **Edit in Office for the web.** Since live co-authoring isn't possible from the drive, a
    one-click way to edit in the browser with others, after which the file is back on the
    drive.
13. **Request access to a restricted folder.** When a user opens a location they can't reach,
    offer a *Request access* action to the owner.
14. **"Shared with me" view.** A place that lists files and folders others shared with the
    user, not only discovered sites.
15. **Copy path / copy SharePoint URL.** Quick actions to copy the `\\VFS365\<user>\…` path or
    the item's web address for pasting into chat or e-mail.

## C. Offline availability & storage

16. **"Always keep on this device".** Pin files or folders to download ahead and keep cached,
    so they are guaranteed offline; today caching only happens as files are opened.
17. **"Free up space".** Right-click to drop a cached copy and make a file online-only again,
    reclaiming disk.
18. **Make a whole folder available offline.** Pin a library or folder subtree ahead of travel
    or a weak connection.
19. **Offline and sync overlay icons.** Show a file's state at a glance in Explorer — on the
    device, online-only, or uploading.
20. **"Work offline" toggle.** Let a user deliberately stop background calls on a metered
    connection or in flight, and resume later.
21. **Cache usage view.** Show how much disk the cache uses, broken down by library, with a
    one-click clear.
22. **Warn before downloading very large trees.** When copying a huge folder out of the drive,
    estimate the size and warn first, to prevent an accidental multi-gigabyte pull.

## D. Search & discovery

23. **Search Microsoft 365 from the drive.** A fast search box (in the tray, or a virtual
    "Search results" folder) that uses SharePoint search instead of a slow Explorer tree-walk.
24. **Recent files.** A tray menu or "Recent" view of recently opened or edited items.
25. **"Add shortcut to my drive".** Follow a library or folder shared with you so it shows at
    the drive root.
26. **Self-service "Pin to VFS365" from Explorer.** Right-click a site, library or folder to
    pin it; today pinning needs the command line, which non-technical staff can't use.
27. **Quick "Go to library" launcher.** A type-to-filter jump list over every library the user
    can reach, for people with many sites.
28. **Followed and frequent sites first.** Surface the user's followed or most-used sites near
    the top of the drive automatically.

## E. Status, activity & transparency

29. **Activity / upload centre window.** A window listing uploads and downloads in progress and
    waiting, with progress, cause and retry — more than a one-line notification.
30. **Per-file sync status in properties.** A details entry showing last-uploaded time and
    whether the file is queued, uploading, in conflict or available offline.
31. **"VFS365 status" column in Explorer.** A sortable column (Up to date / Uploading /
    Waiting / Conflict) in detail view.
32. **Pause uploads for 1, 2 or 24 hours.** A tray option to hold background uploads during a
    call or on a tethered connection.
33. **Upload and download bandwidth limits.** A user-set cap so large transfers don't saturate
    the connection.
34. **Richer "what's happening now" in the tray.** Expand to show the current file, the queue
    length, whether Microsoft 365 is limiting requests, and the next retry time.
35. **Completion notification for big copies.** An optional "your copy of X is done" message
    when a long transfer finishes.
36. **Quota and storage usage.** Show the OneDrive or site storage used versus available, so
    "quota exceeded" is never a surprise.

## F. Metadata & SharePoint columns

37. **View & edit SharePoint columns.** See and edit library metadata (managed metadata,
    choice and people columns) from a file's properties.
38. **Choose a content type.** Pick a content type where the library requires one for a new or
    edited document.
39. **View and apply sensitivity and retention labels.** Show the file's label and, where
    allowed, set a sensitivity label.
40. **Prompt for required metadata on save.** When a library needs metadata before publishing,
    ask for it instead of silently blocking the save or leaving a draft.
41. **Rich file details panel.** Modified-by, created, size, path, web address and version
    count in one place.

## G. Locking & concurrency

*These ease the drive's co-authoring and multi-user database limits.*

42. **Check-out and check-in.** Reserve a file with SharePoint check-out, then check it in or
    discard, from the right-click menu — a safe path for files that can't co-author.
43. **"Who has this open?" presence.** Show whether a colleague currently has the file open or
    checked out before you start editing.
44. **Clear warning for multi-user database files.** A plain-language warning when opening
    Access, QuickBooks or SQLite files whose locks don't cross machines; today this is only an
    admin allow / read-only / block policy.

## H. Previews & thumbnails

45. **Thumbnails in Explorer.** Picture and Office thumbnails so folders aren't walls of
    generic icons.
46. **Preview-pane support.** Make Explorer's preview pane render Office files, PDFs and images
    from the drive by streaming just enough of each.
47. **Rich hover tooltips.** Modified-by, size and sync state in the Explorer hover tooltip.

## I. Personalisation & self-service settings

48. **A settings window.** A small UI for cache size (within the admin's limits), notification
    preferences, language and pins; today these are admin policy and the command line only.
49. **Language override.** Choose the interface language instead of always following the
    Windows display language.
50. **Notification preferences.** Mute low-value messages (uploaded, refreshed) while keeping
    conflicts and sign-in.
51. **Manage pinned locations in a UI.** List, add and remove pins without the command line.
52. **Hide libraries you never use.** Let a user hide clutter libraries from their own view.

## J. Onboarding, help & productivity

53. **First-run welcome or guided tour.** A short intro the first time the drive appears,
    explaining where files live, the tray icon and how offline works.
54. **"What's new" after an update.** Surface the changelog highlights once per version.
55. **One-click "Report a problem".** Bundle the current logs and open the help page or a
    prefilled support mail; today the tray only offers *Show log*.
56. **An in-drive "Read me first" shortcut.** A small help link at the drive root for
    non-technical users.
