using System.Diagnostics;
using System.Text.Json;
using Microsoft.Identity.Client;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Drive.WinFsp;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>The agent's commands, shared by vfs365.exe (console) and vfs365-agent.exe (no window, started at logon).</summary>
public static class AgentCommands
{
    public static string Usage => $"""
        {AgentControl.Publisher}
        {AgentControl.Banner}

        Usage:
          vfs365 settings [--config <file>]               effective settings and where each comes from
          vfs365 whoami   [--config <file>]               sign in, show tenant and OneDrive
          vfs365 discover [--config <file>] [--drives] [--json] [--full] [--audit]
                                                          find the user's libraries (--drives resolves Graph drive IDs, --full reads all metadata again,
                                                          --audit checks recall against site search, followed sites and hubs)
          vfs365 mount    [--config <file>] [--drive M:|*|None] [--verbose] [--trace] [--no-shell]
                                                          mount OneDrive and the discovered libraries
          vfs365 unmount                                  stop the mount in this session
          vfs365 inspect  <path> [--versions]             item ID, size and tags of a path on the volume (OneDrive\...), a folder's
                                                          children, or a file's version count
          vfs365 put      <local file> <volume folder>    upload a file through Graph without the drive (scripted tests)
          vfs365 watch    <volume folder> [--seconds N] [--verbose]
                                                          show push notifications for the drive holding the folder
          vfs365 pin <url> | unpin <url> | pins           show a site or library (a copied browser link works) even where
                                                          search doesn't find it; takes effect at the next start or Restart
          vfs365 signout                                  stop the drive in this session, forget the sign-in, and remove cached
                                                          content, folder listings and discovery state (unsaved changes stay)
          vfs365 machine-setup [--dry-run]                put the VFS365 network provider in Windows' provider order
          vfs365 machine-stop [--cleanup] [--dry-run]     stop every user's agent; --cleanup also removes their cached data,
                                                          Explorer entries and the provider order entry (the installer runs both
                                                          as SYSTEM)
          vfs365 machine-report install|update|uninstall  report the event to the devices table when MonitoringUrl is set (the
                                                          installer runs it as SYSTEM)
          vfs365 about                                    version, licence and notices
        """;

    /// <summary>Logoff or shutdown: drop the navigation pane entry right away and let the mount stop.</summary>
    public static void StopForSessionEnd()
    {
        ExplorerLocation.Unregister();
        AgentControl.RequestStop();
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, bool timestamps = false, bool tray = false)
    {
        void Say(string line) => output.WriteLine(timestamps ? $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}" : line);
        void Log(string line) => output.WriteLine($"  {DateTime.Now:HH:mm:ss.fff} {line}");
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

        var command = args.FirstOrDefault()?.ToLowerInvariant();
        if (command is not ("settings" or "whoami" or "discover" or "mount" or "unmount" or "inspect" or "put" or "watch" or "pin" or "unpin" or "pins" or "signout" or "machine-setup" or "machine-stop" or "machine-report" or "about"))
        {
            error.WriteLine(Usage);
            return 2;
        }

        if (command == "about")
        {
            Say(AgentControl.Publisher);
            Say(AgentControl.Banner);
            Say("Source and licence: see LICENSE and THIRD-PARTY-NOTICES.md next to this program.");
            return 0;
        }

        if (command is "pin" or "unpin")
        {
            if (args.Length < 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out var location) || location.Scheme != "https")
            {
                error.WriteLine("Usage: vfs365 pin <https URL of a SharePoint site or library>");
                return 2;
            }
            var changed = command == "pin" ? UserPins.Add(location.ToString()) : UserPins.Remove(location.ToString());
            Say(changed ? $"{(command == "pin" ? "Pinned" : "Unpinned")}: {location} (takes effect at the next start, or Restart in the tray menu)" : "Nothing changed.");
            return 0;
        }

        if (command == "pins")
        {
            foreach (var pin in UserPins.Load())
            {
                Say($"user:   {pin}");
            }
            foreach (var pin in AgentSettings.Load(Option("--config")).PinnedLocations)
            {
                Say($"policy: {pin}");
            }
            return 0;
        }

        if (command == "unmount")
        {
            if (!AgentControl.RequestStop())
            {
                error.WriteLine("Not mounted.");
                return 1;
            }
            Say("Unmounting.");
            return 0;
        }

        if (command == "machine-setup")
        {
            NetworkProviderOrder.Add(Flag("--dry-run"), Say);
            return 0;
        }

