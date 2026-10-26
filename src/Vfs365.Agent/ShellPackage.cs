using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Vfs365.Agent;

/// <summary>
/// The VFS365 item in Windows 11's context menu: the sparse package vfs365shell.msix (src\Vfs365.Shell\package) in the install folder
/// registers vfs365shell.dll's menu class. Signed releases ship it, unsigned ones don't (Windows refuses unsigned packages). It is
/// registered per user, with the install folder as its external location, by Windows PowerShell's Add-AppxPackage: Microsoft's
/// documented route, in every Windows 11, and needed once per version. Registration is read through kernel32's
/// GetPackagesByPackageFamily, without PowerShell. machine-stop --cleanup removes it for all users (<see cref="RemoveForAllUsers"/>).
/// </summary>
public static class ShellPackage
{
    public const string PackageName = "JSolve.VFS365.Shell";
    public const string FileName = "vfs365shell.msix";

    /// <summary>Windows 11 (build 22000): Windows 10's context menu has no place for packaged commands.</summary>
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static string Folder => AppContext.BaseDirectory.TrimEnd('\\');

    public static string PackagePath => Path.Combine(Folder, FileName);

    /// <summary>A package's identity: its name, the publisher (the signing certificate's subject) and version.</summary>
    public sealed record Identity(string Name, string Publisher, Version Version)
    {
        /// <summary>Name_PublisherId, as Windows names the package family.</summary>
        public string FamilyName => $"{Name}_{PublisherId(Publisher)}";
    }

    static readonly Lazy<Identity?> shipped = new(() => ReadIdentity(PackagePath));
    static readonly SemaphoreSlim registering = new(1, 1);

    /// <summary>The identity in the package this build ships; null when there is none (unsigned or development build).</summary>
    public static Identity? Shipped => shipped.Value;

    /// <summary>
    /// The package's menu is in Windows' context menu for this user: Windows 11, a package shipped and some version of it registered.
    /// The legacy HKCU verbs (<see cref="ExplorerVerbs"/>) are then not needed: Show more options would list the items twice.
    /// </summary>
    public static bool IsActive => Supported && Shipped is { } identity && Registered(identity.FamilyName).Count > 0;

    /// <summary>
    /// Registers the shipped package for this user when this version (or a later one) isn't registered yet; true when it is registered
    /// afterwards. Runs in the background and never throws: the drive works without it, Explorer then keeps the legacy verbs.
    /// </summary>
    public static Task<bool> EnsureRegisteredAsync(Action<string> log, CancellationToken ct = default) => Task.Run(async () =>
    {
        try
        {
            if (!Supported || Shipped is not { } identity)
            {
                return false;
            }
            await registering.WaitAsync(ct);
            try
            {
                if (Registered(identity.FamilyName).Any(n => VersionOf(n) >= identity.Version))
                {
                    return true;
                }
                log($"Explorer menu: registering {PackageName} {identity.Version} for the Windows 11 context menu");
                // -ForceApplicationShutdown: an update ends the package's COM surrogate (Explorer starts it again when needed)
                var (exit, output) = await PowerShellAsync(
                    $"Add-AppxPackage -Path {Quote(PackagePath)} -ExternalLocation {Quote(Folder)} -ForceApplicationShutdown", TimeSpan.FromMinutes(3), ct);
                var registered = Registered(identity.FamilyName).Any(n => VersionOf(n) >= identity.Version);
                log(registered
                    ? $"Explorer menu: {PackageName} {identity.Version} registered"
                    : $"Explorer menu: could not register {PackageName} (PowerShell exit {exit}): {Last(output)}");
                return registered;
            }
            finally
            {
                registering.Release();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"Explorer menu: could not register {PackageName}: {e.Message}");
            return false;
        }
    }, CancellationToken.None);

