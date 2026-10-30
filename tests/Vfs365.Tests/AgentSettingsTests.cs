using Vfs365.Agent;
using Vfs365.Core.Drive;

namespace Vfs365.Tests;

public sealed class AgentSettingsTests : IDisposable
{
    readonly string file = Path.Combine(Path.GetTempPath(), $"vfs365-settings-{Guid.NewGuid():N}.json");

    AgentSettings Load(string json)
    {
        File.WriteAllText(file, json);
        return AgentSettings.Load(file, policies: false);
    }

    [Fact]
    public void Defaults()
    {
        var settings = Load("{}");

        Assert.Equal(AgentSettings.DefaultClientId, settings.ClientId.Value);
        Assert.Equal("built-in", settings.ClientId.Source);
        Assert.Null(settings.TenantId.Value);
        Assert.Equal("VFS365", settings.Label);
        Assert.Equal("None", settings.DriveLetter);
        Assert.Equal(DriveScope.All, settings.Scope);
        Assert.Null(settings.IncludedSites);
        Assert.True(settings.NavigationPane);
        Assert.True(settings.SharedWithMe);
    }

    [Fact]
    public void Shared_with_me_can_be_turned_off_and_says_when_scope_hides_it()
    {
        Assert.False(Policy(("SharedWithMe", 0)).SharedWithMe);
        Assert.Equal("on (shown with Scope All only)", Policy(("Scope", "SharePoint")).Effective().Single(s => s.Name == "SharedWithMe").Value);
        Assert.Equal("off", Policy(("SharedWithMe", 0), ("Scope", "All")).Effective().Single(s => s.Name == "SharedWithMe").Value);
        Assert.Contains("SharedWithMe 'maybe' is not on or off (1 or 0); on is used", Policy(("SharedWithMe", "maybe")).Adjusted());
    }

    [Fact]
    public void Values_lists_and_flags()
    {
        var settings = Load("""
            { "ClientId": " c ", "Label": " Contoso Files ", "DriveLetter": "M", "Scope": "all", "NavigationPane": false,
              "IncludedSites": ["*/sites/*", " "], "ExcludedSites": "*/sites/HR*; */sites/Legal*" }
            """);

        Assert.Equal("c", settings.ClientId.Value);
        Assert.Equal(Path.GetFileName(file), settings.ClientId.Source);
        Assert.Equal("Contoso Files", settings.Label);
        Assert.Equal("M", settings.DriveLetter);
        Assert.Equal(DriveScope.All, settings.Scope);
        Assert.False(settings.NavigationPane);
        Assert.Equal(["*/sites/*"], settings.IncludedSites);
        Assert.Equal(["*/sites/HR*", "*/sites/Legal*", AgentSettings.PersonalSites], settings.ExcludedSites);
    }

    [Fact]
    public void Colleagues_onedrives_stay_hidden_when_policy_sets_its_own_exclusions()
    {
        Assert.Equal(["*/sites/HR*", "*/personal/*"], Load("""{ "ExcludedSites": ["*/sites/HR*"] }""").ExcludedSites);
        Assert.Equal(["*/PERSONAL/*", "*/sites/HR*"], Load("""{ "ExcludedSites": ["*/PERSONAL/*", "*/sites/HR*"] }""").ExcludedSites);
        Assert.Null(Load("{}").ExcludedSites); // the built-in list has it
        Assert.Contains("*/personal/*", new Vfs365.Core.Discovery.DiscoveryOptions().ExcludedSites);
        Assert.Contains("*/personal/*", Load("""{ "ExcludedSites": ["*/sites/HR*"] }""").Effective().Single(s => s.Name == "ExcludedSites").Value);
    }

    static AgentSettings Policy(params (string Name, object Value)[] values) =>
        AgentSettings.FromValues(AgentSettings.ComputerPolicy, values.ToDictionary(v => v.Name, v => v.Value));

    [Theory]
    [InlineData("yes", true)]
    [InlineData("On", true)]
    [InlineData("enabled", true)]
    [InlineData("1", true)]
    [InlineData(" true ", true)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("Disabled", false)]
    [InlineData("0", false)]
    public void Booleans_as_text_are_read_as_people_write_them(string value, bool on)
    {
        var settings = Policy(("TrayIcon", value), ("NavigationPane", value));
        Assert.Equal((on, on), (settings.TrayIcon, settings.NavigationPane));
        Assert.Empty(settings.Adjusted());
    }