        if (command == "machine-stop")
        {
            var dryRun = Flag("--dry-run");
            MachineCleanup.StopAgents(TimeSpan.FromSeconds(60), dryRun, Say);
            if (Flag("--cleanup"))
            {
                MachineCleanup.RemoveUserData(MachineCleanup.ProfileDataFolders(), dryRun, Say);
                MachineCleanup.RemoveExplorerEntries(dryRun, Say);
                NetworkProviderOrder.Remove(dryRun, Say);
            }
            return 0;
        }

        if (command == "machine-report")
        {
            // Best effort and quick: an install never waits long for it or fails because of it
            var action = args.ElementAtOrDefault(1)?.ToLowerInvariant();
            if (action is not ("install" or "update" or "uninstall"))
            {
                error.WriteLine("Usage: vfs365 machine-report install|update|uninstall");
                return 2;
            }
            if (MonitoringTarget.Parse(AgentSettings.Load(Option("--config")).MonitoringUrl, out var problem) is not { } target)
            {
                Say(problem ?? "Monitoring: off (no MonitoringUrl policy for the device)");
                return 0;
            }
            var report = new Monitoring(target, DeviceInfo.Current(), Say).DeviceAsync(action, string.Join(", ", Monitoring.SessionUsers()));
            await Task.WhenAny(report, Task.Delay(TimeSpan.FromSeconds(10)));
            if (report.IsCompletedSuccessfully && report.Result)
            {
                Say($"Reported {action} to {target.Account}");
            }
            else if (!report.IsCompleted)
            {
                Say($"Reporting {action} to {target.Account} took too long; not retried");
            }
            return 0;
        }

