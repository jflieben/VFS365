using System.Net;
using System.Net.Sockets;

namespace Vfs365.Agent;

/// <summary>
/// Whether the name VFS365 resolves on the network (DNS with its suffixes, LLMNR, NetBIOS, the hosts file). It shouldn't: while the
/// drive isn't mounted (before the start, after a crash, refusal or sign-out), Windows sends \\VFS365\... paths that apps and Explorer
/// kept to whoever answers for the name, over SMB or WebDAV and with the user's sign-in (NTLM). An answer is an attacker on the
/// network (LLMNR or NetBIOS spoofing, a DNS record any domain user can add) or a misconfiguration.
/// </summary>
public static class ServerNameCheck
{
    public const string Name = "VFS365";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>The addresses VFS365 resolves to; empty when it doesn't, or no answer came within Timeout.</summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync()
    {
        using var cancel = new CancellationTokenSource(Timeout);
        try
        {
            var lookup = Dns.GetHostAddressesAsync(Name, cancel.Token);
            return await Task.WhenAny(lookup, Task.Delay(Timeout + TimeSpan.FromSeconds(1))) == lookup ? await lookup : [];
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// The warning for the log and monitoring, or null when the name doesn't resolve, or only to 0.0.0.0 or this PC (an admin's record
    /// that keeps others from registering the name).
    /// </summary>
    public static string? Warning(IReadOnlyList<IPAddress> addresses)
    {
        var reachable = addresses.Where(a => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).Distinct().ToList();
        return reachable.Count == 0 ? null
            : $"the name {Name} resolves on the network, to {string.Join(", ", reachable)}. While the drive isn't mounted (before the start, after a " +
              $@"crash or sign-out), Windows sends \\{Name} paths that apps kept to that address, with the user's sign-in (NTLM). An attacker on the " +
              "network (LLMNR or NetBIOS answers, a DNS record) or a misconfiguration can receive them. Remove that DNS record or hosts entry (one " +
              "pointing to 0.0.0.0, owned by an admin, is fine), and turn off LLMNR and NetBIOS name resolution";
    }
}
