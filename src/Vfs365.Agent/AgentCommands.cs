using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
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
          vfs365 whoami   [--sign-in] [--config <file>]   sign in, show tenant and OneDrive; --sign-in shows Windows' sign-in window
          vfs365 discover [--config <file>] [--drives] [--json] [--full] [--audit]
                                                          find the user's libraries (--drives resolves Graph drive IDs, --full reads all metadata again,
                                                          --audit checks recall against site search, followed sites and hubs)
          vfs365 shared   [--config <file>] [--json]      what other people shared with you, as the Shared with me folder shows it
                                                          (owner, kind, drive and item ID, path in the owner's drive); reads it now
          vfs365 mount    [--config <file>] [--drive M:|*|None] [--verbose] [--trace] [--no-shell] [--discover-now]
                                                          mount OneDrive and the discovered libraries (--discover-now skips the
                                                          5 to 90 minute wait before discovery at a start with recent state)
          vfs365 unmount                                  stop the mount in this session
          vfs365 inspect  <path> [--versions]             item ID, size and tags of a path on the volume (OneDrive\...), a folder's
                                                          children, or a file's version count
          vfs365 put      <local file> <volume folder>    upload a file through Graph without the drive (scripted tests)
          vfs365 watch    <volume folder> [--seconds N] [--verbose]
                                                          show push notifications for the drive holding the folder
          vfs365 pin <url> | unpin <url> | pins           show a site or library (a copied browser link works) even where
                                                          search doesn't find it; takes effect at the next start or Restart
          vfs365 signout                                  stop the drive in this session and remove its local data: cached content,
                                                          folder listings and discovery state (unsaved changes stay); the next start
                                                          signs in again with the Windows account
          vfs365 why <link or name> [--check]             why a site or library shows on the drive or not (--check tests access and
                                                          search with a few calls)
          vfs365 health [--json] [--all-users]            exit 0 when the drive works, 1 with the reasons (--all-users: every signed-in
                                                          session, as SYSTEM or administrator)
          vfs365 support-bundle [--out <folder>]          one zip for the helpdesk (logs, settings, discovery, drive state), tokens and
                                                          secrets removed; nothing is uploaded
          vfs365 machine-setup [--dry-run]                put the VFS365 network provider in Windows' provider order
          vfs365 machine-stop [--cleanup] [--dry-run]     stop every user's agent; --cleanup also removes their cached data,
                                                          Explorer entries and the provider order entry (the installer runs both
                                                          as SYSTEM)
          vfs365 machine-start [--dry-run]                start the agent in every signed-in session without one (the
                                                          installer runs it as SYSTEM after an install or upgrade)
          vfs365 machine-report install|update|uninstall  report the event to the devices table when MonitoringUrl is set (the
                                                          installer runs it as SYSTEM)
          vfs365 about                                    version, licence and notices
        """;

    static readonly TimeSpan SessionEndWait = TimeSpan.FromSeconds(60);

    /// <summary>Listing the Shared with me folder reads the shared list again when it is older than this (and no try was this recent).</summary>
    static readonly TimeSpan SharedListedAge = TimeSpan.FromMinutes(5);
    static volatile ManualResetEventSlim? finished;
    static volatile Action<bool>? powerChanged;

    /// <summary>
    /// The session ends (logoff or shutdown that wasn't cancelled): drop the navigation pane entry right away, stop the mount, and wait up
    /// to a minute while pending changes upload (Windows shows the tray's block reason meanwhile).
    /// </summary>
    public static void StopForSessionEnd()
    {
        try
        {
            ExplorerLocation.Unregister();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        AgentControl.RequestStop();
        finished?.Wait(SessionEndWait);
    }

    /// <summary>The PC goes to sleep (false) or resumes (true), from vfs365-agent.exe.</summary>
    public static void PowerChanged(bool resumed) => powerChanged?.Invoke(resumed);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, bool timestamps = false, bool tray = false)
    {
        void Say(string line) => output.WriteLine(timestamps ? $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}" : line);
        void Log(string line) => output.WriteLine($"  {DateTime.Now:HH:mm:ss.fff} {line}");
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

        var command = args.FirstOrDefault()?.ToLowerInvariant();
        if (command is not ("settings" or "whoami" or "discover" or "shared" or "mount"or "unmount" or "inspect" or "put" or "watch" or "pin" or "unpin" or "pins" or "signout" or "machine-setup" or "machine-stop" or "machine-start" or "machine-report" or "about"
            or "why" or "health" or "support-bundle" or "search-spike"))
        {
            error.WriteLine(Usage);
            return 2;
        }

        // Spike 15, not in Usage: windows needs no sign-in, urls signs in below
        if (command == "search-spike" && args.ElementAtOrDefault(1)?.ToLowerInvariant() is var spike && spike is not ("urls" or "query"))
        {
            switch (spike)
            {
                case "windows":
                    return SearchSpike.Windows(args, AgentSettings.Load(Option("--config")), output.WriteLine);
                case "menu":
                    return SearchSpike.Menu(args, output.WriteLine);
                default:
                    error.WriteLine(SearchSpike.Usage);
                    return 2;
            }
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
                // Machine-wide steps first: a profile that can't be cleaned must not leave them behind. Folders still in VFS365 go
                // back before the data goes, which holds where they came from.
                MachineCleanup.RemoveExplorerEntries(dryRun, Say);
                ShellPackage.RemoveForAllUsers(dryRun, Say);
                NetworkProviderOrder.Remove(dryRun, Say);
                var profiles = MachineCleanup.ProfileDataFolders().ToList();
                MachineCleanup.RestoreKnownFolders(profiles, dryRun, Say);
                MachineCleanup.RemoveUserData(profiles, dryRun, Say);
            }
            return 0;
        }

        if (command == "machine-start")
        {
            MachineStart.Run(Flag("--dry-run"), Say);
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
            // Runs as SYSTEM: settings from policy and --config only, never a vfs365.local.json in the working directory
            if (MonitoringTarget.Parse(AgentSettings.Load(Option("--config"), workingDirectory: false).MonitoringUrl, out var problem) is not { } target)
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

        if (command is "why" or "health" or "support-bundle")
        {
            return await SupportCommands.RunAsync(command, args, Say, error);
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
                foreach (var line in SettingsReport.Lines(settings, tenant, UserPins.Load()))
                {
                    Say(line);
                }
                return 0;
            }

            if (command == "mount" && MonitoringTarget.Parse(settings.MonitoringUrl, out _) is { } monitoringTarget)
            {
                monitoring = new Monitoring(monitoringTarget, DeviceInfo.Current(), Say) { TenantId = () => tenant.Tenant, Detail = settings.MonitoringDetail };
            }

            Say($"Tenant:     {tenant.Tenant} ({tenant.Source})");
            if (settings.TenantId.Value is not null)
            {
                Say($"Detected:   {(join is null ? "nothing (device not joined or registered)" : $"{join.TenantId} (device {join.Kind})")}");
            }
            clientId = settings.ClientId.Value!;
            // The console tool runs in the foreground and may show a sign-in window; the background agent only at its start or on Sign in
            var tokens = new WamTokenSource(clientId, tenant.Tenant) { PromptWhenNeeded = !tray };
            if (monitoring is not null)
            {
                // Before this start's first token (a crash row, start problems) the account of the last day with statistics
                var lastUser = UsageStatistics.Load().LastUser();
                monitoring.User = () => tokens.LastResult?.Username ?? lastUser;
                monitoring.TenantId = () => tokens.LastResult?.TenantId ?? tenant.Tenant;
            }

            if (command == "signout")
            {
                if (AgentControl.RequestStop())
                {
                    // No time limit: the agent uploads pending changes before it stops, and the data below goes only after that
                    Say("Stopping:   the drive in this session (pending changes upload first)");
                    var waited = Stopwatch.StartNew();
                    var reported = 0;
                    while (AgentControl.IsMounted())
                    {
                        await Task.Delay(500);
                        if (waited.Elapsed.TotalSeconds >= reported + 10)
                        {
                            reported += 10;
                            Say($"Waiting:    the drive is still uploading changes ({reported} s)");
                        }
                    }
                }
                await tokens.SignOutAsync();
                WipeUserData();
                Say("Signed out: the drive is stopped and cached content, folder listings and discovery state are removed (unsaved changes in staging stay). " +
                    "The next start signs in again with the Windows account.");
                return 0;
            }
            using var http = new HttpClient(M365Client.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan }; // M365Client sets the deadlines
            var client = new M365Client(http, tokens, settings.ApiBudgetPerMinute > 0 ? new RequestBudget(settings.ApiBudgetPerMinute) : null)
            {
                ThrottlePause = TimeSpan.FromMinutes(settings.ThrottlePauseMinutes),
            };
            var api = new SharePointDiscoveryApi(client);
            var stopwatch = Stopwatch.StartNew();
            if (Flag("--verbose"))
            {
                client.Log = Log;
            }

            if (command == "whoami")
            {
                // --sign-in: through Windows' sign-in window, shown even when the Windows account would do (to test the prompt)
                if (Flag("--sign-in") && !await tokens.SignInAsync(userAsked: true, default, force: true))
                {
                    error.WriteLine($"Sign-in: {tokens.SignInProblemText}");
                    return 1;
                }
                var me = await api.GetMyDriveAsync(default);
                Say($"User:       {tokens.LastResult?.Username}");
                Say($"Signed in:  tenant {tokens.LastResult?.TenantId}");
                Say($"OneDrive:   {me.WebUrl}");
                Say($"SharePoint: {SharePointUrls.TenantRootFromOneDriveUrl(me.WebUrl)}");
                return 0;
            }

            if (command == "shared")
            {
                return await SharedAsync(settings, api, client, tokens, Flag("--json"), Say, output);
            }

            if (command == "search-spike")
            {
                return args.ElementAtOrDefault(1)?.ToLowerInvariant() == "query"
                    ? await SearchSpike.QueryAsync(args, api, client, Say)
                    : await SearchSpike.UrlsAsync(args, settings, api, client, Say);
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

            var discoveryOptions = DiscoveryOptionsOf(settings);

            if (command == "mount")
            {
                if (Option(AgentSupervisor.RestartedFlag) is { } crashCode)
                {
                    Say($"Restarted:  the agent stopped unexpectedly (exit code {crashCode}); the lines above say what Windows recorded");
                    monitoring?.Error("crash", $"The agent stopped unexpectedly (exit code {crashCode}) and was started again");
                }
                return await MountAsync(settings, Option("--config"), api, client, tokens, discoveryOptions, tenant.Tenant, background: tray,
                    (tray || Flag("--tray")) && settings.TrayIcon, Option("--after"), Option("--drive"), Flag("--trace"), !Flag("--no-shell") && settings.NavigationPane,
                    Flag("--discover-now"), monitoring, stopwatch, Say, Log, error);
            }

            var previous = DiscoveryStore.Load();
            var result = await new DiscoveryService(api, Flag("--full") ? discoveryOptions with { MetadataMaxAge = TimeSpan.Zero } : discoveryOptions)
                .RunAsync(previous, default, null, tokens.LastResult?.TenantId);
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
            DiscoveryStore.Save(DiscoverySchedule.Keep(result, previous) with { Libraries = libraries, TenantHint = tenant.Tenant });
            DiscoveryReport.Record(result, previous);

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
                Say($"  {$"{library.SiteTitle} / {library.LibraryTitle}",-60} {library.ItemCount,10:N0} items  {library.SiteTemplate ?? "?",-20}{(library.ReadOnly ? "  read-only" : "")}{(library.DriveId is null ? "" : $"  {library.DriveId}")}");
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
        catch (Exception e) when (e is SignInException or M365RequestException or HttpRequestException or FileNotFoundException or JsonException or IOException
            or UnauthorizedAccessException or ArgumentException)
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

    /// <summary>Discovery's options from the settings: site patterns and templates from policy, pins from policy and the user.</summary>
    internal static DiscoveryOptions DiscoveryOptionsOf(AgentSettings settings)
    {
        var options = new DiscoveryOptions
        {
            PinnedLocations = [.. settings.PinnedLocations, .. UserPins.Load()],
            IncludedSiteTemplates = settings.IncludedSiteTemplates,
            ExcludedSiteTemplates = settings.ExcludedSiteTemplates,
        };
        if (settings.IncludedSites is { } included)
        {
            options = options with { IncludedSites = included };
        }
        if (settings.ExcludedSites is { } excluded)
        {
            options = options with { ExcludedSites = excluded };
        }
        return options;
    }

    /// <summary>
    /// \VFS365\&lt;Windows user name&gt;. UNC prefixes are machine-wide, so each user on a multi-session host needs their own;
    /// characters not allowed in a share name become '_'.
    /// </summary>
    public static string UncPrefix(string userName) =>
        $@"\VFS365\{string.Concat(userName.Select(c => char.IsControl(c) || "\"/\\[]:|<>+=;,?*".Contains(c) ? '_' : c))}";

    /// <summary>
    /// The holder of this user's UNC path (DOMAIN\name) when it is another account with the same user name, in another domain or on
    /// another machine (CONTOSO\ann and FABRIKAM\ann, or a local ann and AzureAD\ann); null otherwise.
    /// </summary>
    public static string? SameNameHolder(string? holder, string domain, string user)
    {
        if (holder?.Split('\\') is not [var holderDomain, var holderName])
        {
            return null;
        }
        return UncPrefix(holderName).Equals(UncPrefix(user), StringComparison.OrdinalIgnoreCase) && !holderDomain.Equals(domain, StringComparison.OrdinalIgnoreCase)
            ? holder
            : null;
    }

    /// <summary>DOMAIN\name of a SID, or null when Windows can't say.</summary>
    static string? AccountOf(string? sid)
    {
        try
        {
            return sid is null ? null : new System.Security.Principal.SecurityIdentifier(sid).Translate(typeof(System.Security.Principal.NTAccount)).Value;
        }
        catch (Exception e) when (e is SystemException)
        {
            return null;
        }
    }

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
        File.Delete(DiscoveryReport.FilePath);
        File.Delete(CacheKey.FilePath);
    }

    /// <summary>An engine without a mount, for the diagnostic commands: OneDrive plus the libraries and shared items of the last discovery.</summary>
    internal static async Task<(DriveEngine Engine, DriveApi DriveApi)> ToolEngineAsync(AgentSettings settings, SharePointDiscoveryApi api, M365Client client)
    {
        var oneDrive = await api.GetMyDriveAsync(default);
        var driveApi = new DriveApi(client);
        var state = DiscoveryStore.Load();
        var engine = new DriveEngine(
            DriveNamespace.Build(oneDrive, state?.Libraries ?? [], api.GetLibraryDriveIdAsync, settings.Scope,
                shared: settings.SharedWithMe ? state?.Shared?.Items ?? [] : null),
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

    /// <summary>`vfs365 shared`: what the Shared with me folder's source returns, every item looked up, for support.</summary>
    static async Task<int> SharedAsync(AgentSettings settings, SharePointDiscoveryApi api, M365Client client, WamTokenSource tokens, bool json,
        Action<string> say, TextWriter output)
    {
        var me = await api.GetMyDriveAsync(default);
        var user = tokens.LastResult?.Username ?? throw new IOException("The signed-in account's name is unknown");
        var sharedApi = new SharedWithMeApi(client);
        var result = await new SharedWithMeService(sharedApi).RunAsync(user, me.DriveId, null, recheck: true, default);
        var names = DriveNamespace.SharedNames(result.State.Items);
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                user,
                result.Hits,
                result.SearchError,
                result.Errors,
                Items = result.State.Items.Select(i => new { ShownAs = names.GetValueOrDefault(i.Key), i.Name, i.OwnerName, i.OwnerEmail, Kind = i.IsFolder ? "folder" : "file", i.DriveId, i.ItemId, i.Path, i.ReadOnly, i.Size, i.Modified, i.WebUrl }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            return result.SearchError is null ? 0 : 1;
        }
        var shown = settings.Scope != DriveScope.All ? $"; Scope is {settings.Scope}, so the drive doesn't show them (Scope All does)"
            : !settings.SharedWithMe ? "; policy SharedWithMe is 0, so the drive doesn't show them" : "";
        say($"User:       {user}");
        say($"Search:     {result.Hits} hits (duplicates and your own items left out), {result.SearchError ?? "complete"}");
        say($"Shared:     {result.State.Items.Count} items ({result.State.Items.Count(i => i.IsFolder)} folders) from other people{shown}");
        foreach (var item in result.State.Items.OrderBy(i => names.GetValueOrDefault(i.Key), StringComparer.OrdinalIgnoreCase))
        {
            say($@"  \{DriveNamespace.SharedFolderName}\{names.GetValueOrDefault(item.Key) ?? item.Name}");
            say($"      {(item.IsFolder ? "folder" : "file")}{(item.ReadOnly ? ", view only" : "")}, shared by {item.OwnerName ?? "?"}{(item.OwnerEmail is null ? "" : $" <{item.OwnerEmail}>")}");
            say($"      drive {item.DriveId}, item {item.ItemId}");
            say($"      path in the owner's drive: /{item.Path}");
        }
        foreach (var problem in result.Errors)
        {
            say($"Error:      {problem}");
        }
        say($"Requests:   Graph {client.GraphRequests}, SharePoint {client.SharePointRequests}");
        say("Search covers sharing with named people; files opened through an \"anyone\" or organization link aren't listed.");
        return result.SearchError is null ? 0 : 1;
    }

    /// <summary>
    /// Mounts and runs until stopped. In the background agent a start never ends because of a passing problem: without network or a
    /// sign-in it waits and tries again (tray: "waiting for the network", "waiting for sign-in"); other problems keep the tray icon in
    /// an error state with the cause, Retry and Show log. A drive letter that is in use or not valid gives a drive without a letter.
    /// </summary>
    static async Task<int> MountAsync(AgentSettings settings, string? configPath, SharePointDiscoveryApi api, M365Client client, WamTokenSource tokens,
        DiscoveryOptions discoveryOptions, string tenantHint, bool background, bool showTray, string? restartAfter, string? driveOption, bool trace,
        bool navigationPane, bool discoverNow, Monitoring? monitoring, Stopwatch stopwatch, Action<string> say, Action<string> log, TextWriter error)
    {
        var done = finished = new ManualResetEventSlim(false);
        Tray? tray = null;
        var wake = new SemaphoreSlim(0, 1);
        void WakeUp()
        {
            try
            {
                wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
        var networkUp = NetworkInterface.GetIsNetworkAvailable();
        var networkFailedAt = DateTimeOffset.MinValue;
        StatusFile? status = null; // status.json for 'vfs365 health', once this agent owns the session
        // Throttled responses, grouped into episodes for the log and monitoring (who, when, how much)
        var throttling = new ThrottleEpisodes();
        client.Throttled = throttling.Add;
        void ReportThrottling(DateTimeOffset now)
        {
            foreach (var episode in throttling.Ended(now))
            {
                say($"Throttled:  {episode.Describe()}");
                monitoring?.Throttling(episode);
                AgentEvents.Write(AgentEvent.Throttled, episode.Describe());
            }
        }
        DriveEngine? restoredEngine = null; // set once mounted: change feeds that failed meanwhile are read again soon
        Action? layoutSoon = null; // set once mounted: folder redirection and drive letters again (network back, a first sign-in)
        void NetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
        {
            networkUp = e.IsAvailable;
            say(e.IsAvailable ? "Network:    available" : "Network:    unavailable");
            if (e.IsAvailable)
            {
                networkFailedAt = DateTimeOffset.MinValue;
                restoredEngine?.NetworkRestored();
                layoutSoon?.Invoke(); // targets in libraries that couldn't be read offline
                WakeUp();
            }
        }
        var prefix = UncPrefix(Environment.UserName);
        var unc = $@"\{prefix}";
        var restarting = false; // a new agent takes over: redirected folders stay where they are
        var ownsSession = false; // this agent created the session's stop event (not a second start that ends at once)
        var layoutRestored = false;
        FolderRedirection? redirection = null;
        SessionDriveLetters? letters = null;
        using var layoutCancel = new CancellationTokenSource(); // layout work (folders, letters, moving content) stops at unmount

        // Redirected folders back (unless a new agent takes them over) and the letters for folders gone, each on its own; also after a
        // stop before the mount, for what a crashed run left. Never throws.
        void RestoreLayout(bool restoreFolders, string? driveLetter)
        {
            layoutRestored = true;
            if (restoreFolders)
            {
                try
                {
                    redirection ??= new FolderRedirection(new ShellKnownFolders(), Path.Combine(DiscoveryStore.DataDirectory, FolderRedirection.StateFileName),
                        path => VolumePaths.OnVolume(path, unc, null));
                    var (back, failed) = redirection.RestoreAll();
                    foreach (var line in back)
                    {
                        say($"Redirected: {line}");
                    }
                    foreach (var line in failed)
                    {
                        say($"Warning:    {line}");
                        monitoring?.Error("redirection", line);
                        AgentEvents.Write(AgentEvent.RedirectionProblem, line);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    say($"Warning:    could not put redirected folders back: {e.Message}");
                }
            }
            try
            {
                letters ??= new SessionDriveLetters(unc);
                foreach (var left in letters.RemoveLeftovers([], driveLetter))
                {
                    say($"Drive:      {left} removed, left by an earlier run");
                }
                foreach (var problem in letters.RemoveAll())
                {
                    say($"Warning:    could not remove drive letter {problem}");
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                say($"Warning:    could not remove drive letters: {e.Message}");
            }
        }

        NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
        try
        {
            var letter = DriveLetterOption.Parse(driveOption ?? settings.DriveLetter);

            var signInNotice = DateTimeOffset.MinValue;
            void SignInFromTray() => _ = Task.Run(async () =>
            {
                try
                {
                    if (await tokens.SignInAsync(userAsked: true, default))
                    {
                        say("Sign-in:    done from the tray");
                        WakeUp();
                    }
                    else
                    {
                        say("Sign-in:    from the tray not done (cancelled, failed as logged, or a sign-in window was open already)");
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    say($"Sign-in:    from the tray failed: {e.Message}");
                }
            });
            Action? accountSwitchedNow = null; // set once mounted
            var accountSwitched = false;
            tokens.SignedIn = result =>
            {
                say($"Signed in:  {result.Username} (tenant {result.TenantId})");
                layoutSoon?.Invoke(); // %UPN% in targets: a start without a token couldn't resolve them
            };
            tokens.AccountSwitched = (from, to) =>
            {
                say($"Sign-in:    {to.Username} signed in instead of {from.Username}; the tokens of {from.Username} are forgotten");
                accountSwitched = true;
                accountSwitchedNow?.Invoke();
            };
            tokens.OwnerWindow = () => tray?.Handle ?? IntPtr.Zero;
            tokens.SignInFailed = e =>
            {
                say($"Sign-in:    needed, {SignInText.Of(e, CultureInfo.InvariantCulture)} ({e.Code}: {e.Message.Split('\n')[0].Trim()})");
                monitoring?.Error("sign-in", e.Message);
                AgentEvents.Write(AgentEvent.SignInNeeded, $"Sign-in needed: {SignInText.Of(e, CultureInfo.InvariantCulture)} ({e.Code}: {e.Message.Split('\n')[0].Trim()})");
                status?.Update(s => s with { SignInProblem = e.Code });
                if (tray is { } shown && DateTimeOffset.UtcNow - signInNotice > TimeSpan.FromMinutes(10))
                {
                    signInNotice = DateTimeOffset.UtcNow;
                    shown.Notify(UiText.Get("SignIn.Title"), UiText.Get("SignIn.Text", SignInText.Of(e)), SignInFromTray);
                }
            };
            var startActions = new TrayActions(settings.HelpUrl)
            {
                Retry = WakeUp,
                SignIn = SignInFromTray,
                SignInNeeded = () => tokens.SignInProblem,
                SessionEnded = StopForSessionEnd,
                CollectDiagnostics = () => SupportBundle.CollectFromTray(configPath, say, (title, text) => tray?.Notify(title, text)),
            };
            if (showTray)
            {
                tray = new Tray(settings.Label, startActions, log);
            }

            // Restarted from the tray: the old agent uploads what is pending and unmounts first, however long that takes
            if (int.TryParse(restartAfter, out var previous))
            {
                await WaitForAgentAsync(previous, tray, say);
            }
            EventWaitHandle stop;
            for (var attempt = 0; ; attempt++)
            {
                stop = AgentControl.CreateStopEvent(out var createdNew);
                if (createdNew)
                {
                    ownsSession = true;
                    break;
                }
                stop.Dispose();
                if (restartAfter is null || attempt >= 60)
                {
                    error.WriteLine("Already mounted in this session; run 'vfs365 unmount' first.");
                    return 1;
                }
                await Task.Delay(1000);
            }
            using var stopEvent = stop;
            var agentStatus = status = new StatusFile(StatusFile.DefaultPath, AgentStatus.ForThisProcess(DateTimeOffset.UtcNow));
            log = agentStatus.Tap(log); // "error: " lines become the status's last error
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Set();
            };

            // Waits for the delay, Retry in the tray, a sign-in or the network coming back. False when the agent is to stop instead.
            async Task<bool> PauseAsync(TimeSpan delay)
            {
                var until = DateTime.UtcNow + delay;
                while (DateTime.UtcNow < until && !stop.WaitOne(0))
                {
                    if (await wake.WaitAsync(TimeSpan.FromSeconds(1)))
                    {
                        break;
                    }
                }
                return !stop.WaitOne(0);
            }
            string? lastProblem = null;
            // A start that can't go on: logged and reported once per cause, shown in the tray, tried again after a growing pause
            async Task<bool> WaitAfterAsync(Exception e, int attempt, string what)
            {
                var problem = StartProblem.Of(e, !networkUp);
                var english = StartProblem.Of(e, !networkUp, CultureInfo.InvariantCulture);
                // A problem StartProblem knows only by its exception text gets plain words, in the display language too
                var reason = problem.Wait == StartWait.Retry && problem.Reason == e.Message ? StartText.Plain(e) : problem.Reason;
                var englishReason = english.Wait == StartWait.Retry && english.Reason == e.Message ? StartText.Plain(e, CultureInfo.InvariantCulture) : english.Reason;
                var delay = problem.Wait == StartWait.Retry ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(Math.Min(300, 15 << Math.Min(attempt, 5)));
                if (english.Reason != lastProblem)
                {
                    lastProblem = english.Reason;
                    say($"Waiting:    {what}: {englishReason} ({e.Message.Split('\n')[0].Trim()}); trying again in {delay.TotalMinutes:0.#} min, or at Retry in the tray");
                    if (problem.Wait == StartWait.Retry)
                    {
                        monitoring?.Error("start", e.Message);
                        AgentEvents.Write(AgentEvent.NotStarted, $"VFS365 didn't start, {what}: {englishReason} ({e.Message.Split('\n')[0].Trim()}); it tries again in an hour or at Retry in the tray");
                        tray?.Notify(UiText.Get("Failed.Title"), UiText.Get("Failed.Text", Sentence(reason)));
                    }
                }
                else
                {
                    log($"start: {what} failed again: {e.Message}");
                }
                status?.Update(s => s with
                {
                    State = problem.Wait == StartWait.Retry ? AgentStatus.NotStarted : AgentStatus.Waiting,
                    Problem = $"{what}: {englishReason}",
                    SignInProblem = tokens.SignInProblem ? s.SignInProblem : null,
                });
                tray?.SetStatus(problem.Wait switch
                {
                    StartWait.Network => UiText.Get("Status.WaitingNetwork"),
                    StartWait.SignIn => UiText.Get("Status.WaitingSignIn"),
                    _ => UiText.Get("Status.NotStarted", reason.TrimEnd('.')),
                }, problem.Wait == StartWait.Retry ? TrayState.Error : TrayState.Waiting);
                return await PauseAsync(delay);
            }

            // Mount with what the last run knew (libraries, drive IDs, folder listings), so Explorer shows the drive at once and without
            // network calls; discovery and the change feeds bring it up to date in the background. The first run only needs the OneDrive.
            // The profile's files can be locked or unreadable for a while (antivirus, a profile disk): retried like any start problem
            byte[] key;
            ContentCache cache;
            DiscoveryState? saved;
            bool usable;
            var wipe = false;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    key = CacheKey.LoadOrCreate();
                    cache = new ContentCache(Path.Combine(DiscoveryStore.DataDirectory, "cache"), new DriveApi(client),
                        new ContentCacheOptions { MaxBytes = settings.CacheSizeMB * (1L << 20), Cipher = new CacheCipher(key) });
                    saved = DiscoveryStore.Load();
                    wipe |= DeviceManagement.LeftManagement(); // true once: kept until the wipe below is done
                    if (wipe)
                    {
                        // The device left Intune: company content cached here goes, and nothing from before is shown
                        cache.Clear();
                        MetadataStore.Delete();
                        File.Delete(DiscoveryStore.FilePath);
                        File.Delete(DiscoveryReport.FilePath);
                        say("Wiped:      cached content and folder listings (the device is no longer managed)");
                        wipe = false;
                        saved = null;
                    }
                    usable = saved is { OneDrive: not null } && saved.TenantHint == tenantHint;
                    if (saved is not null && !usable)
                    {
                        // Another tenant than last time: what was cached belongs to the other account
                        cache.Clear();
                        MetadataStore.Delete();
                    }
                    break;
                }
                catch (Exception e) when (background && e is IOException or UnauthorizedAccessException)
                {
                    if (!await WaitAfterAsync(e, attempt, "reading VFS365's files in the profile"))
                    {
                        return 0;
                    }
                }
            }
            // The last run's folder listings are read and decrypted meanwhile (about a second for a large one), imported before the mount
            var snapshots = usable ? Task.Run(() => MetadataStore.Load(key)) : null;
            MyDrive oneDrive;
            MyDrive? justRead = null; // this start's /me/drive answer, which the first discovery then needn't ask for again
            if (usable)
            {
                oneDrive = saved!.OneDrive!;
                client.TenantRootHost = new Uri(saved.TenantRoot).Host; // tokens only for this tenant's SharePoint (set by GetMyDriveAsync otherwise)
            }
            else
            {
                // The first start needs Microsoft 365; the background agent may show the sign-in window here (once; not again for a
                // while after a cancel), never later
                tokens.PromptWhenNeeded |= background;
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        oneDrive = justRead = await api.GetMyDriveAsync(default);
                        break;
                    }
                    catch (Exception e) when (background)
                    {
                        if (!await WaitAfterAsync(e, attempt, "the first start needs Microsoft 365"))
                        {
                            return 0;
                        }
                    }
                }
                tokens.PromptWhenNeeded = !background;
            }
            var keeper = new DiscoveryStateKeeper(usable ? saved! : DiscoverySchedule.Placeholder(oneDrive, tenantHint, settings.Scope, DateTimeOffset.UtcNow));
            if (!usable)
            {
                keeper.Touch(); // the next start mounts at once, also in OneDrive scope (which has no discovery to save it)
            }

            var driveApi = new DriveApi(client);
            DriveHost? drive = null;
            var notices = new NoticeBatcher(settings.NotifyUploads);
            // Shared with me: only with Scope All; shown at once with the last run's list, read again in the background
            var sharedShown = settings.Scope == DriveScope.All && settings.SharedWithMe;
            Action? sharedListed = null;
            var ns = DriveNamespace.Build(oneDrive, settings.Scope == DriveScope.OneDrive ? [] : keeper.Current.Libraries, api.GetLibraryDriveIdAsync, settings.Scope,
                (library, driveId) => keeper.RememberDriveId(library.Key, driveId), sharedShown ? keeper.Current.Shared?.Items ?? [] : null);
            var engine = new DriveEngine(
                ns,
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
                    SharedListed = () => sharedListed?.Invoke(),
                    Notice = notice =>
                    {
                        if (notice.Kind == NoticeKind.UploadWaiting && PlainCause.Of(notice.Detail) == Cause.Offline)
                        {
                            networkFailedAt = DateTimeOffset.UtcNow;
                        }
                        if (notice.Kind is NoticeKind.UploadWaiting or NoticeKind.UploadDone)
                        {
                            status?.FileWaiting(notice.Path ?? notice.Name, notice.Kind == NoticeKind.UploadWaiting ? notice.Detail ?? "unknown cause" : null);
                        }
                        if (showTray)
                        {
                            notices.Add(notice, PlainCause.Of(notice.Detail, !networkUp, tokens.SignInProblem), DateTimeOffset.UtcNow);
                        }
                        if (notice.Kind == NoticeKind.ConflictCopy)
                        {
                            monitoring?.Error("conflict copy", $"{notice.Path ?? notice.Name} was changed or locked in Microsoft 365; saved as {notice.Detail}",
                                notice.Path, notice.Name, notice.Detail);
                            AgentEvents.Write(AgentEvent.ConflictCopy, $"{notice.Path ?? notice.Name} was changed or locked in Microsoft 365; the user's version is saved as {notice.Detail}");
                        }
                        else if (notice.Kind == NoticeKind.UploadWaiting)
                        {
                            monitoring?.Error("upload", $"{notice.Path ?? notice.Name}: {notice.Detail}", notice.Path, notice.Name);
                            AgentEvents.Write(AgentEvent.UploadWaiting, $"{notice.Path ?? notice.Name} is waiting to upload, kept on this PC: {notice.Detail}");
                        }
                        else if (notice.Kind == NoticeKind.NotDeleted)
                        {
                            monitoring?.Error("delete", $"{notice.Path ?? notice.Name} could not be deleted: {notice.Detail}", notice.Path, notice.Name);
                            AgentEvents.Write(AgentEvent.DeleteRefused, $"{notice.Path ?? notice.Name} could not be deleted in Microsoft 365 and is back: {notice.Detail}");
                        }
                    },
                });
            restoredEngine = engine;

            say(AgentControl.Publisher);
            say(AgentControl.Banner);
            // Only what differs from the defaults, with where it comes from ('vfs365 settings' shows them all)
            var changed = settings.Effective().Where(s => s.Source != AgentSettings.Default).ToList();
            say(changed.Count == 0 ? "Settings:   all defaults" : $"Settings:   {changed.Count} not default");
            foreach (var (name, value, source) in changed)
            {
                say($"Setting:    {name} = {value} ({source})");
            }
            foreach (var adjusted in settings.Adjusted())
            {
                say($"Warning:    {adjusted}");
                AgentEvents.Write(AgentEvent.SettingWarning, adjusted);
            }
            _ = MonitoringTarget.Parse(settings.MonitoringUrl, out var monitoringProblem);
            say(monitoring?.Started ?? (monitoringProblem is null ? Monitoring.OffHint : $"Monitoring: off, {monitoringProblem}"));
            if (monitoringProblem is not null)
            {
                AgentEvents.Write(AgentEvent.SettingWarning, $"Monitoring: off, {monitoringProblem}");
            }
            var recovered = engine.Recover();
            if (recovered > 0)
            {
                say($"Recovered:  {recovered} unsaved file(s) from an earlier run, uploading");
                AgentEvents.Write(AgentEvent.Recovered, $"{recovered} unsaved file(s) from an earlier run are being uploaded");
            }
            if (snapshots is not null)
            {
                engine.ImportSnapshots(await snapshots);
            }
            var savedVersion = engine.MetadataVersion; // folder listings are saved again only once they changed

            // Letters for folders (policy DriveMappings) are known before the mount, so DriveLetter * doesn't take one of them
            var layoutProblems = new List<string>();
            var redirectRules = RedirectRule.Parse(settings.FolderRedirection, layoutProblems);
            var mappings = DriveMapping.Parse(settings.DriveMappings, letter.KeepLetter ? letter.MountPoint : null, layoutProblems);
            if (letter is { KeepLetter: true, MountPoint: null } && mappings.Count > 0)
            {
                letter = letter with { MountPoint = DriveMapping.FreeLetter(mappings, l => DosDevices.Target(l) is not null) };
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
            Action<string>? fsTrace = trace ? line => log($"fs {line}") : null;
            void LetterProblem(string problem, string shown)
            {
                say($"Warning:    {problem}; the drive is mounted without a letter, at {unc}{(navigationPane ? " and in the navigation pane" : "")}");
                monitoring?.Error("drive letter", problem);
                AgentEvents.Write(AgentEvent.DriveLetterProblem, $"{problem}; the drive is mounted without a letter, at {unc}");
                tray?.Notify(UiText.Get("Letter.Title"), UiText.Get(navigationPane ? "Letter.Text" : "Letter.TextNoPane", shown, unc));
            }
            // The letter from policy; when it is taken (or not valid) the drive still mounts, by UNC path and in the navigation pane. A held
            // UNC path is no letter problem (the user's own drive in another session, or another user's): it goes to the loop below.
            DriveHost MountDrive()
            {
                if (letter.KeepLetter)
                {
                    try
                    {
                        return DriveHost.Mount(engine, letter.MountPoint, true, prefix, settings.Label, fsLog, fsTrace);
                    }
                    catch (IOException e) when (e is not MountConflictException)
                    {
                        var holder = letter.MountPoint is { } wanted ? DriveLetterReport.Read(wanted).Target : null;
                        LetterProblem($"{letter.MountPoint ?? "No free drive letter"}: {(holder is null ? e.Message : $"in use ({holder})")}", letter.MountPoint ?? "*");
                    }
                }
                else if (letter.Problem is { } invalid)
                {
                    LetterProblem(invalid, driveOption ?? settings.DriveLetter);
                }
                return DriveHost.Mount(engine, null, false, prefix, settings.Label, fsLog, fsTrace);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    drive = MountDrive();
                    break;
                }
                catch (Exception e) when (background && IsMountConflict(e))
                {
                    // Another user's volume holds this user's UNC path: apps with stored paths would save there. Tried again hourly and on Retry.
                    var sameName = SameNameHolder(AccountOf((e as MountConflictException)?.Owner), Environment.UserDomainName, Environment.UserName);
                    var message = sameName is null ? e.Message
                        : $@"{unc} is held by {sameName}, another account with the same user name as this one ({Environment.UserDomainName}\{Environment.UserName}). " +
                          "UNC paths go by user name, so only one of them can have VFS365's drive on this PC at a time; files saved to that path would go to " +
                          $"the other account's drive, so VFS365 doesn't mount. Sign {sameName} out of this PC. ({e.Message})";
                    if (lastProblem != message)
                    {
                        lastProblem = message;
                        say($"Security:   {message} The drive is not started; Retry in the tray tries again.");
                        monitoring?.Error("security", message);
                        AgentEvents.Write(AgentEvent.UncPathTaken, $"{message} The drive is not started.");
                        status?.Update(s => s with { State = AgentStatus.NotStarted, Problem = $"{unc} is held by another account" });
                        tray?.Notify(UiText.Get("Security.Title"), sameName is null ? UiText.Get("Security.Text", unc) : UiText.Get("Security.SameName", unc, sameName));
                    }
                    tray?.SetStatus(UiText.Get("Status.Taken", unc), TrayState.Error);
                    if (!await PauseAsync(TimeSpan.FromHours(1)))
                    {
                        return 0;
                    }
                }
                catch (Exception e) when (background)
                {
                    if (!await WaitAfterAsync(e, attempt, "mounting"))
                    {
                        return 0;
                    }
                }
            }

            // Discovery runs one at a time: at the start (after a wait when the last run's state is recent), daily, and on Refresh in the tray
            var discoveryGate = new SemaphoreSlim(1, 1);
            using var startupDiscovery = new CancellationTokenSource();
            var refreshing = false;
            var accountChanged = false;
            var nextDiscovery = DateTimeOffset.MaxValue;
            var discoveryFailures = 0;
            DateTimeOffset NextDay() => DiscoverySchedule.NextDay(DateTimeOffset.UtcNow, Random.Shared);

            // The agent's own state files (discovery state, folder listings, Refresh time): a failed write is logged once per cause and
            // tried again later; it never ends the agent or skips the saves after it
            var saveProblems = new Dictionary<string, string>(StringComparer.Ordinal);
            bool SaveQuietly(string what, Action save)
            {
                try
                {
                    save();
                    bool recovered;
                    lock (saveProblems)
                    {
                        recovered = saveProblems.Remove(what);
                    }
                    if (recovered)
                    {
                        say($"Saved:      {what} again");
                    }
                    return true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    lock (saveProblems)
                    {
                        if (saveProblems.GetValueOrDefault(what) == e.Message)
                        {
                            return false;
                        }
                        saveProblems[what] = e.Message;
                    }
                    say($"Warning:    could not save {what}, trying again later: {e.Message}");
                    monitoring?.Error("agent", $"could not save {what}: {e.Message}");
                    return false;
                }
            }

            // A new agent with the same arguments waits for this one to upload what is pending and unmount, then mounts. False when it
            // couldn't be started: this one goes on.
            bool RestartAgent(string why)
            {
                say($"Restarting: {why}");
                AgentEvents.Write(AgentEvent.Restarting, $"VFS365 restarts {why}");
                // Without --child (and the last crash's --restarted) the new process is a supervisor of its own; this one's ends with it
                var arguments = Environment.GetCommandLineArgs().Skip(1)
                    .Where(a => !a.Equals(AgentSupervisor.ChildFlag, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var option in new[] { "--after", AgentSupervisor.RestartedFlag })
                {
                    if (arguments.FindIndex(a => a.Equals(option, StringComparison.OrdinalIgnoreCase)) is var at and >= 0)
                    {
                        arguments.RemoveRange(at, Math.Min(2, arguments.Count - at));
                    }
                }
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in arguments.Append("--after").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)))
                {
                    start.ArgumentList.Add(argument);
                }
                try
                {
                    Process.Start(start)?.Dispose();
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    say($"Warning:    the new agent could not be started ({e.Message}); this one goes on");
                    return false;
                }
                restarting = true; // only now: redirected folders stay where they are for the new agent to take over
                stop.Set();
                return true;
            }

            // Folder redirection and drive letters for folders (policies FolderRedirection, DriveMappings): after the mount, after each
            // discovery (targets in libraries known only then), hourly, after resume and when the network returns (a letter taken, a
            // folder moved back), and at the first sign-in. One run at a time; one asked for meanwhile runs after it. Moving content
            // (|move) runs on its own afterwards, so it never holds up the next run. All of it stops at unmount.
            foreach (var problem in layoutProblems)
            {
                say($"Warning:    {problem}");
                AgentEvents.Write(AgentEvent.SettingWarning, problem);
            }
            redirection = new FolderRedirection(new ShellKnownFolders(), Path.Combine(DiscoveryStore.DataDirectory, FolderRedirection.StateFileName),
                path => VolumePaths.OnVolume(path, unc, drive?.DriveLetter));
            var driveLetters = letters = new SessionDriveLetters(unc);
            var folderRedirection = redirection;
            var leftoversChecked = false;
            var moving = 0; // content moving (StartMoves) runs
            var layoutReported = new HashSet<string>(StringComparer.Ordinal);
            void LayoutProblem(string problem)
            {
                lock (layoutReported)
                {
                    if (!layoutReported.Add(problem))
                    {
                        return;
                    }
                }
                say($"Warning:    {problem}");
                monitoring?.Error("redirection", problem);
                AgentEvents.Write(AgentEvent.RedirectionProblem, problem);
            }
            var layout = new SingleRun(async ct =>
            {
                if (drive is { } mounted)
                {
                    RequestPriority.MarkBackground();
                    await ApplyLayoutOnceAsync(mounted, ct);
                    StartMoves();
                }
            }, e => LayoutProblem($"folder redirection and drive mappings: {e.Message}"));
            Task ApplyLayoutAsync() => redirectRules.Count == 0 && mappings.Count == 0 ? Task.CompletedTask : layout.RunAsync(layoutCancel.Token);
            async Task ApplyLayoutOnceAsync(DriveHost mounted, CancellationToken ct)
            {
                // The signed-in account names folders too (%UPN%, %UPNNAME%); a warm start has no token yet
                if (tokens.LastResult is null && redirectRules.Select(r => r.Target).Concat(mappings.Select(m => m.Target)).Any(t => t.Contains("%UPN", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        await tokens.GetAccessTokenAsync(M365Client.GraphResource, ct);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                    }
                }
                var upn = tokens.LastResult?.Username;
                var values = new Dictionary<string, string?> { ["UPN"] = upn, ["UPNNAME"] = upn?.Split('@')[0] };
                async Task<(string? Path, string? Problem)> Resolve(string what, string template)
                {
                    var expanded = FolderTargets.Expand(template, values);
                    var relative = expanded is null ? null : VolumePaths.Relative(expanded, unc, mounted.DriveLetter);
                    if (relative is null)
                    {
                        return (null, expanded is null ? $"{what}: '{template}' names a value that isn't known (yet)"
                            : $"{what}: '{expanded}' is not in VFS365; give a path in it, like OneDrive\\Documents");
                    }
                    var (path, problem) = await FolderTargets.ResolveAsync(engine, relative, create: true, ct);
                    return (path, problem is null ? null : $"{what}: {problem}");
                }

                if (!leftoversChecked)
                {
                    leftoversChecked = true;
                    foreach (var leftover in driveLetters.RemoveLeftovers(mappings.Select(m => m.Letter), mounted.DriveLetter))
                    {
                        say($"Drive:      {leftover} removed, left by an earlier run and no longer in policy DriveMappings");
                    }
                }
                // Each mapping on its own: one that fails never keeps the others or the folders from their place
                foreach (var mapping in mappings)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var (path, problem) = await Resolve($"DriveMappings {mapping.Letter}", mapping.Target);
                        ct.ThrowIfCancellationRequested();
                        if (path is not null)
                        {
                            var folder = VolumePaths.Windows(path, unc, null);
                            problem = driveLetters.Define(mapping.Letter, folder, mapping.Label, out var replaced);
                            if (replaced is not null)
                            {
                                say($"Drive:      {mapping.Letter} was a SharePoint WebDAV mapping to {replaced}; it is now {folder}");
                            }
                        }
                        if (problem is not null)
                        {
                            LayoutProblem(problem);
                        }
                    }
                    catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
                    {
                        LayoutProblem($"DriveMappings {mapping.Letter}: {e.Message}");
                    }
                }
                if (driveLetters.TakeLabelsChanged())
                {
                    ExplorerLocation.Refresh(); // once for all names written
                }

                var targets = new List<(RedirectRule, string)>();
                foreach (var rule in redirectRules)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var (path, problem) = await Resolve($"FolderRedirection {rule.Folder}", rule.Target);
                        if (path is null)
                        {
                            LayoutProblem(problem!);
                        }
                        else
                        {
                            targets.Add((rule, VolumePaths.Windows(path, unc, mounted.DriveLetter)));
                        }
                    }
                    catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
                    {
                        LayoutProblem($"FolderRedirection {rule.Folder}: {e.Message}");
                    }
                }
                ct.ThrowIfCancellationRequested();
                var (done, left, _) = folderRedirection.Apply(targets, redirectRules, ct);
                foreach (var line in done)
                {
                    say($"Redirected: {line}");
                }
                foreach (var line in left)
                {
                    LayoutProblem(line);
                }
            }

            // What local folders still hold for their redirected place (|move), one folder at a time, also what a stop left halfway
            void StartMoves()
            {
                if (layoutCancel.IsCancellationRequested || Interlocked.Exchange(ref moving, 1) == 1)
                {
                    return;
                }
                _ = Task.Run(() =>
                {
                    try
                    {
                        foreach (var move in folderRedirection.PendingMoves())
                        {
                            var stayed = FolderRedirection.MoveContents(move.From, move.To, layoutCancel.Token);
                            folderRedirection.MoveFinished(move);
                            say($"Redirected: moved what {move.From} held into {move.To}{(stayed.Count == 0 ? "" : $", except {stayed.Count} item(s) (see the log)")}");
                            foreach (var item in stayed.Take(20))
                            {
                                log($"redirection: {item}");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        say("Redirected: moving folder content stopped for the unmount; the rest moves at the next start");
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        LayoutProblem($"moving folder content: {e.Message}");
                    }
                    finally
                    {
                        Volatile.Write(ref moving, 0);
                    }
                });
            }

            // Another account signed in than the one whose OneDrive this engine shows: start again cleanly; its cache goes at unmount
            void AccountChanged()
            {
                if (accountChanged)
                {
                    return;
                }
                accountChanged = true;
                say("OneDrive:   the signed-in account changed; cached content is wiped and the drive starts again for the new account");
                if (!RestartAgent("for the new account"))
                {
                    stop.Set(); // never the old account's drive with the new account's tokens
                }
            }
            accountSwitchedNow = AccountChanged; // Sign in with another account: at once, not only at the next discovery
            if (accountSwitched)
            {
                AccountChanged();
            }

            // A failed run (an error, or a search that failed even partly): logged and reported, tried again in 15 to 60 minutes, sooner
            // without libraries. Returns why, in the display language (for Refresh).
            string DiscoveryFailed(string message, string cause, bool network, bool searchFailed = false)
            {
                var delay = DiscoverySchedule.AfterFailure(++discoveryFailures, settings.Scope == DriveScope.OneDrive || keeper.Current.Libraries.Count > 0, Random.Shared);
                say($"Discovery:  failed, showing {(settings.Scope == DriveScope.OneDrive ? "the OneDrive" : $"{keeper.Current.Libraries.Count} libraries")} " +
                    $"from the last run{(searchFailed ? " and what this one found" : "")}; trying again in {delay.TotalMinutes:0} min: {message}");
                monitoring?.Error("discovery", message);
                AgentEvents.Write(AgentEvent.DiscoveryFailed, $"Discovery failed; the libraries of the last run stay and it tries again in {delay.TotalMinutes:0} min: {message}");
                var plain = PlainCause.Of(cause, !networkUp || network, tokens.SignInProblem);
                if (plain == Cause.Offline)
                {
                    networkFailedAt = DateTimeOffset.UtcNow;
                }
                nextDiscovery = DateTimeOffset.UtcNow + delay;
                return PlainCause.Text(plain);
            }

            // Null when it worked, else why not in the display language (for Refresh)
            async Task<string?> DiscoverAsync(bool everything)
            {
                RequestPriority.MarkBackground();
                await discoveryGate.WaitAsync();
                var watch = Stopwatch.StartNew();
                try
                {
                    if (settings.Scope == DriveScope.OneDrive)
                    {
                        var me = await api.GetMyDriveAsync(default);
                        keeper.Replace(keeper.Current with { RunAt = DateTimeOffset.UtcNow, OneDrive = me, TenantHint = tenantHint });
                        log($"discovery: OneDrive checked in {watch.Elapsed.TotalSeconds:N1} s");
                        if (me.DriveId != oneDrive.DriveId)
                        {
                            AccountChanged();
                        }
                    }
                    else
                    {
                        var before = keeper.Current;
                        var result = await new DiscoveryService(api, everything ? discoveryOptions with { MetadataMaxAge = TimeSpan.Zero } : discoveryOptions)
                            .RunAsync(before, default, justRead, tokens.LastResult?.TenantId);
                        justRead = null;
                        // A failed search keeps the libraries (nothing is hidden) but not the time: the next start and Refresh don't take it as recent
                        keeper.Replace(DiscoverySchedule.Keep(result, before) with { TenantHint = tenantHint });
                        SaveQuietly("the discovery report", () => DiscoveryReport.Record(result, before)); // for 'vfs365 why' and support bundles
                        if (result.OneDrive.DriveId != oneDrive.DriveId)
                        {
                            AccountChanged();
                            return null;
                        }
                        engine.SetLibraries(keeper.Current.Libraries);
                        _ = Task.Run(ApplyLayoutAsync); // targets in libraries that only now are known
                        var shown = keeper.Current.Libraries.Select(l => l.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var gone in before.Libraries.Where(l => l.DriveId is not null && !shown.Contains(l.Key)))
                        {
                            cache.ForgetDrive(gone.DriveId!); // no longer reachable or shown: its cached content goes
                        }
                        if (result.SearchError is { } searchError)
                        {
                            // "Search failed after N rows: <the request's error>": the cause is in the request's error
                            return DiscoveryFailed(searchError, searchError.IndexOf(": ", StringComparison.Ordinal) is var at and >= 0 ? searchError[(at + 2)..] : searchError,
                                network: false, searchFailed: true);
                        }
                        say($"Libraries:  {result.Libraries.Count} from discovery in {watch.Elapsed.TotalSeconds:N1} s");
                        _ = Task.Run(() => RefreshSharedAsync(recheck: everything));
                    }
                    discoveryFailures = 0;
                    nextDiscovery = NextDay();
                    return null;
                }
                catch (Exception e) when (e is not OutOfMemoryException) // nothing cancels discovery: a timeout or a cancellation is a failure too
                {
                    return DiscoveryFailed(e.Message, e.Message, PlainCause.IsNetwork(e));
                }
                finally
                {
                    discoveryGate.Release();
                }
            }

            // Shared with me: one search call per run for most users (paths and rights only for new, moved or week-old items), one run at
            // a time; after discovery (daily, and on Refresh with every item checked again) and when the folder is listed and the list is
            // older than SharedListedAge. A failed run keeps the list and is tried again at the next of those.
            var sharedGate = new SemaphoreSlim(1, 1);
            var sharedTriedTicks = DateTimeOffset.MinValue.UtcTicks;
            string? sharedProblem = null;
            async Task RefreshSharedAsync(bool recheck)
            {
                if (!sharedShown || !await sharedGate.WaitAsync(0))
                {
                    return;
                }
                RequestPriority.MarkBackground();
                var watch = Stopwatch.StartNew();
                try
                {
                    Volatile.Write(ref sharedTriedTicks, DateTimeOffset.UtcNow.UtcTicks);
                    if (tokens.LastResult?.Username is null)
                    {
                        await tokens.GetAccessTokenAsync(M365Client.GraphResource, default); // the account's name, without a call to Graph
                    }
                    var user = tokens.LastResult?.Username ?? throw new IOException("the signed-in account's name is unknown");
                    var before = keeper.Current.Shared;
                    var result = await new SharedWithMeService(new SharedWithMeApi(client)).RunAsync(user, oneDrive.DriveId, before, recheck, default);
                    if (result.SearchError is { } error)
                    {
                        throw new IOException($"search failed: {error}");
                    }
                    keeper.SetShared(result.State);
                    engine.SetShared(result.State.Items);
                    foreach (var gone in (before?.Items ?? []).Select(i => i.DriveId).Distinct(StringComparer.Ordinal).Where(id => ns.VolumePathOf(id) is null))
                    {
                        cache.ForgetDrive(gone); // no longer shown anywhere: its cached content goes
                    }
                    var added = result.State.Items.Count(i => before?.Items.Any(b => b.Key == i.Key) != true);
                    var line = $"{result.State.Items.Count} item(s) from other people ({added} new, {result.State.Items.Count(i => i.IsFolder)} folder(s))";
                    if (line != sharedProblem || added > 0 || recheck)
                    {
                        say($"Shared:     {line}, read in {watch.Elapsed.TotalSeconds:N1} s");
                    }
                    sharedProblem = line;
                    foreach (var problem in result.Errors)
                    {
                        log($"shared with me: {problem}");
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    var line = $"failed, the last list stays: {e.Message}";
                    if (line != sharedProblem)
                    {
                        say($"Shared:     {line}");
                    }
                    sharedProblem = line;
                }
                finally
                {
                    sharedGate.Release();
                }
            }
            sharedListed = () =>
            {
                var now = DateTimeOffset.UtcNow;
                if (now - (keeper.Current.Shared?.RunAt ?? DateTimeOffset.MinValue) >= SharedListedAge
                    && now.UtcTicks - Volatile.Read(ref sharedTriedTicks) >= SharedListedAge.Ticks)
                {
                    _ = Task.Run(() => RefreshSharedAsync(recheck: false));
                }
            };

            DateTimeOffset LibrariesReadAt() => DiscoverySchedule.LibrariesReadAt(keeper.Current, settings.Scope);
            string? RefreshBlocked() => RefreshRule.Blocked(DateTimeOffset.UtcNow, LibrariesReadAt(), DiscoveryStore.LastRefresh, refreshing);
            string RefreshLabel() => RefreshRule.Label(DateTimeOffset.UtcNow, LibrariesReadAt(), DiscoveryStore.LastRefresh, refreshing);

            // Refresh in the tray: every library's details and every folder are read again (folders when next opened), once a day at most;
            // a refresh that fails doesn't count
            void RefreshAll()
            {
                if (RefreshBlocked() is not null)
                {
                    return;
                }
                refreshing = true;
                startupDiscovery.Cancel();
                say("Refresh:    from the tray; libraries and folders are read again");
                engine.ExpireListings();
                tray?.SetStatus(UiText.Get("Status.Refreshing"), TrayState.Normal);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (await DiscoverAsync(everything: true) is { } failure)
                        {
                            tray?.Notify(UiText.Get("RefreshFailed.Title"), UiText.Get("RefreshFailed.Text", failure));
                            return;
                        }
                        SaveQuietly("the Refresh time", () => DiscoveryStore.LastRefresh = DateTimeOffset.UtcNow);
                        tray?.Notify(UiText.Get("Refreshed.Title"), settings.Scope == DriveScope.OneDrive
                            ? UiText.Get("Refreshed.Folders")
                            : UiText.Get("Refreshed.Libraries", keeper.Current.Libraries.Count));
                    }
                    finally
                    {
                        refreshing = false;
                    }
                });
            }

            if (tray is not null)
            {
                // Show files opens the navigation pane entry, which needs neither a drive letter nor the network provider loaded in Explorer
                tray.Actions = startActions with
                {
                    Retry = null,
                    ShowFiles = navigationPane ? $"shell:::{ExplorerLocation.Clsid}" : drive.DriveLetter is { } trayLetter ? trayLetter + "\\" : unc,
                    Restart = () => RestartAgent("from the tray"),
                    Refresh = RefreshAll,
                    RefreshBlocked = RefreshBlocked,
                    RefreshLabel = RefreshLabel,
                };
                tray.SetStatus(UiText.Get("Status.UpToDate"), TrayState.Normal);
                if (recovered > 0)
                {
                    tray.Notify(UiText.Get("Recovered.Title"), UiText.Get("Recovered.Text", recovered));
                }
            }
            var statistics = monitoring is null ? null : UsageStatistics.Load();
            DateTime? statisticsDue = null;
            Task<int>? statisticsSending = null;
            using var push = new PushChannels(engine, new DriveNotifications(client) { Problem = log }, log);
            using var due = new CancellationTokenSource();
            Task scheduler = Task.CompletedTask;
            // Folder listings: saved off the scheduler's tick and only once they changed, also at the stop (one save at a time)
            var snapshotGate = new Lock();
            void SaveSnapshotsIfChanged()
            {
                lock (snapshotGate)
                {
                    var version = engine.MetadataVersion;
                    if (accountChanged || version == Interlocked.Read(ref savedVersion))
                    {
                        return;
                    }
                    if (SaveQuietly("the folder listings", () => MetadataStore.Save(engine.ExportSnapshots(MetadataStore.MaxFoldersPerDrive), key)))
                    {
                        Interlocked.Exchange(ref savedVersion, version);
                    }
                }
            }
            // Explorer's entries are a convenience: a registry that refuses them never ends the mounted drive
            bool Explorer(string what, Action change)
            {
                try
                {
                    change();
                    return true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    say($"Warning:    could not {what}: {e.Message}");
                    return false;
                }
            }
            try
            {
                Explorer("name the drive in Explorer", () => ExplorerLocation.SetDriveLabel(prefix, settings.Label));
                say($"Mounted:    {(drive.DriveLetter is { } assigned ? $"{assigned} and " : "")}{unc} ({settings.Scope}) after {stopwatch.Elapsed.TotalSeconds:N1} s, " +
                    $"{(usable ? $"{keeper.Current.Libraries.Count} libraries from the last run" : "first run")}. Ctrl+C or 'vfs365 unmount' to stop.");
                var mountedLetter = drive.DriveLetter;
                AgentEvents.Write(AgentEvent.Started, $"VFS365 {agentStatus.Current.Version} mounted {(mountedLetter is null ? "" : $"{mountedLetter} and ")}{unc} for " +
                    $@"{Environment.UserDomainName}\{Environment.UserName}");
                agentStatus.Update(s => s with { State = AgentStatus.Mounted, Problem = null, DriveLetter = mountedLetter, Unc = unc });
                var throttledSpan = TimeSpan.FromMinutes(Math.Max(2, settings.ThrottlePauseMinutes));
                agentStatus.Live = s =>
                {
                    var waiting = engine.WaitingToUpload();
                    return s with
                    {
                        User = tokens.LastResult?.Username ?? s.User,
                        TenantId = tokens.LastResult?.TenantId ?? s.TenantId,
                        SignInProblem = tokens.SignInProblem ? s.SignInProblem ?? "unknown" : null,
                        Online = networkUp && DateTimeOffset.UtcNow - networkFailedAt >= TimeSpan.FromMinutes(2),
                        Throttled = client.ThrottledWithin(throttledSpan),
                        WaitingUploads = waiting,
                        HeldChanges = Math.Max(0, engine.Unsaved().Count - waiting),
                    };
                };
                if (navigationPane)
                {
                    if (Explorer("add the navigation pane entry", () =>
                        ExplorerLocation.Register(settings.Label, drive.DriveLetter is { } letterRoot ? letterRoot + "\\" : unc, $"{Environment.ProcessPath},0")))
                    {
                        say($"Explorer:   \"{settings.Label}\" in the navigation pane");
                    }
                }
                else
                {
                    Explorer("remove the navigation pane entry", ExplorerLocation.Unregister); // an entry left by a run with the navigation pane on, or one that was ended
                }
                // Windows 11's context menu: the menu package registered for this user in the background (ShellPackage.cs)
                var shellPackage = ShellPackage.EnsureRegisteredAsync(log);
                // Searching with Microsoft 365 (decision 48): the folder verb, the tray item and Explorer's search box on the drive
                string? DriveVolumePath(string path) => ExplorerVerbs.VolumePath(
                    ExplorerSearch.Unshell(path, ExplorerLocation.Clsid, drive.DriveLetter is { } navLetter ? navLetter + "\\" : unc),
                    unc, drive.DriveLetter, driveLetters?.Folders() ?? new Dictionary<string, string>());
                var search = new SearchActions(engine, driveApi.GetLinksAsync, DriveVolumePath, oneDrive, AskWindow.AskAsync, ExplorerMenu.Shell,
                    (title, text, problem) => VerbPipe.Tell(tray, title, text, problem), log);
                // The VFS365 menu on the drive's files and folders and the shell section for overlay icons (ExplorerMenu.cs); removed and
                // cleared when this block ends at the stop
                using var explorerMenu = ExplorerMenu.Start(engine, driveApi, unc, drive.DriveLetter, driveLetters, settings.OverlayIcons, search.AskAndSearchAsync,
                    (title, text, problem) => VerbPipe.Tell(tray, title, text, problem), say, log);
                _ = shellPackage.ContinueWith(_ => explorerMenu?.Update(), TaskScheduler.Default); // legacy verbs go once the package is there
                using var searchWatcher = ExplorerSearchWatcher.Start(engine, settings.ExplorerSearch,
                    () => ExplorerMenu.Roots(unc, drive.DriveLetter, driveLetters?.Folders().Keys ?? []), DriveVolumePath, search.HandOffAsync,
                    (title, text) => VerbPipe.Tell(tray, title, text, false), log);
                say(settings.ExplorerSearch switch
                {
                    ExplorerSearchMode.Browser => "Search:     Explorer searches on the drive open in Microsoft 365",
                    ExplorerSearchMode.Stop => "Search:     Explorer searches on the drive are stopped (policy ExplorerSearch)",
                    _ => "Search:     Explorer searches the drive itself (policy ExplorerSearch)",
                });
                if (tray is not null)
                {
                    tray.Actions = tray.Actions with { Search = () => _ = Task.Run(() => search.AskAndSearchEverythingAsync(default)) };
                }

                // What Windows tells Explorer about the drive: checked now and every 5 minutes, logged when it changes
                if (NetworkProviderOrder.Problem(NetworkProviderOrder.Current()) is { } providerProblem)
                {
                    say($"Warning:    {providerProblem}");
                    AgentEvents.Write(AgentEvent.ProviderOrder, providerProblem);
                }
                string? driveProblems = null;
                void CheckDriveLetter()
                {
                    if (drive.DriveLetter is not { } assignedLetter)
                    {
                        return;
                    }
                    try
                    {
                        var letter = assignedLetter.TrimEnd('\\');
                        var problems = DriveLetterReport.Read(letter).Problems(unc);
                        var key = string.Join("\n", problems);
                        if (key == driveProblems)
                        {
                            return;
                        }
                        if (problems.Count == 0)
                        {
                            say(driveProblems is null ? $"Drive:      Windows shows {letter} as {unc}" : $"Drive:      {letter} is fine again; Windows shows it as {unc}");
                        }
                        foreach (var problem in problems)
                        {
                            say($"Warning:    {problem}");
                            monitoring?.Error("drive letter", problem);
                            AgentEvents.Write(AgentEvent.DriveLetterProblem, problem);
                        }
                        driveProblems = key;
                    }
                    catch (Exception e)
                    {
                        log($"drive letter check: {e.Message}");
                    }
                }
                _ = Task.Run(CheckDriveLetter);
                layoutSoon = () => _ = Task.Run(ApplyLayoutAsync);
                layoutSoon();

                // \\VFS365 must not resolve on the network: whoever answers gets the paths apps kept, while the drive isn't mounted
                _ = Task.Run(async () =>
                {
                    if (ServerNameCheck.Warning(await ServerNameCheck.ResolveAsync()) is { } resolves)
                    {
                        say($"Warning:    {resolves}");
                        monitoring?.Error("security", resolves);
                        AgentEvents.Write(AgentEvent.NameResolves, resolves);
                    }
                });

                // Settings are read once at the start; a policy changed since shows in the log and the tray. Monitoring follows its policy
                // at once (a renewed SAS, another level of detail), which users needn't hear about.
                string? policyChanges = null;
                (string? Url, MonitoringDetail Detail) monitoringPolicy = (settings.MonitoringUrl, settings.MonitoringDetail);
                void CheckSettings()
                {
                    AgentSettings current;
                    IReadOnlyList<string> differences;
                    try
                    {
                        current = AgentSettings.Load(configPath);
                        differences = current.ChangedFrom(settings);
                    }
                    catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        return;
                    }
                    if ((current.MonitoringUrl, current.MonitoringDetail) != monitoringPolicy)
                    {
                        monitoringPolicy = (current.MonitoringUrl, current.MonitoringDetail);
                        var target = MonitoringTarget.Parse(current.MonitoringUrl, out var problem);
                        if (monitoring is null)
                        {
                            say("Settings:   MonitoringUrl changed; monitoring starts after Restart in the tray or at the next sign-in");
                        }
                        else
                        {
                            monitoring.Retarget(target, current.MonitoringDetail);
                            say($"Settings:   monitoring policy changed, in effect now. {(target is null ? $"Monitoring: off, {problem ?? "no MonitoringUrl"}" : monitoring.Started)}" +
                                $", detail {current.MonitoringDetail}");
                        }
                    }
                    differences = differences.Where(name => name is not ("MonitoringUrl" or "MonitoringDetail")).ToList();
                    var names = string.Join(", ", differences);
                    if (names == (policyChanges ?? ""))
                    {
                        return;
                    }
                    policyChanges = names;
                    if (differences.Count > 0)
                    {
                        say($"Settings:   changed since the start ({names}); they apply after Restart in the tray or at the next sign-in");
                        AgentEvents.Write(AgentEvent.SettingsChanged, $"VFS365's settings changed since the start ({names}); they apply after Restart in the tray or at the next sign-in");
                        tray?.Notify(UiText.Get("Policy.Title"), UiText.Get("Policy.Text", names));
                    }
                }

                // Sleep and resume: changes made meanwhile are read as soon as the network is back
                powerChanged = resumed =>
                {
                    say(resumed ? "Resumed:    after sleep; libraries in use are checked for changes" : "Sleeping:   the PC goes to sleep");
                    if (!resumed)
                    {
                        return;
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            for (var i = 0; i < 30 && !NetworkInterface.GetIsNetworkAvailable(); i++)
                            {
                                await Task.Delay(1000);
                            }
                            engine.NetworkRestored();
                            push.Sync();
                            await engine.PollActiveAsync();
                            await ApplyLayoutAsync();
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            log($"after resume: {e.Message}");
                        }
                    });
                };

                if (settings.Scope == DriveScope.OneDrive && !usable)
                {
                    nextDiscovery = NextDay(); // the OneDrive was just read
                }
                else
                {
                    // With a recent state (under a week old, with libraries) the drive shows it at once; discovery waits 5 to 90 minutes, so a
                    // tenant's sign-ins don't all discover at the same moment. A first start, an older state, or one that never found
                    // libraries (a first run that didn't finish, a failed search) discovers right away.
                    var recent = usable && !discoverNow && DiscoverySchedule.Recent(keeper.Current, settings.Scope, DateTimeOffset.UtcNow);
                    var wait = DiscoverySchedule.StartWait(recent, Random.Shared);
                    if (recent && settings.Scope != DriveScope.OneDrive)
                    {
                        say($"Discovery:  in {wait.TotalMinutes:0} min; the libraries of the last run show until then (Refresh in the tray reads them now)");
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(wait, startupDiscovery.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return; // a refresh from the tray ran instead
                        }
                        await DiscoverAsync(everything: false);
                    });
                }

                var throttleShown = TimeSpan.FromMinutes(Math.Max(2, settings.ThrottlePauseMinutes));
                var promptedWithoutTray = false;
                // Due uploads and deletes run on their own, one run at a time: they wait for the engine's write gate, which a large file's
                // download for writing can hold for minutes, and the tick (tray, notices, push, saves) never waits for them
                async Task ProcessDueQuietlyAsync()
                {
                    try
                    {
                        await engine.ProcessDueAsync(false, due.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception e)
                    {
                        log($"error: {e.Message}");
                        monitoring?.Error("agent", e.Message);
                    }
                }
                Action saveDiscoveryState = keeper.SaveIfChanged; // made once: the tick allocates as little as it can
                Func<Task> applyLayout = ApplyLayoutAsync, discover = () => DiscoverAsync(everything: false);
                Action checkDriveLetter = CheckDriveLetter, saveSnapshots = SaveSnapshotsIfChanged;
                scheduler = Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                    var nextSnapshot = DateTimeOffset.UtcNow + SnapshotInterval;
                    Task processing = Task.CompletedTask, snapshotting = Task.CompletedTask;
                    string? lastError = null;
                    // Each part of the tick on its own: one that fails is logged (once per message) and the others still run
                    bool Failed(Exception e)
                    {
                        if (e is OperationCanceledException && due.IsCancellationRequested)
                        {
                            return false; // the stop
                        }
                        if (e.Message != lastError)
                        {
                            lastError = e.Message;
                            log($"error: {e.Message}");
                            monitoring?.Error("agent", e.Message);
                        }
                        return true;
                    }

                    var tick = 0;
                    while (await timer.WaitForNextTickAsync(due.Token))
                    {
                        var now = DateTimeOffset.UtcNow;
                        var at = tick++;
                        if (processing.IsCompleted)
                        {
                            processing = ProcessDueQuietlyAsync();
                        }
                        try
                        {
                            engine.HotPollingPaused = engine.PrefetchPaused = client.BackgroundPaused;
                            ReportThrottling(now);
                            _ = engine.PollActiveAsync();
                        }
                        catch (Exception e) when (Failed(e))
                        {
                        }
                        if (statistics is not null && at % 60 == 0)
                        {
                            try
                            {
                                ReportStatistics(statistics, monitoring!, client, tokens.LastResult?.Username, ref statisticsDue, ref statisticsSending);
                            }
                            catch (Exception e) when (Failed(e))
                            {
                            }
                        }
                        if (at % 5 == 0)
                        {
                            try
                            {
                                push.Sync();
                            }
                            catch (Exception e) when (Failed(e))
                            {
                            }
                        }
                        try
                        {
                            SaveQuietly("the discovery state", saveDiscoveryState);
                            if (tray is not null)
                            {
                                foreach (var (title, text) in notices.Due(now))
                                {
                                    tray.Notify(title, text);
                                }
                            }
                        }
                        catch (Exception e) when (Failed(e))
                        {
                        }
                        // Without a tray icon nothing offers Sign in: a sign-in that only a window solves opens the window by itself, once
                        // per problem (not within 15 minutes of a cancel); other problems (Conditional Access, WAM errors) never prompt
                        if (background && !showTray && at % 5 == 0)
                        {
                            if (!tokens.SignInNeedsUser)
                            {
                                promptedWithoutTray = false;
                            }
                            else if (!promptedWithoutTray)
                            {
                                promptedWithoutTray = true;
                                say("Sign-in:    needed, and there is no tray icon (policy TrayIcon) to offer Sign in: the sign-in window opens");
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        if (await tokens.SignInAsync(userAsked: false, default))
                                        {
                                            say("Sign-in:    done");
                                            WakeUp();
                                        }
                                    }
                                    catch (Exception e) when (e is not OutOfMemoryException)
                                    {
                                        log($"sign-in: {e.Message}");
                                    }
                                });
                            }
                        }
                        if (at % 5 == 0 && tray is not null)
                        {
                            try
                            {
                                var waiting = engine.WaitingToUpload();
                                var offline = !networkUp || now - networkFailedAt < TimeSpan.FromMinutes(2);
                                var throttled = client.ThrottledWithin(throttleShown);
                                var signIn = tokens.SignInProblem;
                                tray.BlockShutdown(waiting > 0 ? UiText.Get("Shutdown.Uploading", waiting) : null);
                                tray.SetStatus(
                                    signIn ? UiText.Get("Status.SignIn")
                                        : offline ? waiting == 0 ? UiText.Get("Status.Offline") : UiText.Get("Status.OfflineWaiting", waiting)
                                        : refreshing ? UiText.Get("Status.Refreshing")
                                        : waiting > 0 ? UiText.Get("Status.Waiting", waiting)
                                        : throttled ? UiText.Get("Status.Throttled")
                                        : UiText.Get("Status.UpToDate"),
                                    signIn ? TrayState.Error : waiting > 0 || offline ? TrayState.Waiting : TrayState.Normal);
                            }
                            catch (Exception e) when (Failed(e))
                            {
                            }
                        }
                        try
                        {
                            if (at % 300 == 150)
                            {
                                _ = Task.Run(checkDriveLetter);
                            }
                            if (at % 300 == 250)
                            {
                                CheckSettings();
                            }
                            if (at % 3600 == 1800)
                            {
                                _ = Task.Run(applyLayout);
                            }
                            if (now >= nextDiscovery && !refreshing)
                            {
                                // Daily, so a PC that sleeps instead of signing out still sees new and removed libraries
                                nextDiscovery = DateTimeOffset.MaxValue;
                                _ = Task.Run(discover);
                            }
                            if (now >= nextSnapshot && snapshotting.IsCompleted && !accountChanged && engine.MetadataVersion != Interlocked.Read(ref savedVersion))
                            {
                                nextSnapshot = now + SnapshotInterval;
                                snapshotting = Task.Run(saveSnapshots); // a large one takes about a second
                            }
                        }
                        catch (Exception e) when (Failed(e))
                        {
                        }
                    }
                });

                stop.WaitOne();
                powerChanged = null;
                layoutSoon = null;
                accountSwitchedNow = null;
                // Layout work and the scheduler stop first: a run going on stops at its next step, and nothing redirects a folder or
                // defines a letter after the teardown below (FolderRedirection and SessionDriveLetters refuse it once restored)
                layoutCancel.Cancel();
                due.Cancel();
                if (!layout.WaitIdle(TimeSpan.FromSeconds(5)))
                {
                    log("layout: a run is still going on; it changes nothing after the teardown");
                }
                // Before the unmount: redirected folders go back (not on a restart: the new agent takes them over), the extra letters go
                RestoreLayout(restoreFolders: !restarting, drive.DriveLetter);
                if (navigationPane)
                {
                    Explorer("remove the navigation pane entry", ExplorerLocation.Unregister);
                }
            }
            finally
            {
                due.Cancel();
                tray?.Dispose();
                tray = null;
                drive.Dispose();
            }

            try
            {
                await scheduler;
            }
            catch (OperationCanceledException)
            {
            }

            // The quick local saves first: at logoff Windows ends the agent about a minute after the session ends, maybe while uploads still run
            void SaveLocalState()
            {
                ReportThrottling(DateTimeOffset.MaxValue); // an episode still going on
                SaveQuietly("the discovery state", keeper.SaveIfChanged);
                SaveSnapshotsIfChanged(); // the second time only when the last uploads changed listings
                if (statistics is not null)
                {
                    statistics.Fold(client.Usage, monitoring!.Errors, DateTime.Now, tokens.LastResult?.Username, Monitoring.Version, monitoring.Device);
                    statistics.Save();
                }
            }
            SaveLocalState();
            await engine.ProcessDueAsync(true, default);
            SaveLocalState();
            if (accountChanged)
            {
                // Nothing of the previous account's stays for the next start
                try
                {
                    cache.Clear();
                    MetadataStore.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    say($"Warning:    could not wipe the previous account's cached content: {e.Message}");
                }
            }
            var notSaved = engine.Unsaved();
            foreach (var unsaved in notSaved)
            {
                say($"Not saved:  {unsaved}");
            }
            if (notSaved.Count > 0)
            {
                AgentEvents.Write(AgentEvent.NotSaved, $"{notSaved.Count} changed file(s) are not saved to Microsoft 365 yet and stay on this PC: " + string.Join("; ", notSaved.Take(20)));
            }
            AgentEvents.Write(AgentEvent.Stopped, $"VFS365 stopped and unmounted {unc}");
            var usage = client.Usage; // monitoring flushes once, below
            say($"Unmounted.  Requests: Graph {usage.GraphRequests}, SharePoint {usage.SharePointRequests}, about {usage.ResourceUnits} resource units " +
                $"in {stopwatch.Elapsed.TotalMinutes:N1} min; throttled {usage.Throttled} time(s), waited for the budget {usage.BudgetWaitForegroundSeconds} s (user actions), " +
                $"{usage.BudgetWaitBackgroundSeconds} s (background), added up over requests");
            return 0;
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
            powerChanged = null;
            layoutSoon = null;
            layoutCancel.Cancel();
            tray?.Dispose();
            if (ownsSession && !restarting && !layoutRestored)
            {
                RestoreLayout(restoreFolders: true, null); // a stop before the mount: what a crashed run left goes back
            }
            ReportThrottling(DateTimeOffset.MaxValue); // a stop before the drive was mounted: the first start's episode
            status?.Close();
            if (monitoring is not null)
            {
                await monitoring.FlushAsync(TimeSpan.FromSeconds(3));
            }
            done.Set();
        }
    }

    static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(5);

    /// <summary>Another user's volume holds this user's UNC path (\\VFS365\&lt;user&gt;). The user's own leftover volume is retried like any start problem.</summary>
    static bool IsMountConflict(Exception e) => e is MountConflictException { OwnedByAnotherUser: true };

    /// <summary>A restart from the tray: waits for the previous agent to finish uploading and unmount, without a time limit.</summary>
    static async Task WaitForAgentAsync(int processId, Tray? tray, Action<string> say)
    {
        try
        {
            using var old = Process.GetProcessById(processId);
            if (!old.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                say("Restart:    waiting for the previous agent to upload pending changes and stop");
                tray?.SetStatus(UiText.Get("Status.Restarting"), TrayState.Waiting);
                await old.WaitForExitAsync();
            }
        }
        catch (ArgumentException)
        {
        }
        for (var i = 0; i < 30 && AgentControl.IsMounted(); i++)
        {
            await Task.Delay(1000);
        }
    }

    static string Sentence(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..].TrimEnd('.');

    /// <summary>
    /// Adds the last minute's usage to today's statistics of this device. Finished days go out a few minutes after the start or after
    /// midnight (spread, so a tenant's devices don't all send at once), each as the device it was used on; a day that doesn't arrive
    /// (offline, an expired SAS) stays and is tried an hour later.
    /// </summary>
    static void ReportStatistics(UsageStatistics statistics, Monitoring monitoring, M365Client client, string? user, ref DateTime? due, ref Task<int>? sending)
    {
        var now = DateTime.Now;
        statistics.Fold(client.Usage, monitoring.Errors, now, user, Monitoring.Version, monitoring.Device);
        statistics.SaveIfChanged(now); // a minute of running time alone isn't worth a write
        if (monitoring.Target is null)
        {
            return; // monitoring was turned off meanwhile
        }
        if (sending is { IsCompleted: true } sent)
        {
            if (sent.IsCompletedSuccessfully && sent.Result == 0)
            {
                due = null;
            }
            sending = null;
        }
        if (sending is not null || statistics.Completed(now).Count == 0)
        {
            return;
        }
        due ??= now + TimeSpan.FromSeconds(Random.Shared.Next(60, 15 * 60));
        if (now < due)
        {
            return;
        }
        due = now + TimeSpan.FromHours(1);
        sending = statistics.SendCompletedAsync(now, (day, usage) =>
            monitoring.StatisticsAsync(day, UsageStatistics.Values(day, usage), usage.User, usage.Version, usage.DeviceOr(monitoring.Device), usage.Salt));
    }
}