        string? clientId = null;
        Monitoring? monitoring = null;
        try
        {
            var settings = AgentSettings.Load(Option("--config"));
            var join = DeviceJoin.Read();
            var tenant = TenantResolver.Resolve(settings, join);

            if (command == "settings")
            {
                Say($"ClientId:       {settings.ClientId.Value} ({settings.ClientId.Source})");
                Say($"Tenant:         {tenant.Tenant} ({tenant.Source})");
                Say($"Label:          {settings.Label}");
                Say($"DriveLetter:    {settings.DriveLetter}");
                Say($"Scope:          {settings.Scope}");
                Say($"IncludedSites:  {string.Join("; ", settings.IncludedSites ?? new DiscoveryOptions().IncludedSites)}");
                Say($"ExcludedSites:  {string.Join("; ", settings.ExcludedSites ?? new DiscoveryOptions().ExcludedSites)}");
                Say($"NavigationPane: {settings.NavigationPane}");
                Say($"CacheSizeMB:    {settings.CacheSizeMB}");
                Say($"TrayIcon:       {settings.TrayIcon}");
                Say($"HelpUrl:        {settings.HelpUrl}");
                Say($"ChangeCheck:    {(settings.ChangeCheckSeconds == 0 ? "push only" : $"{settings.ChangeCheckSeconds} s")}");
                Say($"ApiBudget:      {(settings.ApiBudgetPerMinute == 0 ? "no limit of its own" : $"{settings.ApiBudgetPerMinute} resource units per minute")}");
                Say($"ThrottlePause:  {settings.ThrottlePauseMinutes} min");
                Say($"WalkPrefetch:   {(settings.WalkPrefetchFolders == 0 ? "off" : $"up to {settings.WalkPrefetchFolders} folders ahead")}");
                Say($"ReadAhead:      {(settings.ReadAheadFiles == 0 ? "off" : $"up to {settings.ReadAheadFiles} files ahead")}");
                Say($"Uploads:        new files {(settings.BackgroundUploads ? "in the background" : "before the close returns")}, " +
                    $"{(settings.RepeatSaveSeconds == 0 ? "every close uploads" : $"saves within {settings.RepeatSaveSeconds} s combined")}");
                Say($"DatabaseFiles:  {settings.DatabaseFiles}");
                var pins = settings.PinnedLocations.Concat(UserPins.Load()).ToList();
                Say($"Pinned:         {(pins.Count == 0 ? "none" : string.Join("; ", pins))}");
                var target = MonitoringTarget.Parse(settings.MonitoringUrl, out var problem);
                Say($"Monitoring:     {(target is not null ? $"{target.Account}{(target.Expires is { } expires ? $" (SAS valid until {expires.ToLocalTime():yyyy-MM-dd HH:mm})" : "")}" : problem ?? "off")}");
                return 0;
            }

            if (command == "mount" && MonitoringTarget.Parse(settings.MonitoringUrl, out _) is { } monitoringTarget)
            {
                monitoring = new Monitoring(monitoringTarget, DeviceInfo.Current(), Say) { TenantId = () => tenant.Tenant };
            }

            Say($"Tenant:     {tenant.Tenant} ({tenant.Source})");
            if (settings.TenantId.Value is not null)
            {
                Say($"Detected:   {(join is null ? "nothing (device not joined or registered)" : $"{join.TenantId} (device {join.Kind})")}");
            }
            clientId = settings.ClientId.Value!;
            var tokens = new MsalTokenSource(clientId, tenant.Tenant);
            if (monitoring is not null)
            {
                monitoring.User = () => tokens.LastResult?.Account.Username;
                monitoring.TenantId = () => tokens.LastResult?.TenantId ?? tenant.Tenant;
            }

            if (command == "signout")
            {
                if (AgentControl.RequestStop())
                {
                    Say("Stopping:   the drive in this session (pending changes upload first)");
                    for (var waited = 0; waited < 120 && AgentControl.IsMounted(); waited++)
                    {
                        await Task.Delay(500);
                    }
                }
                await tokens.SignOutAsync();
                WipeUserData();
                Say("Signed out: cached content, folder listings and discovery state removed. Unsaved changes in staging stay.");
                return 0;
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            var client = new M365Client(http, tokens, settings.ApiBudgetPerMinute > 0 ? new RequestBudget(settings.ApiBudgetPerMinute) : null);
            var api = new SharePointDiscoveryApi(client);
            var stopwatch = Stopwatch.StartNew();
            if (Flag("--verbose"))
            {
                client.Log = Log;
            }

            if (command == "whoami")
            {
                var me = await api.GetMyDriveAsync(default);
                Say($"User:       {tokens.LastResult?.Account.Username}");
                Say($"Signed in:  tenant {tokens.LastResult?.TenantId}");
                Say($"OneDrive:   {me.WebUrl}");
                Say($"SharePoint: {SharePointUrls.TenantRootFromOneDriveUrl(me.WebUrl)}");
                return 0;
            }

            if (command == "inspect")
            {
                if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
                {
                    error.WriteLine(@"Usage: vfs365 inspect <path on the volume, for example OneDrive\Documents\a.docx> [--versions]");
                    return 2;
                }
                return await InspectAsync(settings, api, client, args[1], Flag("--versions"), Say);
            }

            if (command == "watch")
            {
                if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
                {
                    error.WriteLine(@"Usage: vfs365 watch <volume folder, for example OneDrive> [--seconds N] [--verbose]");
                    return 2;
                }
                return await WatchAsync(settings, api, client, args[1], int.TryParse(Option("--seconds"), out var seconds) ? seconds : 120,
                    Flag("--verbose") ? Log : null, Say);
            }

            if (command == "put")
            {
                if (args.Length < 3 || !File.Exists(args[1]))
                {
                    error.WriteLine(@"Usage: vfs365 put <local file> <volume folder, for example OneDrive\VFS365-test>");
                    return 2;
                }
                return await PutAsync(settings, api, client, args[1], args[2], Say);
            }

            var discoveryOptions = new DiscoveryOptions { PinnedLocations = [.. settings.PinnedLocations, .. UserPins.Load()] };
            if (settings.IncludedSites is { } included)
            {
                discoveryOptions = discoveryOptions with { IncludedSites = included };
            }
            if (settings.ExcludedSites is { } excluded)
            {
                discoveryOptions = discoveryOptions with { ExcludedSites = excluded };
            }

            if (command == "mount")
            {
                return await MountAsync(settings, api, client, tokens, discoveryOptions, tenant.Tenant, (tray || Flag("--tray")) && settings.TrayIcon, Option("--after"), Option("--drive"), Flag("--trace"), !Flag("--no-shell") && settings.NavigationPane,
                    monitoring, stopwatch, Say, Log, error);
            }

            var previous = DiscoveryStore.Load();
            var result = await new DiscoveryService(api, discoveryOptions).RunAsync(Flag("--full") && previous is not null ? previous with { MetadataAt = default } : previous);
            var libraries = result.Libraries.ToList();
            var driveErrors = new List<string>();
            if (Flag("--drives"))
            {
                for (var i = 0; i < libraries.Count; i++)
                {
                    try
                    {
                        libraries[i] = libraries[i] with { DriveId = await api.GetLibraryDriveIdAsync(libraries[i], default) };
                    }
                    catch (M365RequestException e)
                    {
                        driveErrors.Add($"{libraries[i].WebUrl} / {libraries[i].LibraryTitle}: {e.Message}");
                    }
                }
            }
            DiscoveryStore.Save(result.State with { Libraries = libraries, TenantHint = tenant.Tenant });

            if (Flag("--json"))
            {
                output.WriteLine(JsonSerializer.Serialize(result with { Libraries = libraries }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            Say($"SharePoint: {result.TenantRoot}");
            Say($"Search:     {result.SearchHits} hits, {result.SearchError ?? "complete"}");
            Say($"Libraries:  {libraries.Count} shown ({libraries.Count(l => l.ReadOnly)} read-only), {result.Skipped.Count} skipped, {result.Errors.Count} errors");
            foreach (var library in libraries)
            {
                Say($"  {$"{library.SiteTitle} / {library.LibraryTitle}",-60} {library.ItemCount,10:N0} items{(library.ReadOnly ? "  read-only" : "")}{(library.DriveId is null ? "" : $"  {library.DriveId}")}");
            }
            Say("Skipped:");
            foreach (var group in result.Skipped.GroupBy(s => s.Reason).OrderByDescending(g => g.Count()))
            {
                Say($"  {group.Count(),5}  {group.Key}");
            }
            foreach (var problem in result.Errors)
            {
                Say($"Error:      {problem.WebUrl} / {problem.Title}: {problem.Reason}");
            }
            foreach (var problem in driveErrors)
            {
                Say($"Drive:      {problem}");
            }
            if (result.HidingSkippedReason is not null)
            {
                Say($"Hiding:     skipped this run, {result.HidingSkippedReason}");
            }
            Say($"Requests:   Graph {client.GraphRequests}, SharePoint {client.SharePointRequests}, {stopwatch.Elapsed.TotalSeconds:N1} s");
            Say($"State:      {DiscoveryStore.FilePath}");
            if (Flag("--audit"))
            {
                var audit = await new DiscoveryAudit(client).RunAsync(result with { Libraries = libraries }, discoveryOptions, default);
                Say($"Audit:      {audit.SitesChecked} sites checked ({audit.SitesFromSearch} from site search, {audit.SitesFollowed} followed, {audit.Hubs} hubs), " +
                    $"{audit.SitesNotListable} not listable");
                Say($"Recall:     {audit.LibrariesShown} of {audit.LibrariesExpected} document libraries shown" +
                    $"{(audit.LibrariesExpected > 0 ? $" ({100.0 * audit.LibrariesShown / audit.LibrariesExpected:N0}%)" : "")}");
                foreach (var miss in audit.Missed)
                {
                    Say($"  Missed:   {miss.WebUrl} / {miss.Library}: {miss.Reason}");
                }
                Say($"Requests:   Graph {client.GraphRequests}, SharePoint {client.SharePointRequests} with the audit, {stopwatch.Elapsed.TotalSeconds:N1} s");
            }
            return 0;
        }
        catch (Exception e) when (e is MsalException or M365RequestException or HttpRequestException or FileNotFoundException or JsonException or IOException or ArgumentException)
        {
            error.WriteLine(e.Message);
            if (monitoring is not null)
            {
                monitoring.Error("start", e.Message);
                await monitoring.FlushAsync(TimeSpan.FromSeconds(5));
            }
            if (e.Message.Contains("AADSTS50194", StringComparison.Ordinal))
            {
                error.WriteLine("The app registration is single-tenant: make it multi-tenant, or force TenantId.");
            }
            if (clientId is not null && new[] { "AADSTS65001", "AADSTS90094", "AADSTS90095" }.Any(code => e.Message.Contains(code, StringComparison.Ordinal)))
            {
                error.WriteLine($"An admin must consent to the app for this tenant once: {AgentSettings.AdminConsentUrl(clientId)}");
            }
            return 1;
        }
    }

    /// <summary>
    /// \VFS365\&lt;Windows user name&gt;. UNC prefixes are machine-wide, so each user on a multi-session host needs their own;
    /// characters not allowed in a share name become '_'.
    /// </summary>
    public static string UncPrefix(string userName) =>
        $@"\VFS365\{string.Concat(userName.Select(c => char.IsControl(c) || "\"/\\[]:|<>+=;,?*".Contains(c) ? '_' : c))}";