    [Fact]
    public void Unreadable_values_keep_the_default_and_are_named()
    {
        // REG_SZ "maybe" used to turn the tray off; a REG_BINARY is no number
        var settings = Policy(("TrayIcon", "maybe"), ("CacheSizeMB", new byte[] { 1, 2 }), ("ReadAheadFiles", "eight"), ("Label", 5),
            ("HelpUrl", "intranet"), ("MonitoringUrl", 7));

        Assert.True(settings.TrayIcon);
        Assert.Equal(AgentSettings.DefaultCacheSizeMB, settings.CacheSizeMB);
        Assert.Equal(AgentSettings.DefaultReadAheadFiles, settings.ReadAheadFiles);
        Assert.Equal(AgentSettings.DefaultHelpUrl, settings.HelpUrl);
        var adjusted = settings.Adjusted();
        Assert.Contains("TrayIcon 'maybe' is not on or off (1 or 0); on is used", adjusted);
        Assert.Contains("CacheSizeMB (binary) is not a whole number; 2048 is used", adjusted);
        Assert.Contains("ReadAheadFiles 'eight' is not a whole number; 8 is used", adjusted);
        Assert.Contains("Label 5 is not text; the default is used", adjusted);
        Assert.Contains("HelpUrl 'intranet' is not an http or https address; https://jsolve.nl is used", adjusted);
        Assert.Contains("MonitoringUrl is not text; monitoring is off", adjusted);
        Assert.Equal(6, adjusted.Count);
    }

    [Fact]
    public void Qword_numbers_count_like_dwords()
    {
        var settings = Policy(("CacheSizeMB", 4096L), ("TrayIcon", 0L), ("ChangeCheckSeconds", 1L << 40));
        Assert.Equal(4096, settings.CacheSizeMB);
        Assert.False(settings.TrayIcon);
        Assert.Equal(300, settings.ChangeCheckSeconds);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("All, OneDrive")]
    [InlineData("Teams")]
    public void Choices_take_only_their_names(string value)
    {
        var settings = Policy(("Scope", value), ("DatabaseFiles", value), ("MonitoringDetail", value), ("ExplorerSearch", value));
        Assert.Equal((DriveScope.All, DatabaseFilePolicy.Allow, MonitoringDetail.Full, ExplorerSearchMode.Browser),
            (settings.Scope, settings.DatabaseFiles, settings.MonitoringDetail, settings.ExplorerSearch));
        Assert.Contains($"Scope '{value}' is not All, SharePoint or OneDrive; All is used", settings.Adjusted());
        Assert.Equal(4, settings.Adjusted().Count);
        Assert.Equal(DatabaseFilePolicy.ReadOnly, Policy(("DatabaseFiles", " readonly ")).DatabaseFiles);
        Assert.Equal(ExplorerSearchMode.Stop, Policy(("ExplorerSearch", "stop")).ExplorerSearch);
    }

    [Fact]
    public void A_json_number_that_is_not_a_whole_int_does_not_stop_the_start()
    {
        var settings = Load("""{ "ChangeCheckSeconds": 1.5, "CacheSizeMB": 1e3, "ReadAheadFiles": 99999999999 }""");

        Assert.Equal(AgentSettings.DefaultChangeCheckSeconds, settings.ChangeCheckSeconds);
        Assert.Equal(AgentSettings.DefaultCacheSizeMB, settings.CacheSizeMB);
        Assert.Equal(64, settings.ReadAheadFiles);
        Assert.Contains("ChangeCheckSeconds '1.5' is not a whole number; 20 is used", settings.Adjusted());
    }

