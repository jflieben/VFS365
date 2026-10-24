using System.Text.Json;
using Microsoft.Win32;
using Vfs365.Core.Drive;

namespace Vfs365.Agent;

public sealed record Setting(string? Value, string? Source);

public enum MonitoringDetail { Full, NoPaths }

/// <summary>
/// Agent settings. First match wins per setting: HKLM policy, HKCU policy, config file; lists are not merged (machine policy wins).
/// The ADMX in policy\ writes the policy values. Lists are REG_MULTI_SZ in the registry and arrays in the config file. Each value is
/// read once and kept: a running agent uses the settings it started with (ChangedFrom tells what changed since).
/// </summary>
public sealed class AgentSettings
{
    public const string PolicyKey = @"SOFTWARE\Policies\JSolve\VFS365";
    public const string DefaultLabel = "VFS365";
    public const string DefaultDriveLetter = "None";
    public const DriveScope DefaultScope = DriveScope.SharePoint;

    /// <summary>JSolve's multi-tenant public client app; an admin consents to it once per tenant (docs/APP-REGISTRATION.md).</summary>
    public const string DefaultClientId = "08e570ea-9711-4dc6-b450-2e00fc550fca";

    /// <summary>Every setting name, for change detection.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "ClientId", "TenantId", "DriveLetter", "Label", "Scope", "IncludedSites", "ExcludedSites", "IncludedSiteTemplates", "ExcludedSiteTemplates",
        "PinnedLocations", "NavigationPane", "CacheSizeMB", "TrayIcon", "NotifyUploads", "HelpUrl", "ChangeCheckSeconds", "ApiBudgetPerMinute",
        "ThrottlePauseMinutes", "WalkPrefetchFolders", "ReadAheadFiles", "BackgroundUploads", "RepeatSaveSeconds", "DatabaseFiles", "MonitoringUrl",
        "MonitoringDetail", "FolderRedirection", "DriveMappings",
    ];

    readonly List<(string Source, Func<string, object?> Read)> sources = [];
    readonly Dictionary<string, (object? Value, string? Source)> read = new(StringComparer.OrdinalIgnoreCase);

    AgentSettings()
    {
    }

    public Setting ClientId => Text("ClientId") is { Value: not null } configured ? configured : new(DefaultClientId, "built-in");
    public Setting TenantId => Text("TenantId");

    public static string AdminConsentUrl(string clientId) => $"https://login.microsoftonline.com/organizations/adminconsent?client_id={clientId}";

    /// <summary>Volume label and the Explorer name of the drive and of the navigation pane node.</summary>
    public string Label => Text("Label").Value ?? DefaultLabel;

    /// <summary>A letter, "*" for the first free one counting down from Z, or "None" (default): only the UNC path and the navigation pane node.</summary>
    public string DriveLetter => Text("DriveLetter").Value ?? DefaultDriveLetter;

    public DriveScope Scope => Choice<DriveScope>("Scope") ?? DefaultScope;

    /// <summary>Site URL patterns; null keeps the built-in defaults.</summary>
    public IReadOnlyList<string>? IncludedSites => List("IncludedSites");

    /// <summary>Colleagues' OneDrives: hidden also when policy sets its own ExcludedSites (multi-geo OneDrives come back from search).</summary>
    public const string PersonalSites = "*/personal/*";

    /// <summary>Policy's list replaces the built-in one, with <see cref="PersonalSites"/> always in it; null keeps the built-in list.</summary>
    public IReadOnlyList<string>? ExcludedSites => List("ExcludedSites") is { } excluded
        ? excluded.Contains(PersonalSites, StringComparer.OrdinalIgnoreCase) ? excluded : [.. excluded, PersonalSites]
        : null;

    /// <summary>Site templates (GROUP, STS, SITEPAGEPUBLISHING, ...) to show or hide; none set: all.</summary>
    public IReadOnlyList<string> IncludedSiteTemplates => List("IncludedSiteTemplates") ?? [];

    public IReadOnlyList<string> ExcludedSiteTemplates => List("ExcludedSiteTemplates") ?? [];

    /// <summary>Site or library URLs always shown, also where search doesn't reach (restricted, excluded from search, root or portal sites).</summary>
    public IReadOnlyList<string> PinnedLocations => List("PinnedLocations") ?? [];

    public bool NavigationPane => Bool("NavigationPane") ?? true;

    /// <summary>Known folders moved into the volume while it is mounted: "Documents=OneDrive\Documents", "|move" moves their content.</summary>
    public IReadOnlyList<string> FolderRedirection => List("FolderRedirection") ?? [];

    /// <summary>Drive letters for folders in the volume: "Z:=Sites\Finance\Documents", "|Finance" names it in Explorer.</summary>
    public IReadOnlyList<string> DriveMappings => List("DriveMappings") ?? [];

    public const int DefaultCacheSizeMB = 2048;

    /// <summary>Downloaded file copies kept per user, in MB (at least 100); least recently used go first.</summary>
    public int CacheSizeMB => Number("CacheSizeMB") is { } mb ? Math.Max(100, mb) : DefaultCacheSizeMB;

    /// <summary>Tray icon with status and notifications (conflict copies, sign-in problems, unsaved changes).</summary>
    public bool TrayIcon => Bool("TrayIcon") ?? true;

    /// <summary>"Uploaded" notifications for files that had to wait; conflict copies and waiting uploads always notify.</summary>
    public bool NotifyUploads => Bool("NotifyUploads") ?? true;

    public const int DefaultChangeCheckSeconds = 20;

    /// <summary>How often a library used in the last 2 minutes is checked for changes on top of push; 0 leaves it to push (5 to 300).</summary>
    public int ChangeCheckSeconds => Number("ChangeCheckSeconds") is { } seconds ? seconds <= 0 ? 0 : Math.Clamp(seconds, 5, 300) : DefaultChangeCheckSeconds;

    public const int DefaultApiBudgetPerMinute = 600;

    /// <summary>SharePoint resource units this user's VFS365 may use per minute; 0: no limit of its own (60 to 100,000).</summary>
    public int ApiBudgetPerMinute => Number("ApiBudgetPerMinute") is { } units ? units <= 0 ? 0 : Math.Clamp(units, 60, 100_000) : DefaultApiBudgetPerMinute;

    public const int DefaultThrottlePauseMinutes = 15;

    /// <summary>After Microsoft throttles, background work (loading ahead, change checks, discovery) waits this long (0 to 240).</summary>
    public int ThrottlePauseMinutes => Number("ThrottlePauseMinutes") is { } minutes ? Math.Clamp(minutes, 0, 240) : DefaultThrottlePauseMinutes;

    public const int DefaultWalkPrefetchFolders = 10;

    /// <summary>Folders loaded ahead of an app walking a folder tree that it hasn't opened yet, at most; 0: none (0 to 200).</summary>
    public int WalkPrefetchFolders => Number("WalkPrefetchFolders") is { } folders ? Math.Clamp(folders, 0, 200) : DefaultWalkPrefetchFolders;

    public const int DefaultReadAheadFiles = 8;

    /// <summary>Small files downloaded ahead of a copy out of the drive that it hasn't opened yet, at most; 0: none (0 to 64).</summary>
    public int ReadAheadFiles => Number("ReadAheadFiles") is { } files ? Math.Clamp(files, 0, 64) : DefaultReadAheadFiles;

    /// <summary>New files upload after their close returned, several at a time (default); off: every close waits for its upload.</summary>
    public bool BackgroundUploads => Bool("BackgroundUploads") ?? true;

    public const int DefaultRepeatSaveSeconds = 30;

    /// <summary>A file closed again within this many seconds of its last upload uploads once at their end; 0: every close uploads (0 to 600).</summary>
    public int RepeatSaveSeconds => Number("RepeatSaveSeconds") is { } seconds ? Math.Clamp(seconds, 0, 600) : DefaultRepeatSaveSeconds;

    /// <summary>Table service SAS URL for reports (errors, daily statistics, installs); empty: no reporting.</summary>
    public string? MonitoringUrl => Text("MonitoringUrl").Value;

    /// <summary>Full (default) or NoPaths: error rows carry a short hash instead of file and folder paths, and URLs only their host.</summary>
    public MonitoringDetail MonitoringDetail => Choice<MonitoringDetail>("MonitoringDetail") ?? MonitoringDetail.Full;

    /// <summary>Multi-user database files (Access, QuickBooks, SQLite): Allow, ReadOnly or Block.</summary>
    public DatabaseFilePolicy DatabaseFiles => Choice<DatabaseFilePolicy>("DatabaseFiles") ?? DatabaseFilePolicy.Allow;

    public const string DefaultHelpUrl = "https://jsolve.nl";

    /// <summary>Where the tray's Help opens, for example the organisation's own instructions.</summary>
    public string HelpUrl => ValidHelpUrl(Text("HelpUrl").Value) ?? DefaultHelpUrl;

    static string? ValidHelpUrl(string? url) => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" ? url : null;

    public const string ComputerPolicy = "computer policy", UserPolicy = "user policy", Default = "default";

    /// <summary>
    /// Every setting as the agent uses it, with where it comes from: computer or user policy (marked Intune when Intune's policy store
    /// holds it, else Group Policy or another tool wrote it), the config file, or "default". The monitoring URL shows only its storage
    /// account, never the SAS.
    /// </summary>
    public IReadOnlyList<(string Name, string Value, string Source)> Effective()
    {
        string From(string name) => Raw(name, out var source) is null ? Default
            : source == ComputerPolicy && FromIntune("device", name) ? $"{ComputerPolicy} (Intune)"
            : source == UserPolicy && FromIntune(System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value, name) ? $"{UserPolicy} (Intune)"
            : source!;
        static string OnOff(bool value) => value ? "on" : "off";
        var defaults = new Core.Discovery.DiscoveryOptions();
        var monitoring = MonitoringTarget.Parse(MonitoringUrl, out var problem);
        return
        [
            ("ClientId", ClientId.Value!, From("ClientId")),
            ("TenantId", TenantId.Value ?? "detected", From("TenantId")),
            ("DriveLetter", DriveLetter, From("DriveLetter")),
            ("Label", Label, From("Label")),
            ("Scope", Scope.ToString(), From("Scope")),
            ("IncludedSites", string.Join("; ", IncludedSites ?? defaults.IncludedSites), From("IncludedSites")),
            ("ExcludedSites", string.Join("; ", ExcludedSites ?? defaults.ExcludedSites), From("ExcludedSites")),
            ("IncludedSiteTemplates", IncludedSiteTemplates.Count == 0 ? "all" : string.Join("; ", IncludedSiteTemplates), From("IncludedSiteTemplates")),
            ("ExcludedSiteTemplates", ExcludedSiteTemplates.Count == 0 ? "none" : string.Join("; ", ExcludedSiteTemplates), From("ExcludedSiteTemplates")),
            ("PinnedLocations", PinnedLocations.Count == 0 ? "none" : string.Join("; ", PinnedLocations), From("PinnedLocations")),
            ("NavigationPane", OnOff(NavigationPane), From("NavigationPane")),
            ("FolderRedirection", FolderRedirection.Count == 0 ? "none" : string.Join("; ", FolderRedirection), From("FolderRedirection")),
            ("DriveMappings", DriveMappings.Count == 0 ? "none" : string.Join("; ", DriveMappings), From("DriveMappings")),
            ("CacheSizeMB", CacheSizeMB.ToString(), From("CacheSizeMB")),
            ("TrayIcon", OnOff(TrayIcon), From("TrayIcon")),
            ("NotifyUploads", OnOff(NotifyUploads), From("NotifyUploads")),
            ("HelpUrl", HelpUrl, From("HelpUrl")),
            ("ChangeCheckSeconds", ChangeCheckSeconds == 0 ? "0 (push only)" : ChangeCheckSeconds.ToString(), From("ChangeCheckSeconds")),
            ("ApiBudgetPerMinute", ApiBudgetPerMinute == 0 ? "0 (no limit of its own)" : ApiBudgetPerMinute.ToString(), From("ApiBudgetPerMinute")),
            ("ThrottlePauseMinutes", ThrottlePauseMinutes.ToString(), From("ThrottlePauseMinutes")),
            ("WalkPrefetchFolders", WalkPrefetchFolders == 0 ? "0 (off)" : WalkPrefetchFolders.ToString(), From("WalkPrefetchFolders")),
            ("ReadAheadFiles", ReadAheadFiles == 0 ? "0 (off)" : ReadAheadFiles.ToString(), From("ReadAheadFiles")),
            ("BackgroundUploads", OnOff(BackgroundUploads), From("BackgroundUploads")),
            ("RepeatSaveSeconds", RepeatSaveSeconds == 0 ? "0 (every close uploads)" : RepeatSaveSeconds.ToString(), From("RepeatSaveSeconds")),
            ("DatabaseFiles", DatabaseFiles.ToString(), From("DatabaseFiles")),
            ("MonitoringUrl", monitoring is not null
                ? $"{monitoring.Account}{(monitoring.Expires is { } expires ? $" (SAS valid until {expires.ToLocalTime():yyyy-MM-dd HH:mm})" : "")}"
                : problem ?? "off", From("MonitoringUrl")),
            ("MonitoringDetail", MonitoringDetail.ToString(), From("MonitoringDetail")),
        ];
    }

    /// <summary>Numbers outside their range, and values that can't be read (wrong type or format), with the value used instead, for the log.</summary>
    public IReadOnlyList<string> Adjusted()
    {
        var adjusted = new List<string>();
        void Check(string name, int used, bool zeroAllowed = false)
        {
            if (Number(name) is { } set && set != used && !(zeroAllowed && set < 0 && used == 0))
            {
                adjusted.Add($"{name} {set} is outside its range; {used} is used");
            }
            else if (Number(name) is null && Raw(name, out _) is { } raw)
            {
                adjusted.Add($"{name} {Shown(raw)} is not a whole number; {used} is used");
            }
        }
        void Unreadable(string name, bool readable, string expected, string used)
        {
            if (!readable && Raw(name, out _) is { } raw)
            {
                adjusted.Add(name == "MonitoringUrl" ? $"{name} is not {expected}; monitoring is off" : $"{name} {Shown(raw)} is not {expected}; {used} is used");
            }
        }
        Check("CacheSizeMB", CacheSizeMB);
        Check("ChangeCheckSeconds", ChangeCheckSeconds, zeroAllowed: true);
        Check("ApiBudgetPerMinute", ApiBudgetPerMinute, zeroAllowed: true);
        Check("ThrottlePauseMinutes", ThrottlePauseMinutes);
        Check("WalkPrefetchFolders", WalkPrefetchFolders);
        Check("ReadAheadFiles", ReadAheadFiles);
        Check("RepeatSaveSeconds", RepeatSaveSeconds);
        foreach (var (name, used) in new[] { ("NavigationPane", NavigationPane), ("TrayIcon", TrayIcon), ("NotifyUploads", NotifyUploads), ("BackgroundUploads", BackgroundUploads) })
        {
            Unreadable(name, Bool(name) is not null, "on or off (1 or 0)", used ? "on" : "off");
        }
        Unreadable("Scope", Choice<DriveScope>("Scope") is not null, "SharePoint, OneDrive or All", Scope.ToString());
        Unreadable("DatabaseFiles", Choice<DatabaseFilePolicy>("DatabaseFiles") is not null, "Allow, ReadOnly or Block", DatabaseFiles.ToString());
        Unreadable("MonitoringDetail", Choice<MonitoringDetail>("MonitoringDetail") is not null, "Full or NoPaths", MonitoringDetail.ToString());
        Unreadable("HelpUrl", ValidHelpUrl(Text("HelpUrl").Value) is not null, "an http or https address", DefaultHelpUrl);
        foreach (var name in new[] { "ClientId", "TenantId", "DriveLetter", "Label", "MonitoringUrl" })
        {
            Unreadable(name, Raw(name, out _) is string, "text", "the default");
        }
        foreach (var name in new[] { "IncludedSites", "ExcludedSites", "IncludedSiteTemplates", "ExcludedSiteTemplates", "PinnedLocations", "FolderRedirection", "DriveMappings" })
        {
            Unreadable(name, Raw(name, out _) is string or string[], "a list", "the default");
        }
        return adjusted;
    }

    static string Shown(object raw) => raw switch
    {
        string text => $"'{text}'",
        string[] items => $"'{string.Join("; ", items)}'",
        byte[] => "(binary)",
        _ => Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Names of the settings whose value or source differs from <paramref name="earlier"/>.</summary>
    public IReadOnlyList<string> ChangedFrom(AgentSettings earlier) =>
        Names.Where(name => Fingerprint(Raw(name, out var source), source) != Fingerprint(earlier.Raw(name, out var was), was)).ToList();

    static string Fingerprint(object? value, string? source) =>
        $"{source}|{(value is string[] items ? string.Join("\n", items) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))}";

    /// <summary>
    /// Config file: <paramref name="configPath"/>, else vfs365.local.json in the working directory (the developer's; not for the
    /// machine commands, <paramref name="workingDirectory"/> false, and never as SYSTEM or elevated: an admin running a command in
    /// Downloads would take settings from whoever put a file there), else vfs365.json next to the exe.
    /// </summary>
    public static AgentSettings Load(string? configPath, bool policies = true, bool workingDirectory = true)
    {
        if (configPath is not null && !File.Exists(configPath))
        {
            throw new FileNotFoundException($"Config file not found: {configPath}", configPath);
        }
        var settings = new AgentSettings();
        if (policies)
        {
            settings.sources.Add((ComputerPolicy, name => FromRegistry(Registry.LocalMachine, name)));
            settings.sources.Add((UserPolicy, name => FromRegistry(Registry.CurrentUser, name)));
        }
        var file = ConfigFile(configPath, workingDirectory && !Privileged(), Environment.CurrentDirectory, AppContext.BaseDirectory);
        if (file is not null)
        {
            var json = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
            settings.sources.Add((Path.GetFileName(file), name => FromJson(json, name)));
        }
        return settings;
    }

    /// <summary>The config file Load reads, or null.</summary>
    public static string? ConfigFile(string? configPath, bool workingDirectory, string currentDirectory, string exeDirectory) =>
        configPath ?? (workingDirectory ? new[] { Path.Combine(currentDirectory, "vfs365.local.json") } : [])
            .Append(Path.Combine(exeDirectory, "vfs365.json")).FirstOrDefault(File.Exists);

    /// <summary>SYSTEM, or an elevated administrator.</summary>
    static bool Privileged()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.IsSystem || new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Settings from values as the registry returns them (string, string[], int, long, byte[]), for tests.</summary>
    public static AgentSettings FromValues(string source, IReadOnlyDictionary<string, object> values)
    {
        var settings = new AgentSettings();
        var known = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);
        settings.sources.Add((source, name => known.GetValueOrDefault(name)));
        return settings;
    }

    /// <summary>The first source's value, read once.</summary>
    object? Raw(string name, out string? source)
    {
        lock (read)
        {
            if (!read.TryGetValue(name, out var known))
            {
                known = (null, null);
                foreach (var (from, reader) in sources)
                {
                    if (reader(name) is { } value && value is not (string { Length: 0 } or string[] { Length: 0 }))
                    {
                        known = (value, from);
                        break;
                    }
                }
                read[name] = known;
            }
            source = known.Source;
            return known.Value;
        }
    }

    Setting Text(string name) => Raw(name, out var source) is string text && !string.IsNullOrWhiteSpace(text) ? new(text.Trim(), source) : new(null, null);

    IReadOnlyList<string>? List(string name) => Raw(name, out _) switch
    {
        string[] items => Clean(items),
        string text => Clean(text.Split([';', '\r', '\n'])),
        _ => null,
    };

    /// <summary>
    /// Intune applies imported ADMX policies through its policy store, HKLM\SOFTWARE\Microsoft\PolicyManager\current\&lt;device or user
    /// SID&gt;\&lt;app&gt;~Policy~..., before writing the registry policy. A value there for this setting means Intune set it.
    /// </summary>
    static bool FromIntune(string? scope, string name)
    {
        if (scope is null)
        {
            return false;
        }
        try
        {
            using var store = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\PolicyManager\current\{scope}");
            foreach (var area in store?.GetSubKeyNames().Where(n => n.Contains("VFS365", StringComparison.OrdinalIgnoreCase)) ?? [])
            {
                using var key = store!.OpenSubKey(area);
                if (key?.GetValue(name) is not null)
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return false;
    }

    /// <summary>REG_DWORD, REG_QWORD, a JSON number or a number as text; null when it is none of these.</summary>
    int? Number(string name) => Raw(name, out _) switch
    {
        int number => number,
        long number => (int)Math.Clamp(number, int.MinValue, int.MaxValue),
        string text when long.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) =>
            (int)Math.Clamp(number, int.MinValue, int.MaxValue),
        _ => null,
    };

    /// <summary>A number (0 is off), a JSON boolean, or text: 1/0, true/false, yes/no, on/off, enabled/disabled. Null when it is none of these.</summary>
    bool? Bool(string name) => Raw(name, out _) switch
    {
        int number => number != 0,
        long number => number != 0,
        bool flag => flag,
        string text when long.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) => number != 0,
        string text => text.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "enabled" or "enable" => true,
            "false" or "no" or "off" or "disabled" or "disable" => false,
            _ => null,
        },
        _ => null,
    };

    /// <summary>
    /// One of an enum's names (any case); null otherwise. Numbers and combinations ("1", "All, OneDrive") are refused: Enum.TryParse
    /// takes them and gives values that aren't defined, or internal ones.
    /// </summary>
    T? Choice<T>(string name) where T : struct, Enum =>
        Raw(name, out _) is string text && text.Trim() is { Length: > 0 } trimmed && char.IsAsciiLetter(trimmed[0]) && !trimmed.Contains(',')
            && Enum.TryParse<T>(trimmed, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : null;

    static IReadOnlyList<string>? Clean(IEnumerable<string> items) =>
        items.Select(i => i.Trim()).Where(i => i.Length > 0).ToList() is { Count: > 0 } list ? list : null;

    static object? FromRegistry(RegistryKey hive, string name)
    {
        using var key = hive.OpenSubKey(PolicyKey);
        return key?.GetValue(name);
    }

    static object? FromJson(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Array => property.Value.EnumerateArray().Select(i => i.ToString()).ToArray(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    // Not a whole number in range (1.5, 1e9): kept as text, so Adjusted names it and the default applies
                    JsonValueKind.Number => property.Value.TryGetInt32(out var number) ? number : property.Value.GetRawText(),
                    _ => null,
                };
            }
        }
        return null;
    }
}
