using Microsoft.Win32;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Drive.WinFsp;
using Vfs365.Tests.Drive;

namespace Vfs365.Tests.Mounted;

/// <summary>Mounted-volume tests run one at a time (they measure latency) and only where WinFsp is installed.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MountedCollection
{
    public const string Name = "Mounted";
}

/// <summary>A test on a real WinFsp volume; skipped where WinFsp isn't installed (CI).</summary>
public sealed class MountedFactAttribute : FactAttribute
{
    public MountedFactAttribute()
    {
        if (MountedDrive.Missing is { } why)
        {
            Skip = why;
        }
    }
}

public sealed class MountedTheoryAttribute : TheoryAttribute
{
    public MountedTheoryAttribute()
    {
        if (MountedDrive.Missing is { } why)
        {
            Skip = why;
        }
    }
}

/// <summary>
/// A real WinFsp volume over the real file system, engine and content cache, with the fake drive behind them (the OneDrive at the
/// volume root). Mounted at a UNC prefix of its own (\\VFS365T\&lt;random&gt;), without a drive letter or Explorer node; used through
/// System.IO on <see cref="Root"/>. Dispose unmounts.
/// </summary>
sealed class MountedDrive : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly List<string> log = [];
    readonly DriveHost host;

    public FakeDriveApi Fake { get; }
    public SlowDriveApi Api { get; }
    public DriveEngine Engine { get; }

    /// <summary>The volume's UNC root, such as \\VFS365T\t0123abcd.</summary>
    public string Root { get; }

    public bool NamespaceLocked => host.NamespaceLocked;

    /// <summary>Why mounted tests can't run here, or null.</summary>
    public static string? Missing { get; } = FindMissing();

    static string? FindMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "needs Windows";
        }
        var installDir = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\WinFsp", "InstallDir", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WinFsp", "InstallDir", null) as string;
        var dll = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "winfsp-a64.dll",
            System.Runtime.InteropServices.Architecture.X86 => "winfsp-x86.dll",
            _ => "winfsp-x64.dll",
        };
        return installDir is not null && File.Exists(Path.Combine(installDir, "bin", dll)) ? null : "needs WinFsp installed";
    }

    MountedDrive(bool winFspGuard, Action<FakeDriveApi>? seed, Func<DriveEngineOptions, DriveEngineOptions>? configure, bool trace, DriveNamespace? ns)
    {
        Fake = new FakeDriveApi();
        seed?.Invoke(Fake);
        Api = new SlowDriveApi(Fake);
        var options = new DriveEngineOptions
        {
            StagingDirectory = Path.Combine(directory, "staging"),
            UploadNewFilesInBackground = true,
            Log = Log,
            Changed = change => notify?.Invoke(change),
        };
        Engine = new DriveEngine(
            ns ?? DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused"), DriveScope.OneDrive),
            Api,
            new ContentCache(Path.Combine(directory, "cache"), Api, new ContentCacheOptions()),
            configure?.Invoke(options) ?? options);
        var share = $"t{Guid.NewGuid():N}"[..12];
        host = DriveHost.Mount(Engine, null, false, $@"\VFS365T\{share}", "VFS365 test", Log, trace ? Log : null, winFspGuard);
        notify = host.Notify;
        Root = $@"\\VFS365T\{share}";
    }

    Action<EngineChange>? notify;

    /// <param name="winFspGuard">True keeps WinFsp's FINE namespace lock; false runs with the engine's own locking only.</param>
    /// <param name="trace">Logs every file system callback too.</param>
    /// <param name="ns">Another namespace than the OneDrive at the root (the fake serves every drive ID).</param>
    public static MountedDrive Start(bool winFspGuard = false, Action<FakeDriveApi>? seed = null, Func<DriveEngineOptions, DriveEngineOptions>? configure = null,
        bool trace = false, DriveNamespace? ns = null) =>
        new(winFspGuard, seed, configure, trace, ns);

    public string PathOf(string relative) => $@"{Root}\{relative.Replace('/', '\\')}";

    void Log(string line)
    {
        lock (log)
        {
            log.Add(line);
        }
    }

    public IReadOnlyList<string> Lines()
    {
        lock (log)
        {
            return [.. log];
        }
    }

    /// <summary>Error lines the file system or engine logged.</summary>
    public IReadOnlyList<string> Errors() => Lines().Where(line => line.StartsWith("error:", StringComparison.Ordinal)).ToList();

    /// <summary>Everything due now: deletes and temp files past their settle window, waiting uploads (what an unmount does).</summary>
    public Task SettleAsync() => Engine.ProcessDueAsync(all: true, default);

    /// <summary>Files and folders the volume shows below a folder, as '/'-separated paths relative to the root.</summary>
    public SortedSet<string> VolumeTree(string folder = "")
    {
        var tree = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = folder.Length == 0 ? Root : PathOf(folder);
        foreach (var path in Directory.EnumerateFileSystemEntries(start, "*", SearchOption.AllDirectories))
        {
            tree.Add(path[(Root.Length + 1)..].Replace('\\', '/'));
        }
        return tree;
    }

    /// <summary>What the fake server holds below a folder.</summary>
    public SortedSet<string> ServerTree(string folder = "") =>
        new(Fake.Paths.Where(path => folder.Length == 0 || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Unmounts now (Dispose does too; twice is harmless).</summary>
    public void Unmount() => host.Dispose();

    public void Dispose()
    {
        try
        {
            host.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
