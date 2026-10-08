using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>
/// What Windows tells Explorer about the drive, as seen from the agent's own sign-in. Explorer labels a letter "Disconnected Network
/// Drive" when no network provider reports it (WinFsp.Np missing from the provider order), when a remembered mapping
/// (HKCU\Network\&lt;letter&gt;) holds the letter for another share, or when the letter is gone while the drive still works by UNC path.
/// </summary>
public sealed record DriveLetterReport(string Letter, string? Target, int ConnectionError, string? RemoteName, string? Remembered, bool WinFspInOrder, bool VfsInOrder)
{
    const string OrderKey = @"SYSTEM\CurrentControlSet\Control\NetworkProvider\Order";

    /// <summary>Reads it now; <paramref name="letter"/> like "T:".</summary>
    public static DriveLetterReport Read(string letter)
    {
        var target = new StringBuilder(1024);
        var hasTarget = QueryDosDevice(letter, target, target.Capacity) > 0;
        var remote = new StringBuilder(1024);
        var length = remote.Capacity;
        var error = WNetGetConnection(letter, remote, ref length);
        string? remembered = null;
        try
        {
            using var mapping = Registry.CurrentUser.OpenSubKey($@"Network\{letter.TrimEnd(':')}");
            remembered = mapping?.GetValue("RemotePath") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        var order = ProviderOrder();
        return new(letter, hasTarget ? target.ToString() : null, error, error == 0 ? remote.ToString() : null, remembered,
            order.Contains("WinFsp.Np", StringComparer.OrdinalIgnoreCase), order.Contains("VFS365.Np", StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> ProviderOrder()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(OrderKey);
            return (key?.GetValue("ProviderOrder") as string ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>What is wrong and how to fix it; empty when Windows shows the letter as VFS365's <paramref name="unc"/>.</summary>
    public IReadOnlyList<string> Problems(string unc)
    {
        var problems = new List<string>();
        var name = Letter.TrimEnd(':');
        if (Target is null)
        {
            problems.Add($"{Letter} is gone: something removed the drive letter (a logon script with 'net use * /delete', another drive mapping tool). " +
                $"Files stay reachable at {unc}; Restart in the tray assigns {Letter} again.");
        }
        if (Remembered is not null && !Remembered.Equals(unc, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{Letter} is also remembered as a mapping to {Remembered} (HKCU\\Network\\{name}, from a drive mapping policy, a logon script or " +
                $"'net use /persistent:yes'). Explorer can show that mapping as a disconnected network drive, and when that share is reachable at sign-in " +
                $"it takes {Letter} before VFS365 can. Remove {Letter} from that policy or script, or delete HKCU\\Network\\{name} (not with 'net use {Letter} /delete', which would disconnect VFS365).");
        }
        if (Target is not null && ConnectionError != 0)
        {
            problems.Add($"No network provider reports {Letter} (WNetGetConnection error {ConnectionError}), so Explorer shows it as a disconnected network drive " +
                "although it works." + (WinFspInOrder ? "" : " WinFsp.Np is missing from the network provider order (HKLM\\" + OrderKey + "), which a policy or another " +
                "installer can replace. Put WinFsp.Np back in it, or run 'vfs365.exe machine-setup' as an administrator."));
        }
        else if (RemoteName is not null && !RemoteName.Equals(unc, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"Windows reports {Letter} as {RemoteName}, not as VFS365's {unc}.");
        }
        return problems;
    }

    /// <summary>A provider order without VFS365.Np: typed or pasted \\VFS365 paths don't open in Explorer (the drive itself works).</summary>
    public static string? ProviderProblem(IReadOnlyList<string> order) => order.Contains("VFS365.Np", StringComparer.OrdinalIgnoreCase)
        ? null
        : "VFS365.Np is not in the network provider order (HKLM\\" + OrderKey + "): typed or pasted \\\\VFS365 paths won't open in Explorer. " +
          "Run 'vfs365.exe machine-setup' as an administrator, or add VFS365.Np after WinFsp.Np in the policy that sets that order.";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint QueryDosDevice(string device, StringBuilder target, int max);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetGetConnection(string local, StringBuilder remote, ref int length);
}
