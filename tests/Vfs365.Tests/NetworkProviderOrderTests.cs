using Vfs365.Agent;

namespace Vfs365.Tests;

public sealed class NetworkProviderOrderTests
{
    [Theory]
    [InlineData("WinFsp.Np,RDPNP,P9NP,LanmanWorkstation,webclient", "WinFsp.Np,VFS365.Np,RDPNP,P9NP,LanmanWorkstation,webclient")]
    [InlineData("RDPNP,LanmanWorkstation,webclient", "VFS365.Np,RDPNP,LanmanWorkstation,webclient")]
    [InlineData("RDPNP,winfsp.np,LanmanWorkstation", "RDPNP,winfsp.np,VFS365.Np,LanmanWorkstation")]
    [InlineData("VFS365.Np,LanmanWorkstation,WinFsp.Np", "LanmanWorkstation,WinFsp.Np,VFS365.Np")]
    [InlineData("", "VFS365.Np")]
    public void Added_after_WinFsp_once(string order, string expected)
    {
        Assert.Equal(expected, NetworkProviderOrder.With(order));
        Assert.Equal(expected, NetworkProviderOrder.With(NetworkProviderOrder.With(order)));
    }

    [Theory]
    [InlineData("WinFsp.Np,VFS365.Np,RDPNP,LanmanWorkstation", "WinFsp.Np,RDPNP,LanmanWorkstation")]
    [InlineData("vfs365.np", "")]
    [InlineData("WinFsp.Np,RDPNP", "WinFsp.Np,RDPNP")]
    public void Removed(string order, string expected) => Assert.Equal(expected, NetworkProviderOrder.Without(order));
}
