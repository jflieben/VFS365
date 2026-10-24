using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>The DriveLetter setting as WinFsp takes it.</summary>
/// <param name="MountPoint">"M:", or null for the first free letter (also used with None, which removes the letter after mounting).</param>
/// <param name="KeepLetter">False for None: only the UNC path and the navigation pane.</param>
/// <param name="Problem">Why the value isn't usable; the drive then mounts without a letter.</param>
public sealed record DriveLetterOption(string? MountPoint, bool KeepLetter, string? Problem = null)
{
    public static readonly DriveLetterOption None = new(null, false);

    /// <summary>"M", "m", "M:", "M:\" and "m:\" give "M:"; "*" the first free letter; "None" or nothing none.</summary>
    public static DriveLetterOption Parse(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }
        if (text == "*")
        {
            return new(null, true);
        }
        var letter = text.TrimEnd('\\', '/').TrimEnd(':');
        return letter.Length == 1 && char.IsAsciiLetter(letter[0]) && text.Length - letter.Length <= 2
            ? new($"{char.ToUpperInvariant(letter[0])}:", true)
            : None with { Problem = $"DriveLetter '{text}' is not a letter, * or None" };
    }
}

/// <summary>
/// What Windows tells Explorer about the drive, as seen from the agent's own sign-in. Explorer labels a letter "Disconnected Network
/// Drive" when no network provider reports it (WinFsp.Np missing from the provider order), when a remembered mapping
/// (HKCU\Network\&lt;letter&gt;) holds the letter for another share, or when the letter is gone while the drive still works by UNC path.
/// </summary>
public sealed record DriveLetterReport(string Letter, string? Target, int ConnectionError, string? RemoteName, string? Remembered, bool WinFspInOrder)
{
    /// <summary>Reads it now; <paramref name="letter"/> like "T:".</summary>
    public static DriveLetterReport Read(string letter)
    {
        var target = DosDevices.Target(letter);
        var error = DosDevices.Connection(letter, out var remote);
        string? remembered = null;
        try
        {
            using var mapping = Registry.CurrentUser.OpenSubKey($@"Network\{letter.TrimEnd(':')}");
            remembered = mapping?.GetValue("RemotePath") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return new(letter, target, error, remote, remembered,
            NetworkProviderOrder.Current().Contains(NetworkProviderOrder.WinFsp, StringComparer.OrdinalIgnoreCase));
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
                "although it works." + (WinFspInOrder ? "" : " WinFsp.Np is missing from the network provider order (HKLM\\" + NetworkProviderOrder.OrderKey + "), which a policy or another " +
                "installer can replace. Put WinFsp.Np back in it, or run 'vfs365.exe machine-setup' as an administrator."));
        }
        else if (RemoteName is not null && !RemoteName.Equals(unc, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"Windows reports {Letter} as {RemoteName}, not as VFS365's {unc}.");
        }
        return problems;
    }
}

/// <summary>Drive letters through Windows: DOS devices of the session (as subst makes them) and network connections.</summary>
internal static class DosDevices
{
    const uint RawTargetPath = 1, RemoveDefinition = 2, ExactMatchOnRemove = 4;

    /// <summary>What <paramref name="letter"/> ("T:") points at, or null when it is free.</summary>
    public static string? Target(string letter)
    {
        var target = new StringBuilder(1024);
        return QueryDosDevice(letter, target, target.Capacity) > 0 ? target.ToString() : null;
    }

    /// <summary>Gives the letter <paramref name="device"/> (\??\UNC\...) as its raw target path; when false, <c>new Win32Exception()</c> tells why.</summary>
    public static bool Define(string letter, string device) => DefineDosDevice(RawTargetPath, letter, device);

    /// <summary>Removes the letter's definition when it is <paramref name="device"/>.</summary>
    public static void Remove(string letter, string device) => DefineDosDevice(RawTargetPath | RemoveDefinition | ExactMatchOnRemove, letter, device);

    /// <summary>Disconnects the network drive on the letter, open files or not; a remembered mapping goes too (CONNECT_UPDATE_PROFILE).</summary>
    public static void Disconnect(string letter) => WNetCancelConnection2(letter, 1, true);

    /// <summary>WNetGetConnection: 0 and the remote name a network provider reports for the letter, else the error and null.</summary>
    public static int Connection(string letter, out string? remote)
    {
        var name = new StringBuilder(1024);
        var length = name.Capacity;
        var error = WNetGetConnection(letter, name, ref length);
        remote = error == 0 ? name.ToString() : null;
        return error;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint QueryDosDevice(string device, StringBuilder target, int max);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool DefineDosDevice(uint flags, string device, string target);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetGetConnection(string local, StringBuilder remote, ref int length);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetCancelConnection2(string name, uint flags, bool force);
}
