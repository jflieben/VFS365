using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Vfs365.Core.Discovery;

namespace Vfs365.Agent;

/// <summary>
/// `vfs365 support-bundle` and the tray's Collect diagnostics: one zip with what a helpdesk asks for first, by default in
/// %LOCALAPPDATA%\VFS365\support (never on the drive or in a folder redirected into it). Nothing is uploaded. Every text goes through
/// the <see cref="Redactor"/>; each part is best effort, and one that fails holds a short note instead. Never read: cached content,
/// metadata.bin, cache.key, staging content and token caches (WAM keeps those outside this folder anyway).
/// </summary>
public sealed class SupportBundle(string dataFolder, Func<AgentSettings> settings)
{
    public const string FolderName = "support", Prefix = "VFS365-support-";
    public const int Keep = 5, PreviousLogs = 2;

    public static string DefaultFolder { get; } = Path.Combine(DiscoveryStore.DataDirectory, FolderName);

    /// <summary>The default folder, or %TEMP%\VFS365\support when %LOCALAPPDATA% itself is redirected onto the drive.</summary>
    public static string LocalFolder() => Refusal(DefaultFolder) is null ? DefaultFolder : Path.Combine(Path.GetTempPath(), "VFS365", FolderName);

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>The health check for the bundle; by default the current user's, from the bundle's status file.</summary>
    public Func<HealthReport>? HealthCheck { get; init; }

    static readonly UTF8Encoding Utf8 = new(false);
    static int collecting;

    /// <summary>Why <paramref name="folder"/> can't take a bundle (it is on the VFS365 drive), or null.</summary>
    public static string? Refusal(string folder, string? driveLetter = null)
    {
        var full = Path.GetFullPath(folder);
        if (full.StartsWith(@"\\VFS365\", StringComparison.OrdinalIgnoreCase) || full.TrimEnd('\\').Equals(@"\\VFS365", StringComparison.OrdinalIgnoreCase))
        {
            return $"{full} is on the VFS365 drive; a support bundle stays on this PC. Choose a local folder.";
        }
        if (full.Length >= 2 && full[1] == ':' && char.IsAsciiLetter(full[0]))
        {
            var letter = full[..2].ToUpperInvariant();
            var target = DosDevices.Target(letter);
            if (letter.Equals(driveLetter, StringComparison.OrdinalIgnoreCase)
                || target is not null && (target.Contains(@"\VFS365\", StringComparison.OrdinalIgnoreCase) || target.Contains("WinFsp", StringComparison.OrdinalIgnoreCase)))
            {
                return $"{letter} is a VFS365 drive letter; a support bundle stays on this PC. Choose a local folder.";
            }
        }
        return null;
    }

    /// <summary>Writes the zip into <paramref name="folder"/> and returns its path.</summary>
    public string Create(string folder)
    {
        var now = Clock();
        Directory.CreateDirectory(folder);
        var name = $"{Prefix}{Safe(Environment.MachineName)}-{Safe(Environment.UserName)}-{now:yyyyMMdd-HHmmss}.zip";
        var path = Path.Combine(folder, name);
        var partial = path + ".partial";
        using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var (entry, build) in Parts(now))
            {
                string text;
                try
                {
                    text = build();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    text = $"Could not collect {entry}: {e.GetType().Name}: {e.Message}";
                }
                using var writer = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Optimal).Open(), Utf8);
                writer.Write(Redactor.Redact(text));
            }
        }
        File.Move(partial, path, overwrite: true);
        if (Path.GetFullPath(folder).TrimEnd('\\').Equals(Path.GetFullPath(DefaultFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Prune(folder);
        }
        return path;
    }

    /// <summary>Explorer with the zip selected.</summary>
    public static void Show(string zip)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>The tray's Collect diagnostics: in the background, one at a time; a failure is logged and shown.</summary>
    public static void CollectFromTray(string? configPath, Action<string> log, Action<string, string>? notify)
    {
        if (Interlocked.Exchange(ref collecting, 1) == 1)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var zip = new SupportBundle(DiscoveryStore.DataDirectory, () => AgentSettings.Load(configPath)).Create(LocalFolder());
                log($"Diagnostics: collected in {zip} (from the tray)");
                Show(zip);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log($"Diagnostics: could not be collected: {e.Message}");
                notify?.Invoke(UiText.Get("Diagnostics.Title"), UiText.Get("Diagnostics.Failed", e.Message));
            }
            finally
            {
                Volatile.Write(ref collecting, 0);
            }
        });
    }

