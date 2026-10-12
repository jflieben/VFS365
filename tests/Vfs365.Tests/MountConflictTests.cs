using System.Security.Principal;
using Vfs365.Drive.WinFsp;

namespace Vfs365.Tests;

public sealed class MountConflictTests
{
    static readonly SecurityIdentifier Me = new("S-1-5-21-1-2-3-1001");

    [Fact]
    public void Own_volume_is_no_security_alert()
    {
        var conflict = MountConflictException.For(@"\VFS365\ann", Me, Me, unchecked((int)0x80070050));

        Assert.False(conflict.OwnedByAnotherUser);
        Assert.Contains("already mounted by this user", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_owner_or_an_unreadable_root_is_a_security_alert()
    {
        var other = MountConflictException.For(@"\VFS365\ann", new SecurityIdentifier("S-1-5-21-1-2-3-1002"), Me, 0);
        var unreadable = MountConflictException.For(@"\VFS365\ann", null, Me, 0);

        Assert.True(other.OwnedByAnotherUser);
        Assert.Equal("S-1-5-21-1-2-3-1002", other.Owner);
        Assert.True(unreadable.OwnedByAnotherUser);
        Assert.Null(unreadable.Owner);
        Assert.IsAssignableFrom<IOException>(unreadable);
    }
}
