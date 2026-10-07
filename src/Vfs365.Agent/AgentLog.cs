namespace Vfs365.Agent;

/// <summary>Output of the background agent: %LOCALAPPDATA%\VFS365\agent.log, moved to agent.log.old above 5 MB.</summary>
public static class AgentLog
{
    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VFS365", "agent.log");

    public static TextWriter Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 5 * 1024 * 1024)
        {
            File.Move(FilePath, FilePath + ".old", overwrite: true);
        }
        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return TextWriter.Synchronized(new StreamWriter(stream) { AutoFlush = true });
    }
}
