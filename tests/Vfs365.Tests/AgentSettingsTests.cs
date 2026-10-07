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
        Assert.Equal(DriveScope.SharePoint, settings.Scope);
        Assert.Null(settings.IncludedSites);
        Assert.True(settings.NavigationPane);
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
        Assert.Equal(["*/sites/HR*", "*/sites/Legal*"], settings.ExcludedSites);
    }

    [Fact]
    public void Unknown_scope_and_empty_values_fall_back_to_defaults()
    {
        var settings = Load("""{ "ClientId": "", "Scope": "Teams", "Label": "", "IncludedSites": [] }""");

        Assert.Equal(AgentSettings.DefaultClientId, settings.ClientId.Value);
        Assert.Equal(DriveScope.SharePoint, settings.Scope);
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

    public void Dispose() => File.Delete(file);
}
