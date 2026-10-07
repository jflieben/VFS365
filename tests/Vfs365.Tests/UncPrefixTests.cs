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
}