    IEnumerable<(string Name, Func<string> Build)> Parts(DateTimeOffset now)
    {
        var status = new Lazy<AgentStatus?>(() => StatusFile.Read(Data(StatusFile.FileName)));
        var loaded = new Lazy<AgentSettings>(settings);
        var health = new Lazy<HealthReport>(() => HealthCheck?.Invoke() ?? Health.CurrentUser(now.ToUniversalTime(), Data(StatusFile.FileName)));
        yield return ("README.txt", () => Readme(now));
        yield return ("summary.txt", () => Summary(now, health.Value));
        yield return ("health.json", () => health.Value.ToJson());
        yield return ("settings.txt", () => Lines(SettingsReport.Lines(loaded.Value, TenantResolver.Resolve(loaded.Value, DeviceJoin.Read()), UserPinsOf())));
        yield return ("tenant.txt", () => TenantText(loaded.Value, status.Value));
        yield return ("discovery.txt", DiscoveryText);
        yield return ("drive.txt", () => DriveText(loaded.Value, status.Value));
        yield return ("waiting.txt", () => WaitingText(status.Value));
        yield return ("status.json", () => ReadShared(Data(StatusFile.FileName)) ?? "No status file (the agent didn't run in this profile yet).");
        yield return ("redirection.json", () => ReadShared(Data(FolderRedirection.StateFileName)) ?? "No redirection.json: no folders are redirected now.");
        foreach (var log in Logs())
        {
            yield return ($"logs/{Path.GetFileName(log)}", () => ReadShared(log) ?? "");
        }
    }

    string Data(string name) => Path.Combine(dataFolder, name);

