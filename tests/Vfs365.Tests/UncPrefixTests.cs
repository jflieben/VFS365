using Vfs365.Agent;

namespace Vfs365.Tests;

public sealed class UncPrefixTests
{
    [Theory]
    [InlineData("JaneDoe", @"\VFS365\JaneDoe")]
    [InlineData("Jane Doe", @"\VFS365\Jane Doe")]
    [InlineData("a.b-c", @"\VFS365\a.b-c")]
    [InlineData("x[1]+y", @"\VFS365\x_1__y")]
    public void One_share_per_user(string user, string prefix) => Assert.Equal(prefix, AgentCommands.UncPrefix(user));

    [Theory]
    [InlineData(@"FABRIKAM\ann", @"FABRIKAM\ann")]  // the same name in another domain
    [InlineData(@"AzureAD\Ann", @"AzureAD\Ann")]    // a cloud account next to a local one
    [InlineData(@"CONTOSO\bob", null)]              // another user: a plain security problem
    [InlineData(@"CONTOSO\ann", null)]              // the same account
    [InlineData(null, null)]                        // owner unknown
    public void Another_account_with_the_same_user_name_is_named(string? holder, string? named) =>
        Assert.Equal(named, AgentCommands.SameNameHolder(holder, "CONTOSO", "ann"));

    [Fact]
    public void The_server_name_resolving_on_the_network_is_a_warning()
    {
        // An LLMNR answer (Responder) or a DNS record: \\VFS365 paths would go there while the drive isn't mounted
        var warning = ServerNameCheck.Warning([System.Net.IPAddress.Parse("10.1.2.3"), System.Net.IPAddress.Parse("10.1.2.3")]);
        Assert.NotNull(warning);
        Assert.Contains("resolves on the network, to 10.1.2.3.", warning);
        Assert.Contains("LLMNR", warning);

        Assert.Null(ServerNameCheck.Warning([]));
        // An admin's record that only holds the name, or the hosts file sending it to this PC
        Assert.Null(ServerNameCheck.Warning([System.Net.IPAddress.Any, System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback]));
    }
}
