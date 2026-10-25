using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vfs365.Tests.Mounted;

/// <summary>How applications save, as the file system sees it, through System.IO.</summary>
static class SavePatterns
{
    /// <summary>Word: ~WRD temp written, original renamed to ~WRL, temp renamed onto the original, ~WRL deleted.</summary>
    public static void Word(string path, byte[] content, int n)
    {
        var folder = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(folder, $"~WRD{n:0000}.tmp");
        var backup = Path.Combine(folder, $"~WRL{n:0000}.tmp");
        File.WriteAllBytes(temp, content);
        File.Move(path, backup);
        File.Move(temp, path);
        File.Delete(backup);
    }

    /// <summary>Excel: temp with an 8 hex digit name written, original renamed away, temp renamed onto the original, the old one deleted.</summary>
    public static void Excel(string path, byte[] content, Random random)
    {
        var folder = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(folder, $"{random.Next():X8}");
        var aside = Path.Combine(folder, $"{random.Next():X8}.tmp");
        File.WriteAllBytes(temp, content);
        File.Move(path, aside);
        File.Move(temp, path);
        File.Delete(aside);
    }

    /// <summary>Chrome: written as .crdownload in pieces, closed, renamed to its name.</summary>
    public static void ChromeDownload(string path, byte[] content)
    {
        var partial = path + ".crdownload";
        using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            for (var at = 0; at < content.Length; at += 64 * 1024)
            {
                stream.Write(content, at, Math.Min(64 * 1024, content.Length - at));
            }
        }
        File.Move(partial, path);
    }

    /// <summary>Firefox: an empty file at the name first, the content as .part, then .part renamed onto the name.</summary>
    public static void FirefoxDownload(string path, byte[] content)
    {
        File.WriteAllBytes(path, []);
        File.WriteAllBytes(path + ".part", content);
        File.Move(path + ".part", path, overwrite: true);
    }
}

/// <summary>
/// Repeats file operations on their own threads every few milliseconds and records how long each took, to find the worst wait
/// while something slow runs.
/// </summary>
sealed class Probes : IDisposable
{
    readonly record struct Sample(string Kind, long Start, long End);

    readonly List<Sample> samples = [];
    readonly List<Thread> threads = [];
    readonly List<(string Kind, Exception Error)> errors = [];
    volatile bool stopping;

    public Probes(TimeSpan interval, params (string Kind, Action Probe)[] probes)
    {
        foreach (var (kind, probe) in probes)
        {
            var thread = new Thread(() =>
            {
                while (!stopping)
                {
                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        probe();
                    }
                    catch (Exception e)
                    {
                        lock (errors)
                        {
                            errors.Add((kind, e));
                        }
                    }
                    var end = Stopwatch.GetTimestamp();
                    lock (samples)
                    {
                        samples.Add(new Sample(kind, start, end));
                    }
                    Thread.Sleep(interval);
                }
            }) { IsBackground = true, Name = $"probe {kind}" };
            threads.Add(thread);
            thread.Start();
        }
    }

    public IReadOnlyList<(string Kind, Exception Error)> Errors
    {
        get
        {
            lock (errors)
            {
                return [.. errors];
            }
        }
    }

    /// <summary>The longest probe of a kind that ran during [from, to] (Stopwatch timestamps), in milliseconds, and how many ran.</summary>
    public (double WorstMs, int Count) Worst(string kind, long from, long to)
    {
        lock (samples)
        {
            var overlapping = samples.Where(s => s.Kind == kind && s.End >= from && s.Start <= to).ToList();
            return (overlapping.Count == 0 ? 0 : overlapping.Max(s => Stopwatch.GetElapsedTime(s.Start, s.End).TotalMilliseconds), overlapping.Count);
        }
    }

    public void Dispose()
    {
        stopping = true;
        foreach (var thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(30));
        }
    }
}

/// <summary>Win32 calls System.IO doesn't offer.</summary>
static class Native
{
    const uint FileFlagBackupSemantics = 0x02000000, GenericRead = 0x80000000, OpenExisting = 3;
    const int FileIdBothDirectoryRestartInfo = 11;

    /// <summary>A handle to a folder, for listing it again and again without opening it again.</summary>
    public static SafeFileHandle OpenFolder(string path)
    {
        var handle = CreateFileW(path, GenericRead, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        return handle.IsInvalid ? throw new Win32Exception(Marshal.GetLastPInvokeError(), path) : handle;
    }

    /// <summary>Lists an open folder from its start (the first buffer of entries).</summary>
    public static void ListFromStart(SafeFileHandle folder)
    {
        var buffer = new byte[64 * 1024];
        if (!GetFileInformationByHandleEx(folder, FileIdBothDirectoryRestartInfo, buffer, buffer.Length))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, byte[] buffer, int size);
}