    /// <summary>agent.log and the newest earlier logs.</summary>
    IEnumerable<string> Logs()
    {
        var current = Data("agent.log");
        var earlier = Directory.Exists(Data("logs"))
            ? new DirectoryInfo(Data("logs")).GetFiles("agent-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(PreviousLogs).Select(f => f.FullName)
            : [];
        return File.Exists(current) ? earlier.Prepend(current) : earlier;
    }

    static string Readme(DateTimeOffset now) => $"""
        VFS365 support bundle, made {now:yyyy-MM-dd HH:mm:ss zzz} by VFS365 {Version}.

        summary.txt       version, Windows, the user and the health check (health.json has it as JSON)
        settings.txt      the settings in effect, where each comes from, and warnings ('vfs365 settings')
        tenant.txt        how the tenant was chosen, and the account last signed in
        discovery.txt     the last discovery: libraries shown, skipped (with the reason), errors and hidden libraries; what others shared
                          (kinds and IDs, no names)
        drive.txt         drive letter, DriveMappings letters, network provider order, WinFsp
        waiting.txt       changes waiting to upload, with the cause, and changes kept on this PC
        status.json       what the running agent reported last
        redirection.json  folders FolderRedirection moved, while the drive is mounted
        logs\             agent.log (this sign-in) and the {PreviousLogs} newest earlier logs

        Tokens and secrets (tokens, Authorization values, SAS signatures, download links, passwords, keys) are removed from every file.
        Never included: copies of files, folder listings (metadata.bin), the cache key, the content of unsaved changes, token caches.
        Nothing is uploaded: send this file to your helpdesk yourself.
        """;

    static string Version => typeof(SupportBundle).Assembly.GetName().Version!.ToString(3);

    static string Summary(DateTimeOffset now, HealthReport health)
    {
        using var process = Process.GetCurrentProcess();
        return Lines(
        [
            AgentControl.Publisher,
            AgentControl.Banner,
            $"Made:       {now:yyyy-MM-dd HH:mm:ss zzz} ({now.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC)",
            $"Program:    {Environment.ProcessPath}",
            $"Windows:    {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version}), {RuntimeInformation.OSArchitecture}; this process {RuntimeInformation.ProcessArchitecture}",
            $".NET:       {RuntimeInformation.FrameworkDescription}",
            $"Computer:   {Environment.MachineName}",
            $"User:       {Environment.UserDomainName}\\{Environment.UserName}, session {process.SessionId}",
            $"Language:   {CultureInfo.CurrentUICulture.Name} (display), {CultureInfo.CurrentCulture.Name} (formats)",
            "",
            .. health.ToText(),
        ]);
    }

    IReadOnlyList<string> UserPinsOf()
    {
        var file = Data("pinned.json");
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (JsonException)
        {
            return ["(pinned.json can't be read)"];
        }
    }

    string TenantText(AgentSettings current, AgentStatus? status)
    {
        var join = DeviceJoin.Read();
        var tenant = TenantResolver.Resolve(current, join);
        var state = DiscoveryStore.Load(Data("discovery.json"));
        return Lines(
        [
            $"Tenant:     {tenant.Tenant} ({tenant.Source})",
            $"Device:     {(join is null ? "not joined or registered to Entra" : $"{join.Kind} to {join.TenantName ?? join.TenantId} ({join.TenantId}), device {join.DeviceId}")}",
            $"Signed in:  {(status?.User is { } user ? $"{user} (tenant {status.TenantId}), as of {status.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" : "unknown (no status file with an account)")}",
            $"Sign-in:    {(status?.SignInProblem is { } code ? $"needed ({code})" : "no problem reported")}",
            $"State:      {(state is null ? "no discovery state" : $"tenant {state.TenantId ?? "?"} ({state.TenantHint}), SharePoint {state.TenantRoot}, OneDrive {state.OneDrive?.WebUrl ?? "none"}")}",
        ]);
    }

    string DiscoveryText()
    {
        var state = DiscoveryStore.Load(Data("discovery.json"));
        var report = DiscoveryReport.Load(Data(DiscoveryReport.FileName));
        if (state is null && report is null)
        {
            return "No discovery state or report in this profile.";
        }
        var lines = new List<string>();
        if (state is not null)
        {
            lines.Add($"Last run:   {(state.RunAt == DateTimeOffset.MinValue ? "none completed" : state.RunAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}, " +
                $"SharePoint {state.TenantRoot}, rules version {state.RulesVersion}");
            lines.Add($"Shown:      {state.Libraries.Count} libraries");
            foreach (var library in state.Libraries)
            {
                var missed = state.Misses?.GetValueOrDefault(library.Key) is > 0 and var misses ? $", missed by search {misses}x" : "";
                lines.Add($"  {library.SiteTitle} / {library.LibraryTitle} ({library.WebUrl}){(library.ReadOnly ? ", read-only" : "")}, {library.SiteTemplate ?? "template ?"}, " +
                    $"{library.ItemCount:N0} items{missed}{(library.DriveId is null ? "" : ", drive known")}");
            }
            foreach (var (url, keys) in state.Pinned ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                lines.Add($"Pinned:     {url}: {(state.EmptyPins?.GetValueOrDefault(url) is { } empty ? empty.Reason : $"{keys.Count} library(ies)")}");
            }
            // Shared with me: kinds and IDs only, no names ('vfs365 shared' shows them to the user)
            if (state.Shared is { } shared)
            {
                lines.Add($"Shared:     {shared.Items.Count} item(s) other people shared, read {(shared.RunAt == DateTimeOffset.MinValue ? "never" : shared.RunAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}");
                lines.AddRange(shared.Items.Select(i => $"  {(i.IsFolder ? "folder" : "file")}{(i.ReadOnly ? ", view only" : "")}{(i.Path is null ? ", place unknown" : "")}" +
                    $"{(i.Misses > 0 ? $", missed by search {i.Misses}x" : "")}, drive {i.DriveId}, item {i.ItemId}"));
            }
        }
        if (report is not null)
        {
            lines.Add($"Report:     {report.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}, {report.SearchHits} search hits{(report.SearchError is null ? "" : $", search failed: {report.SearchError}")}" +
                $"{(report.SearchNote is null ? "" : $", {report.SearchNote}")}{(report.HidingSkippedReason is null ? "" : $", nothing hidden: {report.HidingSkippedReason}")}");
            lines.Add($"Skipped:    {report.Skipped.Count}");
            foreach (var group in report.Skipped.GroupBy(s => s.Reason).OrderByDescending(g => g.Count()))
            {
                lines.Add($"  {group.Count(),5}  {group.Key}");
            }
            lines.AddRange(report.Skipped.Take(500).Select(s => $"  {s.WebUrl} / {s.Title}: {s.Reason}"));
            lines.Add($"Errors:     {report.Errors.Count}");
            lines.AddRange(report.Errors.Take(500).Select(s => $"  {s.WebUrl} / {s.Title}: {s.Reason}"));
            lines.Add($"Hidden:     {report.Hidden.Count}");
            lines.AddRange(report.Hidden.Take(500).Select(h => $"  {h.WebUrl} / {h.Title} since {h.Since.ToLocalTime():yyyy-MM-dd}: {h.Reason}"));
        }
        return Lines(lines);
    }

    static string DriveText(AgentSettings current, AgentStatus? status)
    {
        var unc = status?.Unc ?? $@"\{AgentCommands.UncPrefix(Environment.UserName)}";
        var lines = new List<string>
        {
            $"DriveLetter: {current.DriveLetter} (setting); {(status?.DriveLetter is { } letter ? $"mounted at {letter} and {unc}" : status?.IsMounted == true ? $"mounted at {unc} only" : $"not mounted ({status?.State ?? "no status"})")}",
        };
        if (status?.DriveLetter is { } mounted)
        {
            var report = DriveLetterReport.Read(mounted);
            lines.Add($"  {mounted} DOS device {report.Target ?? "none"}, WNetGetConnection {(report.ConnectionError == 0 ? report.RemoteName : $"error {report.ConnectionError}")}" +
                $"{(report.Remembered is null ? "" : $", remembered as {report.Remembered}")}");
            lines.AddRange(report.Problems(unc).Select(p => $"  Problem: {p}"));
        }
        var problems = new List<string>();
        var mappings = DriveMapping.Parse(current.DriveMappings, DriveLetterOption.Parse(current.DriveLetter).MountPoint, problems);
        lines.Add($"DriveMappings: {(mappings.Count == 0 ? "none" : $"{mappings.Count}")}");
        foreach (var mapping in mappings)
        {
            var target = DosDevices.Target(mapping.Letter);
            lines.Add($"  {mapping.Letter} = {mapping.Target}{(mapping.Label is null ? "" : $" ({mapping.Label})")}: " +
                (target is null ? "not defined" : SessionDriveLetters.Ours(target, unc) ? $"defined, {target}" : $"held by something else: {target}"));
        }
        lines.AddRange(problems.Select(p => $"  Problem: {p}"));
        lines.Add($"FolderRedirection: {(current.FolderRedirection.Count == 0 ? "none" : string.Join("; ", current.FolderRedirection))} (redirection.json has what moved)");
        var machine = MachineHealth.Read();
        lines.Add($"Providers:  {string.Join(",", machine.ProviderOrder)}");
        lines.AddRange(machine.Problems().Select(p => $"  Problem: {p}"));
        lines.Add($"WinFsp:     {machine.WinFspVersion ?? "not installed"}");
        lines.Add($"Events:     source {WindowsEventSink.Source} {(machine.EventSource ? "registered" : "not registered")}");
        return Lines(lines);
    }

    /// <summary>What waits, with the cause the agent last saw, and the journaled changes in staging (their records only, never content).</summary>
    string WaitingText(AgentStatus? status)
    {
        var lines = new List<string>
        {
            $"Waiting:    {status?.WaitingUploads.ToString(CultureInfo.InvariantCulture) ?? "?"} change(s) waiting to upload, {status?.HeldChanges.ToString(CultureInfo.InvariantCulture) ?? "?"} " +
            "not queued (still open, held under a temp name, or refused by SharePoint)",
        };
        lines.AddRange((status?.WaitingFiles ?? []).Select(w => $"  {w.Path}: {w.Cause} (since {w.Since.ToLocalTime():yyyy-MM-dd HH:mm:ss})"));
        var staging = Data("staging");
        var journals = Directory.Exists(staging) ? Directory.GetFiles(staging, "*.staging.json") : [];
        lines.Add($"Journaled:  {journals.Length} unsaved change(s) kept in staging until they upload");
        foreach (var journal in journals.Take(500))
        {
            try
            {
                using var record = JsonDocument.Parse(File.ReadAllText(journal));
                var root = record.RootElement;
                string? Text(string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var flushed = !root.TryGetProperty("Flushed", out var flag) || flag.ValueKind != JsonValueKind.False;
                var stagingFile = new FileInfo(journal[..^".json".Length]);
                lines.Add($"  {Text("DrivePath")}: changed {Text("Modified")}, {(stagingFile.Exists ? $"{stagingFile.Length:N0} bytes" : "no staging file")}" +
                    $"{(flushed ? "" : ", still being written when the agent stopped")}{(Text("OriginalPath") is { } original ? $", renamed away from {original}" : "")}" +
                    $"{(root.TryGetProperty("Base", out var item) && item.ValueKind == JsonValueKind.Object ? "" : ", a new file")}");
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                lines.Add($"  {Path.GetFileName(journal)}: can't be read ({e.Message})");
            }
        }
        return Lines(lines);
    }

    /// <summary>A file another process may be writing (agent.log); null when it is missing.</summary>
    static string? ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static void Prune(string folder)
    {
        foreach (var old in new DirectoryInfo(folder).GetFiles($"{Prefix}*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Keep))
        {
            try
            {
                old.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    static string Safe(string name) => string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    static string Lines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
}

/// <summary>The effective settings as `vfs365 settings` prints them, with where each comes from and the warnings.</summary>
static class SettingsReport
{
    public static IEnumerable<string> Lines(AgentSettings settings, TenantChoice tenant, IReadOnlyList<string> userPins)
    {
        yield return $"{"Tenant",-21} {tenant.Tenant} ({tenant.Source})";
        foreach (var (name, value, source) in settings.Effective())
        {
            yield return $"{name,-21} {value} ({source})";
        }
        foreach (var adjusted in settings.Adjusted())
        {
            yield return $"{"Warning",-21} {adjusted}";
        }
        yield return $"{"User pins",-21} {(userPins.Count == 0 ? "none" : string.Join("; ", userPins))} (vfs365 pin)";
    }
}