    [Fact]
    public void The_working_directory_config_is_only_for_user_commands()
    {
        var working = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vfs365-cwd-{Guid.NewGuid():N}")).FullName;
        var exe = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vfs365-exe-{Guid.NewGuid():N}")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(working, "vfs365.local.json"), "{}");
            Assert.Equal(Path.Combine(working, "vfs365.local.json"), AgentSettings.ConfigFile(null, true, working, exe));
            Assert.Null(AgentSettings.ConfigFile(null, false, working, exe)); // machine-report, SYSTEM, elevated

            File.WriteAllText(Path.Combine(exe, "vfs365.json"), "{}");
            Assert.Equal(Path.Combine(exe, "vfs365.json"), AgentSettings.ConfigFile(null, false, working, exe));
            Assert.Equal(file, AgentSettings.ConfigFile(file, false, working, exe));
        }
        finally
        {
            Directory.Delete(working, recursive: true);
            Directory.Delete(exe, recursive: true);
        }
    }

    [Fact]
    public void Unknown_scope_and_empty_values_fall_back_to_defaults()
    {
        var settings = Load("""{ "ClientId": "", "Scope": "Teams", "Label": "", "IncludedSites": [] }""");

        Assert.Equal(AgentSettings.DefaultClientId, settings.ClientId.Value);
        Assert.Equal(DriveScope.All, settings.Scope);
        Assert.Equal("VFS365", settings.Label);
        Assert.Null(settings.IncludedSites);
    }

    [Fact]
    public void Api_use_defaults_and_limits()
    {
        var defaults = Load("{}");
        Assert.Equal(600, defaults.ApiBudgetPerMinute);
        Assert.Equal(15, defaults.ThrottlePauseMinutes);
        Assert.Equal(10, defaults.WalkPrefetchFolders);
        Assert.Null(defaults.MonitoringUrl);

        var set = Load("""{ "ApiBudgetPerMinute": 10, "ThrottlePauseMinutes": 999, "WalkPrefetchFolders": "0", "MonitoringUrl": " https://a/?sig=x " }""");
        Assert.Equal(60, set.ApiBudgetPerMinute);
        Assert.Equal(240, set.ThrottlePauseMinutes);
        Assert.Equal(0, set.WalkPrefetchFolders);
        Assert.Equal("https://a/?sig=x", set.MonitoringUrl);

        Assert.Equal(0, Load("""{ "ApiBudgetPerMinute": 0 }""").ApiBudgetPerMinute);
    }

    [Fact]
    public void Effective_settings_name_their_source_and_hide_the_sas()
    {
        var settings = Load("""
            { "DriveLetter": "M", "ReadAheadFiles": 0,
              "MonitoringUrl": "https://contoso.table.core.windows.net/?sv=2026-02-06&ss=t&srt=o&sp=a&se=2027-01-01T00:00:00Z&sig=secret123" }
            """);
        var effective = settings.Effective().ToDictionary(s => s.Name);

        Assert.Equal(("M", Path.GetFileName(file)), (effective["DriveLetter"].Value, effective["DriveLetter"].Source));
        Assert.Equal(("VFS365", "default"), (effective["Label"].Value, effective["Label"].Source));
        Assert.Equal("0 (off)", effective["ReadAheadFiles"].Value);
        Assert.StartsWith("contoso.table.core.windows.net", effective["MonitoringUrl"].Value);
        Assert.DoesNotContain(settings.Effective(), s => s.Value.Contains("secret123") || s.Value.Contains("sig="));
        Assert.Equal(33, effective.Count);
        Assert.Equal(AgentSettings.Names.Order(), effective.Keys.Order());
    }

    [Fact]
    public void Upload_notices_and_monitoring_detail()
    {
        var defaults = Load("{}");
        Assert.True(defaults.NotifyUploads);
        Assert.Equal(MonitoringDetail.Full, defaults.MonitoringDetail);

        var set = Load("""{ "NotifyUploads": 0, "MonitoringDetail": "nopaths" }""");
        Assert.False(set.NotifyUploads);
        Assert.Equal(MonitoringDetail.NoPaths, set.MonitoringDetail);
        Assert.Equal(MonitoringDetail.Full, Load("""{ "MonitoringDetail": "some" }""").MonitoringDetail);
    }

    [Fact]
    public void Values_outside_their_range_are_named_for_the_log()
    {
        var adjusted = Load("""{ "ChangeCheckSeconds": 3, "ApiBudgetPerMinute": 10, "CacheSizeMB": 50, "ReadAheadFiles": 8 }""").Adjusted();

        Assert.Equal(3, adjusted.Count);
        Assert.Contains("ChangeCheckSeconds 3 is outside its range; 5 is used", adjusted);
        Assert.Contains("ApiBudgetPerMinute 10 is outside its range; 60 is used", adjusted);
        Assert.Contains("CacheSizeMB 50 is outside its range; 100 is used", adjusted);
        Assert.Empty(Load("""{ "ChangeCheckSeconds": 0, "ApiBudgetPerMinute": 0 }""").Adjusted());
    }

    [Fact]
    public void Settings_are_read_once_and_changes_are_named()
    {
        var started = Load("""{ "Label": "Files", "ReadAheadFiles": 8 }""");
        Assert.Equal("Files", started.Label);
        Assert.Equal(8, started.ReadAheadFiles);

        var now = Load("""{ "Label": "Files", "ReadAheadFiles": 4, "NotifyUploads": false }""");

        Assert.Equal(8, started.ReadAheadFiles); // the running agent keeps what it started with
        Assert.Equal(["NotifyUploads", "ReadAheadFiles"], now.ChangedFrom(started).Order(StringComparer.Ordinal));
        Assert.Empty(now.ChangedFrom(now));
    }

    public void Dispose() => File.Delete(file);
}