    /// <summary>
    /// Removes the package for every user (as SYSTEM, machine-stop --cleanup at uninstall): Explorer would otherwise keep a menu
    /// registration pointing at the removed install folder. Users not signed in included.
    /// </summary>
    public static void RemoveForAllUsers(bool dryRun, Action<string> say)
    {
        if (!Supported)
        {
            return;
        }
        if (dryRun)
        {
            say($"Would remove the {PackageName} package (Windows 11 context menu) for all users");
            return;
        }
        say($"Removing the {PackageName} package (Windows 11 context menu) for all users");
        try
        {
            var (exit, output) = PowerShellAsync(
                $"Get-AppxPackage -AllUsers -Name {Quote(PackageName)} | ForEach-Object {{ Remove-AppxPackage -Package $_.PackageFullName -AllUsers }}",
                TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
            if (exit != 0)
            {
                say($"Could not remove the {PackageName} package (PowerShell exit {exit}): {Last(output)}");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            say($"Could not remove the {PackageName} package: {e.Message}");
        }
    }

    /// <summary>The Identity of a package's AppxManifest.xml; null when the file is missing or isn't a package.</summary>
    public static Identity? ReadIdentity(string msix)
    {
        try
        {
            if (!File.Exists(msix))
            {
                return null;
            }
            using var package = ZipFile.OpenRead(msix);
            using var manifest = package.GetEntry("AppxManifest.xml")?.Open();
            if (manifest is null)
            {
                return null;
            }
            var root = XDocument.Load(manifest).Root;
            var identity = root?.Element(root.Name.Namespace + "Identity");
            return identity?.Attribute("Name")?.Value is { } name && identity.Attribute("Publisher")?.Value is { } publisher &&
                Version.TryParse(identity.Attribute("Version")?.Value, out var version)
                ? new Identity(name, publisher, version)
                : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The 13-character publisher ID in package family names: the first 8 bytes of the SHA-256 of the publisher (UTF-16LE), as 5-bit
    /// groups in Crockford's base 32.
    /// </summary>
    public static string PublisherId(string publisher)
    {
        const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        var bits = (ulong)hash[0] << 56 | (ulong)hash[1] << 48 | (ulong)hash[2] << 40 | (ulong)hash[3] << 32 |
            (ulong)hash[4] << 24 | (ulong)hash[5] << 16 | (ulong)hash[6] << 8 | hash[7];
        var id = new char[13];
        for (var i = 0; i < 13; i++)
        {
            // 65 bits: the 64 of the hash and a zero
            var shift = 59 - 5 * i;
            id[i] = Alphabet[(int)(shift >= 0 ? bits >> shift : bits << -shift) & 31];
        }
        return new string(id);
    }

    /// <summary>The version in a package full name (Name_Version_Architecture_ResourceId_PublisherId).</summary>
    public static Version? VersionOf(string fullName) =>
        fullName.Split('_') is { Length: 5 } parts && Version.TryParse(parts[1], out var version) ? version : null;

    /// <summary>Full names of this user's registered packages of a family; empty when none or it can't be read.</summary>
    public static IReadOnlyList<string> Registered(string familyName)
    {
        uint count = 0, length = 0;
        var result = GetPackagesByPackageFamily(familyName, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        if (result != InsufficientBuffer || count == 0)
        {
            return [];
        }
        var names = Marshal.AllocHGlobal((int)count * IntPtr.Size);
        var buffer = Marshal.AllocHGlobal((int)length * 2);
        try
        {
            if (GetPackagesByPackageFamily(familyName, ref count, names, ref length, buffer) != 0)
            {
                return [];
            }
            return [.. Enumerable.Range(0, (int)count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size)) ?? "")];
        }
        finally
        {
            Marshal.FreeHGlobal(names);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>A PowerShell single-quoted string.</summary>
    internal static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    static string Last(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no output";

    /// <summary>Runs a command in Windows PowerShell 5.1, whose Appx module is the full one, without a window; its exit code and output.</summary>
    static async Task<(int Exit, string Output)> PowerShellAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "$ErrorActionPreference = 'Stop'; " + command })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"PowerShell did not finish within {timeout.TotalMinutes:0} minutes");
        }
        return (process.ExitCode, (await output) + (await error));
    }

    const int InsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetPackagesByPackageFamily(string familyName, ref uint count, IntPtr fullNames, ref uint bufferLength, IntPtr buffer);
}
