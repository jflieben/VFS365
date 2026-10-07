using System.Text.RegularExpressions;
using Vfs365.Graph;

namespace Vfs365.Tests;

public class ResourceUnitsTests
{
    [Theory]
    [InlineData(GraphOperation.GetItem, 1)]
    [InlineData(GraphOperation.Download, 1)]
    [InlineData(GraphOperation.DeltaWithToken, 1)]
    [InlineData(GraphOperation.DeltaWithoutToken, 2)]
    [InlineData(GraphOperation.ListChildren, 2)]
    [InlineData(GraphOperation.Upload, 2)]
    [InlineData(GraphOperation.Delete, 2)]
    [InlineData(GraphOperation.Permissions, 5)]
    public void Cost_matches_published_table(GraphOperation operation, int expected)
    {
        Assert.Equal(expected, ResourceUnits.Cost(operation));
    }

    [Theory]
    [InlineData(1, 1_250, 1_200_000)]
    [InlineData(1_000, 1_250, 1_200_000)]
    [InlineData(1_001, 2_500, 2_400_000)]
    [InlineData(15_000, 3_750, 3_600_000)]
    [InlineData(50_000, 5_000, 4_800_000)]
    [InlineData(60_000, 6_250, 6_000_000)]
    public void AppBudget_follows_license_bands(int licenses, int perMinute, int per24Hours)
    {
        Assert.Equal(new AppBudget(perMinute, per24Hours), ResourceUnits.AppBudgetFor(licenses));
    }

    [Theory]
    [InlineData("GET", "https://graph.microsoft.com/v1.0/drives/b!x/root:/a:/children?$select=id&$top=200", true, 2)]
    [InlineData("GET", "https://graph.microsoft.com/v1.0/drives/b!x/root:/a.docx?$select=id", true, 1)]
    [InlineData("GET", "https://graph.microsoft.com/v1.0/drives/b!x/root/delta?token=latest", true, 1)]
    [InlineData("GET", "https://graph.microsoft.com/v1.0/drives/b!x/root/delta", true, 2)]
    [InlineData("GET", "https://x.sharepoint.com/sites/a/_api/web?$select=Id", true, 2)]
    [InlineData("PUT", "https://graph.microsoft.com/v1.0/drives/b!x/items/1/content", true, 2)]
    [InlineData("PUT", "https://x.sharepoint.com/upload?token=abc", false, 0)]
    [InlineData("GET", "https://x.sharepoint.com/download.aspx?token=abc", false, 0)]
    public void Estimate_prices_requests_like_the_table(string method, string url, bool authenticated, int expected)
    {
        Assert.Equal(expected, ResourceUnits.Estimate(new HttpMethod(method), new Uri(url), authenticated));
    }

    [Fact]
    public void Budget_holds_a_minute_and_refills()
    {
        var now = DateTimeOffset.UnixEpoch;
        var budget = new RequestBudget(60, () => now);
        for (var i = 0; i < 30; i++)
        {
            Assert.True(budget.TryTake(2, background: false, out _));
        }
        Assert.False(budget.TryTake(2, background: false, out var wait));
        Assert.Equal(TimeSpan.FromSeconds(2), wait);

        now += TimeSpan.FromSeconds(2);
        Assert.True(budget.TryTake(2, background: false, out _));
    }

    [Fact]
    public void Background_work_only_uses_the_upper_half()
    {
        var now = DateTimeOffset.UnixEpoch;
        var budget = new RequestBudget(60, () => now);
        Assert.True(budget.TryTake(28, background: true, out _));
        Assert.False(budget.TryTake(4, background: true, out var wait));
        Assert.True(wait > TimeSpan.Zero);
        Assert.True(budget.TryTake(30, background: false, out _));
    }

    [Fact]
    public void No_budget_never_waits()
    {
        var budget = new RequestBudget(0);
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(budget.TryTake(2, background: true, out _));
        }
    }

    [Fact]
    public void UserAgent_is_decorated_as_isv()
    {
        Assert.Matches(new Regex(@"^ISV\|JSolve\|VFS365/\d+\.\d+\.\d+$"), GraphUserAgent.Value);
    }
}