    /// <summary>Cached content, folder listings, discovery state and the cache key; never staging (unsaved changes).</summary>
    static void WipeUserData()
    {
        var cache = Path.Combine(DiscoveryStore.DataDirectory, "cache");
        if (Directory.Exists(cache))
        {
            Directory.Delete(cache, recursive: true);
        }
        MetadataStore.Delete();
        File.Delete(DiscoveryStore.FilePath);
        File.Delete(CacheKey.FilePath);
    }

    /// <summary>An engine without a mount, for the diagnostic commands: OneDrive plus the libraries of the last discovery.</summary>
    static async Task<(DriveEngine Engine, DriveApi DriveApi)> ToolEngineAsync(AgentSettings settings, SharePointDiscoveryApi api, M365Client client)
    {
        var oneDrive = await api.GetMyDriveAsync(default);
        var driveApi = new DriveApi(client);
        var engine = new DriveEngine(
            DriveNamespace.Build(oneDrive, DiscoveryStore.Load()?.Libraries ?? [], api.GetLibraryDriveIdAsync, settings.Scope),
            driveApi,
            new ContentCache(Path.Combine(Path.GetTempPath(), $"vfs365-tool-{Environment.ProcessId}"), driveApi),
            new DriveEngineOptions { StagingDirectory = Path.Combine(Path.GetTempPath(), $"vfs365-tool-{Environment.ProcessId}", "staging") });
        return (engine, driveApi);
    }

