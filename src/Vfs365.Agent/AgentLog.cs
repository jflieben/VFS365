using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Vfs365.Agent;

/// <summary>
/// Log of the background agent. %LOCALAPPDATA%\VFS365\agent.log holds the current sign-in: a new sign-in moves the previous one to
/// logs\agent-&lt;time of its last line&gt;.log, and a file that reaches MaxBytes continues in a new one the same way. Logs older
/// than KeepDays go, and at most MaxFiles are kept.
/// </summary>
public sealed class AgentLog : TextWriter
{
    public const long MaxBytes = 10L << 20;
    public const int KeepDays = 7, MaxFiles = 50;

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VFS365");
    public static string FilePath { get; } = Path.Combine(Folder, "agent.log");

    readonly string folder;
    readonly long maxBytes;
    readonly Func<DateTime> clock;
    AppendFile writer;
    long written, rollAt;

    AgentLog(string folder, long maxBytes, Func<DateTime> clock)
    {
        (this.folder, this.maxBytes, this.clock, rollAt) = (folder, maxBytes, clock, maxBytes);
        writer = OpenFile(out written);
    }

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>The agent's log; <paramref name="newSession"/> (a sign-in, not a restart from the tray) starts a new file.</summary>
    public static TextWriter Open(bool newSession) => TextWriter.Synchronized(Open(Folder, newSession, MaxBytes, () => DateTime.Now));

    public static AgentLog Open(string folder, bool newSession, long maxBytes, Func<DateTime> clock)
    {
        Directory.CreateDirectory(folder);
        if (newSession)
        {
            Archive(folder);
        }
        Prune(folder, clock());
        return new AgentLog(folder, maxBytes, clock);
    }

    public override void Write(char value) => Write(value.ToString());

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        writer.Write(value);
        written += Encoding.UTF8.GetByteCount(value);
        if (written >= rollAt && value.EndsWith('\n'))
        {
            Roll();
        }
    }

    public override void WriteLine(string? value) => Write(value + NewLine);

    public override void Flush()
    {
    }

    /// <summary>
    /// The file is full: it moves to logs\ and the session goes on in a new one. When it can't be moved (another program holds it open
    /// without delete sharing) it goes on in the same file and the next try waits for another half of MaxBytes.
    /// </summary>
    void Roll()
    {
        writer.WriteLine($"{clock():yyyy-MM-dd HH:mm:ss} Log reached {maxBytes >> 20} MB; it continues in agent.log");
        writer.Dispose();
        var archived = Archive(folder);
        Prune(folder, clock());
        writer = OpenFile(out written);
        if (archived is null)
        {
            rollAt = written + Math.Max(1, maxBytes / 2);
            writer.WriteLine($"{clock():yyyy-MM-dd HH:mm:ss} Log could not be moved to logs\\ (another program has it open); it goes on in this file");
            return;
        }
        rollAt = maxBytes;
        writer.WriteLine($"{clock():yyyy-MM-dd HH:mm:ss} Log continues from logs\\{Path.GetFileName(archived)}");
    }

    AppendFile OpenFile(out long length)
    {
        var file = new AppendFile(Path.Combine(folder, "agent.log"));
        length = file.Length;
        return file;
    }

    /// <summary>
    /// agent.log opened for appending only (FILE_APPEND_DATA without FILE_WRITE_DATA): Windows puts every write at the end of the file,
    /// so the supervisor, the agent and an agent being replaced by a restart can all write without overwriting each other's lines.
    /// One write per line.
    /// </summary>
    sealed class AppendFile : IDisposable
    {
        const uint FileAppendData = 0x4, Synchronize = 0x100000, ShareAll = 7, OpenAlways = 4;
        readonly SafeFileHandle handle;

        public AppendFile(string path)
        {
            handle = CreateFile(path, FileAppendData | Synchronize, ShareAll, IntPtr.Zero, OpenAlways, 0x80, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new IOException($"Can't open {path} (error {Marshal.GetLastPInvokeError()})");
            }
        }

        public long Length => GetFileSizeEx(handle, out var size) ? size : 0;

        public void Write(string text)
        {
            var bytes = Utf8.GetBytes(text);
            WriteFile(handle, bytes, bytes.Length, out _, IntPtr.Zero);
        }

        public void WriteLine(string text) => Write(text + Environment.NewLine);

        public void Dispose() => handle.Dispose();

        static readonly UTF8Encoding Utf8 = new(false);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        static extern bool GetFileSizeEx(SafeFileHandle file, out long size);
    }

    /// <summary>Moves agent.log, if it has anything, to logs\ under the time of its last line. Returns where it went.</summary>
    static string? Archive(string folder)
    {
        var current = Path.Combine(folder, "agent.log");
        var info = new FileInfo(current);
        if (!info.Exists || info.Length == 0)
        {
            return null;
        }
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(folder, "logs")).FullName;
            var target = Path.Combine(logs, $"agent-{info.LastWriteTime:yyyyMMdd-HHmmss}.log");
            for (var n = 2; File.Exists(target); n++)
            {
                target = Path.Combine(logs, $"agent-{info.LastWriteTime:yyyyMMdd-HHmmss}-{n}.log");
            }
            File.Move(current, target);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // another agent of this user still writes it (a restart in progress), or another program holds it open
        }
    }

    /// <summary>Removes logs older than KeepDays and all but the newest MaxFiles, and the agent.log.old of earlier versions.</summary>
    static void Prune(string folder, DateTime now)
    {
        TryDelete(Path.Combine(folder, "agent.log.old"));
        var logs = Path.Combine(folder, "logs");
        if (!Directory.Exists(logs))
        {
            return;
        }
        var files = new DirectoryInfo(logs).GetFiles("agent-*.log").OrderByDescending(f => f.LastWriteTime).ToList();
        foreach (var old in files.Where((file, index) => index >= MaxFiles || now - file.LastWriteTime > TimeSpan.FromDays(KeepDays)))
        {
            TryDelete(old.FullName);
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            writer.Dispose();
        }
        base.Dispose(disposing);
    }
}
