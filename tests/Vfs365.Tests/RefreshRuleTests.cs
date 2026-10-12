using System.Globalization;
using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>Refresh in the tray: once a day, and only when the libraries were read more than an hour ago.</summary>
public sealed class RefreshRuleTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US"), Dutch = CultureInfo.GetCultureInfo("nl-NL");

    static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    [Fact]
    public void Allowed_when_the_libraries_are_over_an_hour_old_and_no_refresh_today() =>
        Assert.Null(RefreshRule.Blocked(Now, Now.AddHours(-2), Now.AddHours(-25), running: false, English));

    [Fact]
    public void Allowed_without_any_earlier_refresh() =>
        Assert.Null(RefreshRule.Blocked(Now, Now.AddDays(-1), null, running: false, English));

    [Fact]
    public void Once_a_day()
    {
        var reason = RefreshRule.Blocked(Now, Now.AddHours(-5), Now.AddHours(-3), running: false, English);
        Assert.NotNull(reason);
        Assert.Contains("once a day", reason);
        Assert.Contains(Local(Now.AddHours(-3).AddDays(1)), reason);
    }

    [Fact]
    public void Not_within_an_hour_of_reading_the_libraries()
    {
        var reason = RefreshRule.Blocked(Now, Now.AddMinutes(-20), null, running: false, English);
        Assert.NotNull(reason);
        Assert.Contains("20 minute(s) ago", reason);
        Assert.Contains(Local(Now.AddMinutes(40)), reason);
    }

    [Fact]
    public void Not_while_one_runs() => Assert.Equal("Refreshing now.", RefreshRule.Blocked(Now, Now.AddDays(-1), null, running: true, English));

    [Fact]
    public void Dutch_windows_gets_dutch_text()
    {
        Assert.Contains("één keer per dag", RefreshRule.Blocked(Now, Now.AddHours(-5), Now.AddHours(-3), running: false, Dutch));
        Assert.Equal("Wordt nu vernieuwd.", RefreshRule.Blocked(Now, Now.AddDays(-1), null, running: true, Dutch));
    }

    [Fact]
    public void The_menu_item_says_from_when_it_works()
    {
        Assert.Equal("&Refresh", RefreshRule.Label(Now, Now.AddHours(-2), null, running: false, English));
        Assert.Equal("&Refresh (running)", RefreshRule.Label(Now, Now.AddHours(-2), null, running: true, English));
        Assert.Equal($"&Refresh (from {Local(Now.AddMinutes(40))})", RefreshRule.Label(Now, Now.AddMinutes(-20), null, running: false, English));
        // Both rules: the later time counts
        var label = RefreshRule.Label(Now, Now.AddMinutes(-20), Now.AddHours(-3), running: false, Dutch);
        Assert.Contains(Local(Now.AddHours(21)), label);
        Assert.StartsWith("&Vernieuwen (vanaf ", label);
    }

    [Fact]
    public void Onedrive_scope_only_has_the_daily_rule() =>
        Assert.Null(RefreshRule.Blocked(Now, DateTimeOffset.MinValue, null, running: false, English));
}