    /// <summary>Prints push notifications for the drive holding a folder, until the time is up or Ctrl+C.</summary>
    static async Task<int> WatchAsync(AgentSettings settings, SharePointDiscoveryApi api, M365Client client, string folder, int seconds, Action<string>? trace,
        Action<string> say)
    {
        var (engine, _) = await ToolEngineAsync(settings, api, client);
        if (await engine.GetEntryAsync("\\" + folder.Trim('\\'), default) is not { } entry || await engine.DriveIdOfAsync(entry, default) is not { } driveId)
        {
            say($"Not in a library: {folder}");
            return 1;
        }
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        var notifications = new DriveNotifications(client) { Trace = trace, Problem = say };
        await notifications.ListenAsync(driveId, () => say($"{DateTime.Now:HH:mm:ss.fff} changed"), up => say($"{DateTime.Now:HH:mm:ss.fff} {(up ? "connected" : "disconnected")}"), cancel.Token);
        return 0;
    }
    /// <summary>Resolves a volume path the way the mount does (libraries from the last discovery) and prints what Graph has there.</summary>
    static async Task<int> InspectAsync(AgentSettings settings, SharePointDiscoveryApi api, M365Client client, string path, bool versions, Action<string> say)
    {
        var (engine, driveApi) = await ToolEngineAsync(settings, api, client);

        var entry = await engine.GetEntryAsync("\\" + path.Trim('\\'), default);
        if (entry is null)
        {
            say($"Not found:  {path}");
            return 1;
        }
        say($"Path:       {entry.Path}");
        say($"Item:       {entry.ItemId ?? "(virtual folder)"}");
        if (entry.IsDirectory)
        {
            var children = await engine.ListAsync(entry, default);
            say($"Children:   {children.Count}");
            foreach (var child in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                say($"  {(child.IsDirectory ? "<dir>" : child.Size.ToString("N0")),14}  {child.Modified.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {child.Name}");
            }
            return 0;
        }
        say($"Size:       {entry.Size:N0}");
        say($"Modified:   {entry.Modified.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        say($"Tag:        {entry.ContentTag}");
        if (versions && entry.ItemId is { } itemId)
        {
            var history = await driveApi.ListVersionsAsync(entry.DriveId!, itemId, default);
            say($"Versions:   {history.Count}");
        }
        return 0;
    }

    /// <summary>Uploads a local file into a folder on the volume straight through Graph, as a change made elsewhere would arrive.</summary>
    static async Task<int> PutAsync(AgentSettings settings, SharePointDiscoveryApi api, M365Client client, string file, string folder, Action<string> say)
    {
        var (engine, driveApi) = await ToolEngineAsync(settings, api, client);
        var target = await engine.GetEntryAsync("\\" + folder.Trim('\\'), default);
        if (target is not { IsDirectory: true } || await engine.DriveIdOfAsync(target, default) is not { } driveId)
        {
            say($"Not a folder in a library: {folder}");
            return 1;
        }
        var name = Path.GetFileName(file);
        var existing = await engine.GetEntryAsync($"{target.Path}\\{name}", default);
        var item = await driveApi.UploadAsync(driveId,
            existing?.ItemId is { } id ? new UploadTarget(id, null, null, null) : UploadTarget.New(target.DrivePath, name), file, default);
        say($"Uploaded:   {target.Path}\\{item.Name} ({item.Id}, {item.Size:N0} bytes)");
        return 0;
    }

    static async Task<int> MountAsync(AgentSettings settings, SharePointDiscoveryApi api, M365Client client, MsalTokenSource tokens, DiscoveryOptions discoveryOptions,
        string tenantHint, bool showTray, string? restartAfter, string? driveOption, bool trace, bool navigationPane, Monitoring? monitoring, Stopwatch stopwatch,
        Action<string> say, Action<string> log, TextWriter error)
    {
        // Drive letter: a letter, "*" for the first free one, or "None" (mounted on a free letter that is then removed; the UNC path stays)
        var letter = driveOption ?? settings.DriveLetter;
        var keepLetter = !letter.Equals("none", StringComparison.OrdinalIgnoreCase);
        string? mountPoint = null;
        if (keepLetter && letter != "*")
        {
            var single = letter.TrimEnd(':');
            if (single.Length != 1 || !char.IsAsciiLetter(single[0]))
            {
                throw new ArgumentException($"DriveLetter '{letter}' is not a letter, * or None.");
            }
            mountPoint = char.ToUpperInvariant(single[0]) + ":";
        }

        // Restarted from the tray: the old agent unmounts first
        if (int.TryParse(restartAfter, out var previous))
        {
            try
            {
                using var old = Process.GetProcessById(previous);
                old.WaitForExit(TimeSpan.FromSeconds(90));
            }
            catch (ArgumentException)
            {
            }
        }

        using var stop = AgentControl.CreateStopEvent(out var createdNew);
        if (!createdNew)
        {
            error.WriteLine("Already mounted in this session; run 'vfs365 unmount' first.");
            return 1;
        }
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };

        // Mount with what the last run knew (libraries, drive IDs, folder listings), so Explorer shows the drive at once and without
        // network calls; discovery and the change feeds bring it up to date in the background. The first run only needs the OneDrive.
        var key = CacheKey.LoadOrCreate();
        var cache = new ContentCache(Path.Combine(DiscoveryStore.DataDirectory, "cache"), new DriveApi(client),
            new ContentCacheOptions { MaxBytes = settings.CacheSizeMB * (1L << 20), Cipher = new CacheCipher(key) });
        var saved = DiscoveryStore.Load();
        if (DeviceManagement.LeftManagement())
        {
            // The device left Intune: company content cached here goes, and nothing from before is shown
            say("Wiped:      cached content and folder listings (the device is no longer managed)");
            cache.Clear();
            MetadataStore.Delete();
            saved = null;
        }
        var usable = saved is { OneDrive: not null } && saved.TenantHint == tenantHint;
        if (saved is not null && !usable)
        {
            // Another tenant than last time: what was cached belongs to the other account
            cache.Clear();
            MetadataStore.Delete();
        }
        var oneDrive = usable ? saved!.OneDrive! : await api.GetMyDriveAsync(default);
        var keeper = new DiscoveryStateKeeper(usable
            ? saved!
            : new DiscoveryState(DateTimeOffset.UtcNow, SharePointUrls.TenantRootFromOneDriveUrl(oneDrive.WebUrl), [], new Dictionary<string, string>(),
                LibraryRules.Version, oneDrive, default, tenantHint));

        var prefix = UncPrefix(Environment.UserName);
        var driveApi = new DriveApi(client);
        DriveHost? drive = null;
        Tray? tray = null;
        var engine = new DriveEngine(
            DriveNamespace.Build(oneDrive, settings.Scope == DriveScope.OneDrive ? [] : keeper.Current.Libraries, api.GetLibraryDriveIdAsync, settings.Scope,
                (library, driveId) => keeper.RememberDriveId(library.Key, driveId)),
            driveApi,
            cache,
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(DiscoveryStore.DataDirectory, "staging"),
                Log = log,
                HotPollInterval = TimeSpan.FromSeconds(settings.ChangeCheckSeconds),
                PrefetchAhead = settings.WalkPrefetchFolders,
                ReadAheadFiles = settings.ReadAheadFiles,
                UploadNewFilesInBackground = settings.BackgroundUploads,
                RepeatSaveWindow = TimeSpan.FromSeconds(settings.RepeatSaveSeconds),
                DatabaseFiles = settings.DatabaseFiles,
                Changed = change => drive?.Notify(change),
                Notice = notice =>
                {
                    tray?.Notify(notice.Kind switch
                    {
                        NoticeKind.ConflictCopy => "Saved as a copy",
                        NoticeKind.UploadWaiting => "Waiting to upload",
                        _ => "Uploaded",
                    }, notice.Kind switch
                    {
                        NoticeKind.ConflictCopy => $"{notice.Name} was changed or locked in Microsoft 365. Your version is saved next to it as {notice.Detail}.",
                        NoticeKind.UploadWaiting => $"{notice.Name} isn't uploaded yet ({notice.Detail}). VFS365 keeps trying; your changes are kept on this PC.",
                        _ => $"{notice.Name} is saved to Microsoft 365.",
                    });
                    if (notice.Kind == NoticeKind.ConflictCopy)
                    {
                        monitoring?.Error("conflict copy", $"{notice.Path ?? notice.Name} was changed or locked in Microsoft 365; saved as {notice.Detail}");
                    }
                    else if (notice.Kind == NoticeKind.UploadWaiting)
                    {
                        monitoring?.Error("upload", $"{notice.Path ?? notice.Name}: {notice.Detail}");
                    }
                },
            });
        if (usable)
        {
            engine.ImportSnapshots(MetadataStore.Load(key));
        }

