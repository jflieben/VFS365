using Vfs365.Graph;

namespace Vfs365.Tests;

public sealed class TrustedHostsTests
{
    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/me/drive")]
    [InlineData("https://contoso.sharepoint.com/sites/x")]
    [InlineData("https://CONTOSO-my.sharepoint.com/personal/ann")]
    [InlineData("https://contosoeur.sharepoint.com/sites/x")]
    [InlineData("https://contosoEUR-my.sharepoint.com/personal/ann")]
    [InlineData("https://contoso.sharepoint.com:443/sites/x")]
    public void Trusted(string url)
    {
        Assert.True(TrustedHosts.IsTrusted(new Uri(url), "contoso.sharepoint.com"));
        Assert.True(TrustedHosts.IsTrusted(new Uri(url), "contoso-my.sharepoint.com"));
    }

    [Theory]
    [InlineData("http://contoso.sharepoint.com/sites/x")]
    [InlineData("http://graph.microsoft.com/v1.0/me")]
    [InlineData("https://contoso.sharepoint.com:8443/sites/x")]
    [InlineData("https://user@contoso.sharepoint.com/sites/x")]
    [InlineData("https://fabrikam.sharepoint.com/sites/x")]
    [InlineData("https://evilcontoso.sharepoint.com/sites/x")]
    [InlineData("https://contosoabcd.sharepoint.com/sites/x")]
    [InlineData("https://contoso-admin.sharepoint.com/")]
    [InlineData("https://contoso.sharepoint.com.evil.example/sites/x")]
    [InlineData("https://x.contoso.sharepoint.com/")]
    [InlineData("https://contoso.sharepoint.us/sites/x")]
    [InlineData("https://graph.microsoft.com.evil.example/")]
    [InlineData("ftp://contoso.sharepoint.com/")]
    public void Not_trusted(string url) => Assert.False(TrustedHosts.IsTrusted(new Uri(url), "contoso.sharepoint.com"));
    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/x", "contosoeur-my.sharepoint.com", true)]
    [InlineData("https://contosoapc.sharepoint.com/sites/x", "contosoeur-my.sharepoint.com", true)]
    [InlineData("https://contosoxyz.sharepoint.com/sites/x", "contosoeur-my.sharepoint.com", false)]
    [InlineData("https://contxyz.sharepoint.com/sites/x", "contoso.sharepoint.com", false)]
    public void Multi_geo_locations_of_one_tenant_trust_each_other(string url, string root, bool trusted) =>
        Assert.Equal(trusted, TrustedHosts.IsTrusted(new Uri(url), root));
}
