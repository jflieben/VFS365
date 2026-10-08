using System.Text.Json;
using Microsoft.Win32;
using Vfs365.Core.Drive;

namespace Vfs365.Agent;

public sealed record Setting(string? Value, string? Source);

/// <summary>
/// Agent settings. First match wins per setting: HKLM policy, HKCU policy, config file. The ADMX in policy\ writes the policy values.
/// Lists are REG_MULTI_SZ in the registry and arrays in the config file.
/// </summary>
public sealed class AgentSettings
{
    public const string PolicyKey = @"SOFTWARE\Policies\JSolve\VFS365";
    public const string DefaultLabel = "VFS365";
    public const string DefaultDriveLetter = "None";
    public const DriveScope DefaultScope = DriveScope.SharePoint;

    /// <summary>JSolve's multi-tenant public client app; an admin consents to it once per tenant (docs/APP-REGISTRATION.md).</summary>
    public const string DefaultClientId = "08e570ea-9711-4dc6-b450-2e00fc550fca";

    readonly List<(string Source, Func<string, object?> Read)> sources = [];

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

    public DriveScope Scope => Enum.TryParse<DriveScope>(Text("Scope").Value, ignoreCase: true, out var scope) ? scope : DefaultScope;

    /// <summary>Site URL patterns; null keeps the built-in defaults.</summary>
    public IReadOnlyList<string>? IncludedSites => List("IncludedSites");

    public IReadOnlyList<string>? ExcludedSites => List("ExcludedSites");

    /// <summary>Site or library URLs always shown, also where search doesn't reach (restricted, excluded from search, root or portal sites).</summary>
    public IReadOnlyList<string> PinnedLocations => List("PinnedLocations") ?? [];

    public bool NavigationPane => Bool("NavigationPane") ?? true;

    public const int DefaultCacheSizeMB = 2048;

    /// <summary>Downloaded file copies kept per user, in MB (at least 100); least recently used go first.</summary>
    public int CacheSizeMB => Raw("CacheSizeMB", out _) switch
    {
        int mb => Math.Max(100, mb),
        string text when int.TryParse(text, out var mb) => Math.Max(100, mb),
        _ => DefaultCacheSizeMB,
    };

    /// <summary>Tray icon with status and notifications (conflict copies, sign-in problems, unsaved changes).</summary>
    public bool TrayIcon => Bool("TrayIcon") ?? true;

    public const int DefaultChangeCheckSeconds = 20;

    /// <summary>How often a library used in the last 2 minutes is checked for changes on top of push; 0 leaves it to push (5 to 300).</summary>
    public int ChangeCheckSeconds => Number("ChangeCheckSeconds") is { } seconds ? seconds <= 0 ? 0 : Math.Clamp(seconds, 5, 300) : DefaultChangeCheckSeconds;

    public const int DefaultApiBudgetPerMinute = 600;

    /// <summary>SharePoint resource units this user's VFS365 may use per minute; 0: no limit of its own (60 to 100,000).</summary>
    public int ApiBudgetPerMinute => Number("ApiBudgetPerMinute") is { } units ? units <= 0 ? 0 : Math.Clamp(units, 60, 100_000) : DefaultApiBudgetPerMinute;

    public const int DefaultThrottlePauseMinutes = 15;

    /// <summary>After Microsoft throttles, background work (loading ahead, frequent change checks) stops this long (0 to 240).</summary>
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

    /// <summary>Multi-user database files (Access, QuickBooks, SQLite): Allow, ReadOnly or Block.</summary>
    public DatabaseFilePolicy DatabaseFiles =>
        Enum.TryParse<DatabaseFilePolicy>(Text("DatabaseFiles").Value, ignoreCase: true, out var policy) ? policy : DatabaseFilePolicy.Allow;

    public const string DefaultHelpUrl = "https://jsolve.nl";

    /// <summary>Where the tray's Help opens, for example the organisation's own instructions.</summary>
    public string HelpUrl => Text("HelpUrl").Value is { } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http"
        ? url
        : DefaultHelpUrl;

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
            ("PinnedLocations", PinnedLocations.Count == 0 ? "none" : string.Join("; ", PinnedLocations), From("PinnedLocations")),
            ("NavigationPane", OnOff(NavigationPane), From("NavigationPane")),
            ("CacheSizeMB", CacheSizeMB.ToString(), From("CacheSizeMB")),
            ("TrayIcon", OnOff(TrayIcon), From("TrayIcon")),
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
        ];
    }

    /// <summary>Config file: <paramref name="configPath"/>, else vfs365.local.json in the working directory, else vfs365.json next to the exe.</summary>
    public static AgentSettings Load(string? configPath, bool policies = true)
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
        var file = configPath ?? new[]
        {
            Path.Combine(Environment.CurrentDirectory, "vfs365.local.json"),
            Path.Combine(AppContext.BaseDirectory, "vfs365.json"),
        }.FirstOrDefault(File.Exists);
        if (file is not null)
        {
            var json = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
            settings.sources.Add((Path.GetFileName(file), name => FromJson(json, name)));
        }
        return settings;
    }

    object? Raw(string name, out string? source)
    {
        foreach (var (from, read) in sources)
        {
            if (read(name) is { } value && value is not (string { Length: 0 } or string[] { Length: 0 }))
            {
                source = from;
                return value;
            }
        }
        source = null;
        return null;
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

    int? Number(string name) => Raw(name, out _) switch
    {
        int number => number,
        string text when int.TryParse(text.Trim(), out var number) => number,
        _ => null,
    };

    bool? Bool(string name) => Raw(name, out _) switch
    {
        int number => number != 0,
        bool flag => flag,
        string text => text.Trim() is "1" || text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
        _ => null,
    };

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
                    JsonValueKind.Number => property.Value.GetInt32(),
                    _ => null,
                };
            }
        }
        return null;
    }
}