        say(AgentControl.Publisher);
        say(AgentControl.Banner);
        _ = MonitoringTarget.Parse(settings.MonitoringUrl, out var monitoringProblem);
        say(monitoring?.Started ?? (monitoringProblem is null ? Monitoring.OffHint : $"Monitoring: off, {monitoringProblem}"));
        var recovered = engine.Recover();
        if (recovered > 0)
        {
            say($"Recovered:  {recovered} unsaved file(s) from an earlier run, uploading");
        }

        // File system errors ("error: ..." lines) also go to monitoring
        Action<string> fsLog = monitoring is null ? log : line =>
        {
            log(line);
            if (line.StartsWith("error: ", StringComparison.Ordinal))
            {
                monitoring.Error("file system", line["error: ".Length..]);
            }
        };
        drive = DriveHost.Mount(engine, mountPoint, keepLetter, prefix, settings.Label, fsLog, trace ? line => log($"fs {line}") : null);
        if (showTray)
        {
            // Show files opens the navigation pane entry, which needs neither a drive letter nor the network provider loaded in Explorer
            var showFiles = navigationPane ? $"shell:::{ExplorerLocation.Clsid}" : drive.DriveLetter is { } trayLetter ? trayLetter + "\\" : $@"\{prefix}";
            tray = new Tray(settings.Label, new TrayActions(showFiles, settings.HelpUrl, () =>
            {
                // A new agent with the same arguments waits for this one to unmount, then mounts
                say("Restarting: from the tray");
                var arguments = Environment.GetCommandLineArgs().Skip(1).ToList();
                if (arguments.IndexOf("--after") is var at and >= 0)
                {
                    arguments.RemoveRange(at, Math.Min(2, arguments.Count - at));
                }
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in arguments.Append("--after").Append(Environment.ProcessId.ToString()))
                {
                    start.ArgumentList.Add(argument);
                }
                Process.Start(start)?.Dispose();
                stop.Set();
            }), log);
            if (recovered > 0)
            {
                tray.Notify("Uploading earlier changes", $"{recovered} file(s) changed before VFS365 last stopped are being uploaded.");
            }
        }
        var lastSignInNotice = DateTimeOffset.MinValue;
        tokens.SignInFailed = e =>
        {
            monitoring?.Error("sign-in", e.Message);
            if (tray is not null && DateTimeOffset.UtcNow - lastSignInNotice > TimeSpan.FromMinutes(10))
            {
                lastSignInNotice = DateTimeOffset.UtcNow;
                tray.Notify("Sign-in needed", $"VFS365 couldn't sign you in: {e.Message}");
            }
        };
        var statistics = monitoring is null ? null : UsageStatistics.Load();
        DateTime? statisticsDue = null;
        using var push = new PushChannels(engine, new DriveNotifications(client) { Problem = log }, log);
        using var due = new CancellationTokenSource();
        Task scheduler;
        try
        {
            var unc = $@"\{prefix}";
            ExplorerLocation.SetDriveLabel(prefix, settings.Label);
            say($"Mounted:    {(drive.DriveLetter is { } assigned ? $"{assigned} and " : "")}{unc} ({settings.Scope}) after {stopwatch.Elapsed.TotalSeconds:N1} s, " +
                $"{(usable ? $"{keeper.Current.Libraries.Count} libraries from the last run" : "first run")}. Ctrl+C or 'vfs365 unmount' to stop.");
            if (navigationPane)
            {
                ExplorerLocation.Register(settings.Label, drive.DriveLetter is { } letterRoot ? letterRoot + "\\" : unc, $"{Environment.ProcessPath},0");
                say($"Explorer:   \"{settings.Label}\" in the navigation pane");
            }
            else
            {
                ExplorerLocation.Unregister(); // an entry left by a run with the navigation pane on, or one that was ended
            }

            if (settings.Scope != DriveScope.OneDrive)
            {
                _ = Task.Run(async () =>
                {
                    RequestPriority.MarkBackground();
                    try
                    {
                        var before = keeper.Current.Libraries;
                        var result = await new DiscoveryService(api, discoveryOptions).RunAsync(keeper.Current);
                        keeper.Replace(result.State with { TenantHint = tenantHint });
                        engine.SetLibraries(keeper.Current.Libraries);
                        var shown = keeper.Current.Libraries.Select(l => l.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var gone in before.Where(l => l.DriveId is not null && !shown.Contains(l.Key)))
                        {
                            cache.ForgetDrive(gone.DriveId!); // no longer reachable or shown: its cached content goes
                        }
                        say($"Libraries:  {result.Libraries.Count} from discovery after {stopwatch.Elapsed.TotalSeconds:N1} s{(result.SearchError is { } problem ? $" ({problem})" : "")}");
                        if (result.OneDrive.DriveId != oneDrive.DriveId)
                        {
                            say("OneDrive:   the signed-in account changed; cached content wiped, its OneDrive shows after the next start");
                            cache.Clear();
                            MetadataStore.Delete();
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        say($"Discovery:  failed, showing the libraries from the last run: {e.Message}");
                        monitoring?.Error("discovery", e.Message);
                    }
                });
            }

            scheduler = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                var nextSnapshot = DateTimeOffset.UtcNow + SnapshotInterval;
                var throttlePause = TimeSpan.FromMinutes(settings.ThrottlePauseMinutes);
                var tick = 0;
                while (await timer.WaitForNextTickAsync(due.Token))
                {
                    try
                    {
                        await engine.ProcessDueAsync(false, due.Token);
                        engine.HotPollingPaused = engine.PrefetchPaused = DateTimeOffset.UtcNow - client.LastThrottled < throttlePause;
                        _ = engine.PollActiveAsync();
                        if (statistics is not null && tick % 60 == 0)
                        {
                            ReportStatistics(statistics, monitoring!, client, ref statisticsDue);
                        }
                        if (tick++ % 5 == 0)
                        {
                            push.Sync();
                        }
                        keeper.SaveIfChanged();
                        if (tray is not null && tick % 5 == 1)
                        {
                            var waiting = engine.Unsaved().Count;
                            tray.SetStatus(
                                tokens.SignInProblem ? $"{settings.Label}: sign-in needed"
                                    : waiting == 0 ? $"{settings.Label}: up to date"
                                    : $"{settings.Label}: {waiting} change(s) waiting to upload",
                                tokens.SignInProblem ? TrayState.Error : waiting == 0 ? TrayState.Normal : TrayState.Waiting);
                        }
                        if (DateTimeOffset.UtcNow >= nextSnapshot)
                        {
                            MetadataStore.Save(engine.ExportSnapshots(MetadataStore.MaxFoldersPerDrive), key);
                            nextSnapshot = DateTimeOffset.UtcNow + SnapshotInterval;
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        log($"error: {e.Message}");
                        monitoring?.Error("agent", e.Message);
                    }
                }
            });

            stop.WaitOne();
            if (navigationPane)
            {
                ExplorerLocation.Unregister();
            }
        }
        finally
        {
            tray?.Dispose();
            drive.Dispose();
        }

        due.Cancel();
        try
        {
            await scheduler;
        }
        catch (OperationCanceledException)
        {
        }
        await engine.ProcessDueAsync(true, default);
        keeper.SaveIfChanged();
        MetadataStore.Save(engine.ExportSnapshots(MetadataStore.MaxFoldersPerDrive), key);
        foreach (var unsaved in engine.Unsaved())
        {
            say($"Not saved:  {unsaved}");
        }
        if (statistics is not null)
        {
            statistics.Fold(client.Usage, monitoring!.Errors, DateTime.Now);
            statistics.Save();
            await monitoring.FlushAsync(TimeSpan.FromSeconds(3));
        }
        var usage = client.Usage;
        say($"Unmounted.  Requests: Graph {usage.GraphRequests}, SharePoint {usage.SharePointRequests}, about {usage.ResourceUnits} resource units " +
            $"in {stopwatch.Elapsed.TotalMinutes:N1} min; throttled {usage.Throttled} time(s), waited {usage.BudgetWaitSeconds} s for the budget");
        return 0;
    }

    static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Adds the last minute's usage to today's statistics. Finished days go out once, a few minutes after the start or after midnight
    /// (spread, so a tenant's devices don't all send at once), and are then dropped whether or not sending worked.
    /// </summary>
    static void ReportStatistics(UsageStatistics statistics, Monitoring monitoring, M365Client client, ref DateTime? due)
    {
        var now = DateTime.Now;
        statistics.Fold(client.Usage, monitoring.Errors, now);
        var completed = statistics.Completed(now);
        if (completed.Count > 0)
        {
            due ??= now + TimeSpan.FromSeconds(Random.Shared.Next(60, 15 * 60));
            if (now >= due)
            {
                foreach (var day in completed)
                {
                    monitoring.Statistics(day, UsageStatistics.Values(day, statistics.Days[day]));
                    statistics.Remove(day);
                }
                due = null;
            }
        }
        statistics.Save();
    }
}
