using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Vfs365.Agent;

/// <summary>
/// How Explorer's VFS365 menu reaches the agent: "vfs365-agent.exe verb &lt;name&gt; &lt;path&gt;" sends one line ("name\tpath\n") over a
/// named pipe of the user's session and gets "ok" or why not back. The pipe is the user's only (CurrentUserOnly on both ends: the
/// agent's pipe admits only its user, and the client talks only to a pipe its user owns), requests are limited in size and time.
/// </summary>
public static class VerbPipe
{
    /// <summary>A request is at most this long: a verb and a path of up to 32,767 characters in UTF-8.</summary>
    public const int MaxRequest = 128 * 1024;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    public static string Name(int sessionId, string userSid) => $"VFS365.Verbs.{sessionId}.{userSid}";

    public static string CurrentName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return Name(Process.GetCurrentProcess().SessionId, identity.User!.Value);
    }

    /// <summary>
    /// Answers requests one at a time until <paramref name="ct"/> ends. <paramref name="accept"/> gets the verb and path and returns null
    /// when it takes the request (and does the work after answering), else why not.
    /// </summary>
    public static async Task ServeAsync(string name, Func<string, string, string?> accept, Action<string> log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"Explorer menu: no pipe ({e.Message}), another agent of this user may have it; trying again in a minute");
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                continue;
            }
            await using (server)
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                try
                {
                    var line = await ReadLineAsync(server, timeout.Token).ConfigureAwait(false);
                    var split = line?.IndexOf('\t') ?? -1;
                    var answer = split <= 0 ? "malformed request" : accept(line![..split], line[(split + 1)..]);
                    await server.WriteAsync(Encoding.UTF8.GetBytes((answer ?? "ok") + "\n"), timeout.Token).ConfigureAwait(false);
                    server.WaitForPipeDrain();
                }
                catch (Exception e) when (e is IOException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    log($"Explorer menu: a request failed: {e.Message}");
                }
            }
        }
    }

    /// <summary>Sends a request; true when the agent took it. <paramref name="answer"/>: why not, or that no agent answered (null).</summary>
    public static bool Send(string name, string verb, string path, TimeSpan timeout, out string? answer)
    {
        answer = null;
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeout);
            // The agent opens the browser or the file: it may take the foreground, which Explorer's click gave this process
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var agent))
            {
                AllowSetForegroundWindow(agent);
            }
            client.Write(Encoding.UTF8.GetBytes($"{verb}\t{path}\n"));
            client.Flush();
            using var cts = new CancellationTokenSource(timeout);
            answer = ReadLineAsync(client, cts.Token).GetAwaiter().GetResult();
            return answer == "ok";
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>One line, without its "\n"; null when the other end closed first. Throws when it is longer than MaxRequest.</summary>
    static async Task<string?> ReadLineAsync(PipeStream pipe, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var line = new MemoryStream();
        while (true)
        {
            var read = await pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }
            var end = Array.IndexOf(buffer, (byte)'\n', 0, read);
            line.Write(buffer, 0, end >= 0 ? end : read);
            if (line.Length > MaxRequest)
            {
                throw new IOException("request too long");
            }
            if (end >= 0)
            {
                return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            }
        }
    }

    /// <summary>
    /// "vfs365-agent.exe verb &lt;name&gt; &lt;path&gt;", run by Explorer: hands the request to this session's agent and ends. Shows a short
    /// message when no agent answers or it refuses. No window otherwise, no log.
    /// </summary>
    public static int RunClient(string[] args)
    {
        if (args.Length != 3 || !ExplorerVerbs.IsVerb(args[1]))
        {
            return 2;
        }
        if (Send(CurrentName(), args[1], PathArgument(args[2]), RequestTimeout, out var answer))
        {
            return 0;
        }
        MessageBoxW(IntPtr.Zero, answer is null ? UiText.Get("Verb.NotRunning") : answer, UiText.Get("Verb.Menu"), 0x40 | 0x10000); // MB_ICONINFORMATION | MB_SETFOREGROUND
        return 1;
    }

    /// <summary>
    /// A path argument as Explorer meant it: "%1" of a drive's top ("M:\") arrives as M:" (the backslash escapes the closing quote),
    /// which is M:\ again. Quotes can't occur in Windows paths.
    /// </summary>
    internal static string PathArgument(string argument) => argument.EndsWith('"') ? argument[..^1].TrimEnd('\\') + @"\" : argument;

    /// <summary>A verb's result or problem as a notification; without a tray icon (policy TrayIcon 0) only problems show, as a message.</summary>
    internal static void Tell(Tray? tray, string title, string text, bool problem)
    {
        if (tray is not null)
        {
            tray.Notify(title, text);
        }
        else if (problem)
        {
            MessageBoxW(IntPtr.Zero, text, title, 0x40 | 0x10000);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
