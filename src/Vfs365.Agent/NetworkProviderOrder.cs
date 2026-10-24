using System.Security;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>
/// Puts VFS365's network provider (vfs365np.dll; the MSI installs it and its Services key) in Windows' provider order, right after
/// WinFsp's: typed \\VFS365 paths then resolve before SMB tries a server named VFS365. Machine-wide; the installer runs this as SYSTEM.
/// </summary>
public static class NetworkProviderOrder
{
    public const string Name = "VFS365.Np";
    internal const string WinFsp = "WinFsp.Np";
    const string Key = @"SYSTEM\CurrentControlSet\Control\NetworkProvider";
    internal const string OrderKey = Key + @"\Order";

    /// <summary>The providers in Windows' order now; empty when it can't be read.</summary>
    public static IReadOnlyList<string> Current()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(OrderKey);
            return (key?.GetValue("ProviderOrder") as string ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>A provider order without VFS365.Np: typed or pasted \\VFS365 paths don't open in Explorer (the drive itself works).</summary>
    public static string? Problem(IReadOnlyList<string> order) => order.Contains(Name, StringComparer.OrdinalIgnoreCase)
        ? null
        : "VFS365.Np is not in the network provider order (HKLM\\" + OrderKey + "): typed or pasted \\\\VFS365 paths won't open in Explorer. " +
          "Run 'vfs365.exe machine-setup' as an administrator, or add VFS365.Np after WinFsp.Np in the policy that sets that order.";

    /// <summary>The order with VFS365.Np right after WinFsp.Np, or first when WinFsp.Np is missing.</summary>
    public static string With(string order)
    {
        var providers = Without(order).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        providers.Insert(providers.FindIndex(p => p.Equals(WinFsp, StringComparison.OrdinalIgnoreCase)) + 1, Name);
        return string.Join(',', providers);
    }

    public static string Without(string order) => string.Join(',',
        order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => !p.Equals(Name, StringComparison.OrdinalIgnoreCase)));

    public static void Add(bool dryRun, Action<string> say) => Update(With, dryRun, say);

    public static void Remove(bool dryRun, Action<string> say) => Update(Without, dryRun, say);

    // HwOrder too: Windows upgrades can rebuild ProviderOrder from it
    static void Update(Func<string, string> change, bool dryRun, Action<string> say)
    {
        foreach (var list in new[] { "Order", "HwOrder" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{Key}\{list}", writable: !dryRun);
                if (key?.GetValue("ProviderOrder") is not string order || change(order) is var updated && updated == order)
                {
                    continue;
                }
                say($"{(dryRun ? "Would set" : "Set")} {list}\\ProviderOrder: {updated}");
                if (!dryRun)
                {
                    key.SetValue("ProviderOrder", updated);
                }
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
            {
                say($"{list}\\ProviderOrder: no access ({e.Message}); run as administrator");
            }
        }
    }
}
