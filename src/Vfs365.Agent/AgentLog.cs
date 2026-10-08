using System.Text;

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
    StreamWriter writer;
    long written;

    AgentLog(string folder, long maxBytes, Func<DateTime> clock)
    {
        (this.folder, this.maxBytes, this.clock) = (folder, maxBytes, clock);
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
        if (written >= maxBytes && value.EndsWith('\n'))
        {
            Roll();
        }
    }

    public override void WriteLine(string? value) => Write(value + NewLine);

    public override void Flush() => writer.Flush();

    /// <summary>The file is full: it moves to logs\ and the session goes on in a new one.</summary>
    void Roll()
    {
        writer.WriteLine($"{clock():yyyy-MM-dd HH:mm:ss} Log reached {maxBytes >> 20} MB; it continues in agent.log");
        writer.Dispose();
        var archived = Archive(folder);
        Prune(folder, clock());
        writer = OpenFile(out written);
        writer.WriteLine($"{clock():yyyy-MM-dd HH:mm:ss} Log continues from logs\\{Path.GetFileName(archived)}");
    }

    StreamWriter OpenFile(out long length)
    {
        var stream = new FileStream(Path.Combine(folder, "agent.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        length = stream.Length;
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
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
        var logs = Directory.CreateDirectory(Path.Combine(folder, "logs")).FullName;
        var target = Path.Combine(logs, $"agent-{info.LastWriteTime:yyyyMMdd-HHmmss}.log");
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(logs, $"agent-{info.LastWriteTime:yyyyMMdd-HHmmss}-{n}.log");
        }
        try
        {
            File.Move(current, target);
            return target;
        }
        catch (IOException)
        {
            return null; // another agent of this user still writes it (a restart in progress)
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
