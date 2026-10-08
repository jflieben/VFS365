using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>Refresh in the tray: once a day, and only when the libraries were read more than an hour ago.</summary>
public sealed class RefreshRuleTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Allowed_when_the_libraries_are_over_an_hour_old_and_no_refresh_today() =>
        Assert.Null(RefreshRule.Blocked(Now, Now.AddHours(-2), Now.AddHours(-25), running: false));

    [Fact]
    public void Allowed_without_any_earlier_refresh() =>
        Assert.Null(RefreshRule.Blocked(Now, Now.AddDays(-1), null, running: false));

    [Fact]
    public void Once_a_day()
    {
        var reason = RefreshRule.Blocked(Now, Now.AddHours(-5), Now.AddHours(-3), running: false);
        Assert.NotNull(reason);
        Assert.Contains("once a day", reason);
        Assert.Contains((Now.AddHours(-3).AddDays(1)).ToLocalTime().ToString("HH:mm"), reason);
    }

    [Fact]
    public void Not_within_an_hour_of_reading_the_libraries()
    {
        var reason = RefreshRule.Blocked(Now, Now.AddMinutes(-20), null, running: false);
        Assert.NotNull(reason);
        Assert.Contains("20 minute(s) ago", reason);
        Assert.Contains(Now.AddMinutes(40).ToLocalTime().ToString("HH:mm"), reason);
    }

    [Fact]
    public void Not_while_one_runs() => Assert.Equal("Refreshing now.", RefreshRule.Blocked(Now, Now.AddDays(-1), null, running: true));
}
