using System.Globalization;
using System.Text.RegularExpressions;
using Vfs365.Core.Drive;

namespace Vfs365.Agent;

public enum Cause { Offline, Throttled, SignedOut, Locked, NoAccess, ReadOnly, Unavailable, Gone, Other }

/// <summary>Why something failed, in words a user understands, from an error message and what the agent knows about the network and sign-in.</summary>
public static partial class PlainCause
{
    /// <summary>
    /// Status codes lead ("429 TooManyRequests from ..."); then sign-in errors, the agent's own view (no network, sign-in problem), and
    /// connection failures, which .NET ends with "(host:port)" whatever the Windows language.
    /// </summary>
    public static Cause Of(string? message, bool offline = false, bool signInProblem = false)
    {
        message ??= "";
        if (StatusCode().Match(message) is { Success: true } status)
        {
            return int.Parse(status.Groups[1].Value, CultureInfo.InvariantCulture) switch
            {
                _ when message.Contains("serviceReadOnly", StringComparison.OrdinalIgnoreCase) => Cause.ReadOnly,
                429 or 503 => Cause.Throttled,
                423 => Cause.Locked,
                401 => Cause.SignedOut,
                403 => Cause.NoAccess,
                404 or 410 => Cause.Gone,
                408 or 504 => Cause.Offline,
                >= 500 => Cause.Unavailable,
                _ => Cause.Other,
            };
        }
        if (message.Contains("AADSTS", StringComparison.Ordinal) || message.Contains("sign-in", StringComparison.OrdinalIgnoreCase)
            || message.Contains("interaction_required", StringComparison.Ordinal) || message.Contains("WAM", StringComparison.Ordinal))
        {
            return Cause.SignedOut;
        }
        if (message.Contains("read-only", StringComparison.OrdinalIgnoreCase))
        {
            return Cause.ReadOnly;
        }
        if (offline || HostAndPort().IsMatch(message) || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("canceled", StringComparison.OrdinalIgnoreCase) || message.Contains("No such host", StringComparison.OrdinalIgnoreCase))
        {
            return Cause.Offline;
        }
        return signInProblem ? Cause.SignedOut : Cause.Other;
    }

    public static string Text(Cause cause, CultureInfo? culture = null) => UiText.Get(culture, $"Cause.{cause}");

    [GeneratedRegex(@"^\s*(\d{3})\s")]
    private static partial Regex StatusCode();

    [GeneratedRegex(@"\([\w.-]+:\d+\)\s*\.?\s*$")]
    private static partial Regex HostAndPort();
}

/// <summary>A sign-in problem in plain words: the common AADSTS codes and sign-in errors, else a generic "sign-in failed".</summary>
public static class SignInText
{
    static readonly (string[] Codes, string Key)[] Known =
    [
        (["AADSTS50076", "AADSTS50079", "AADSTS50074", "AADSTS50072"], "Aad.Mfa"),
        (["AADSTS50173", "AADSTS50133", "AADSTS50132", "AADSTS70008", "AADSTS700082", "AADSTS70043", "AADSTS50089"], "Aad.Expired"),
        (["AADSTS53003", "AADSTS53000", "AADSTS53001", "AADSTS53002"], "Aad.Blocked"),
        (["AADSTS65001", "AADSTS90094", "AADSTS90095", "AADSTS700016"], "Aad.Consent"),
        (["AADSTS50057"], "Aad.Disabled"),
        (["AADSTS50020", SignInException.Guest], "Aad.Guest"),
        ([SignInException.Cancelled], "Aad.Cancelled"),
    ];

    public static string Of(Exception e, CultureInfo? culture = null) => UiText.Get(culture, Key(Message(e)));

    /// <summary>Problems only an administrator (or another account) solves: trying again by itself won't help.</summary>
    public static bool Permanent(Exception e) =>
        Key(Message(e)) is "Aad.Consent" or "Aad.Guest" or "Aad.Blocked" or "Aad.Disabled" || Message(e).Contains("AADSTS50194", StringComparison.Ordinal);

    static string Message(Exception e) => e is SignInException signIn ? $"{signIn.Code} {signIn.Message}" : e.Message;

    static string Key(string message) =>
        Known.FirstOrDefault(known => known.Codes.Any(code => message.Contains(code, StringComparison.Ordinal))).Key ?? "Aad.Other";
}

public enum StartWait { Network, SignIn, Retry }

/// <summary>
/// Why the agent couldn't start, in plain words, and what it waits for: the network and a sign-in come back by themselves (tried again
/// with a growing pause, and at once when the network returns or the user signs in); anything else waits for Retry in the tray.
/// </summary>
public sealed record StartProblem(StartWait Wait, string Reason)
{
    public static StartProblem Of(Exception e, bool offline = false, CultureInfo? culture = null) => e switch
    {
        SignInException when SignInText.Permanent(e) => new(StartWait.Retry, SignInText.Of(e, culture)),
        SignInException => new(StartWait.SignIn, SignInText.Of(e, culture)),
        HttpRequestException or TaskCanceledException or TimeoutException or System.Net.Sockets.SocketException =>
            new(StartWait.Network, PlainCause.Text(Cause.Offline, culture)),
        _ when PlainCause.Of(e.Message, offline) is var cause && cause is Cause.Offline or Cause.Throttled or Cause.Unavailable =>
            new(StartWait.Network, PlainCause.Text(cause, culture)),
        _ => new(StartWait.Retry, e.Message),
    };
}

/// <summary>Plain words for a start problem that <see cref="StartProblem"/> only knows by its exception text: the drive, WinFsp, the profile's files.</summary>
public static class StartText
{
    public static string Plain(Exception e, CultureInfo? culture = null) => e switch
    {
        Vfs365.Drive.WinFsp.MountConflictException => UiText.Get(culture, "Start.Mounted"),
        DllNotFoundException or BadImageFormatException => UiText.Get(culture, "Start.WinFsp"),
        IOException when e.Message.StartsWith("WinFsp", StringComparison.Ordinal) => UiText.Get(culture, "Start.WinFsp"),
        IOException or UnauthorizedAccessException => UiText.Get(culture, "Start.Files"),
        _ => PlainCause.Text(PlainCause.Of(e.Message), culture),
    };
}

/// <summary>
/// Notifications about uploads and conflict copies, grouped: what arrives within Window shows as one toast per kind ("27 files are
/// waiting to upload: offline") instead of one per file, and a kind shows again only after Cooldown, with what arrived meanwhile, so a
/// slow trickle doesn't give a toast every few seconds. "Uploaded" toasts can be muted (policy NotifyUploads).
/// </summary>
public sealed class NoticeBatcher(bool notifyUploads = true)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(4), Cooldown = TimeSpan.FromMinutes(1);

    static readonly NoticeKind[] Kinds = [NoticeKind.ConflictCopy, NoticeKind.NotDeleted, NoticeKind.UploadWaiting, NoticeKind.UploadDone];

    readonly Lock gate = new();
    readonly List<(EngineNotice Notice, Cause Cause, DateTimeOffset At)> pending = [];
    readonly Dictionary<NoticeKind, DateTimeOffset> shown = [];

    static string NameOf(EngineNotice notice) => notice.Path ?? notice.Name;

    public void Add(EngineNotice notice, Cause cause, DateTimeOffset now)
    {
        if (notice.Kind == NoticeKind.UploadDone && !notifyUploads)
        {
            return;
        }
        lock (gate)
        {
            pending.Add((notice, cause, now));
        }
    }

    /// <summary>
    /// The toasts (title, text) for notices that arrived more than Window ago, per kind at most once per Cooldown; empty until then.
    /// The window counts from the first notice of the kinds that may show now, so a burst of one kind isn't split by another kind waiting.
    /// </summary>
    public IReadOnlyList<(string Title, string Text)> Due(DateTimeOffset now, CultureInfo? culture = null)
    {
        var batches = new List<(NoticeKind Kind, List<(EngineNotice Notice, Cause Cause)> Items)>();
        lock (gate)
        {
            // A file that uploaded after it was waiting needs no "waiting" toast
            var done = pending.Where(n => n.Notice.Kind == NoticeKind.UploadDone).GroupBy(n => NameOf(n.Notice), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(n => n.At), StringComparer.OrdinalIgnoreCase);
            pending.RemoveAll(n => n.Notice.Kind == NoticeKind.UploadWaiting && done.TryGetValue(NameOf(n.Notice), out var at) && at >= n.At);
            var ready = Kinds.Where(kind => !(shown.TryGetValue(kind, out var last) && now - last < Cooldown) && pending.Any(n => n.Notice.Kind == kind)).ToList();
            if (ready.Count == 0 || now - pending.Where(n => ready.Contains(n.Notice.Kind)).Min(n => n.At) < Window)
            {
                return [];
            }
            foreach (var kind in ready)
            {
                batches.Add((kind, pending.Where(n => n.Notice.Kind == kind).Select(n => (n.Notice, n.Cause)).ToList()));
                pending.RemoveAll(n => n.Notice.Kind == kind);
                shown[kind] = now;
            }
        }
        var toasts = new List<(string, string)>();
        foreach (var (kind, batch) in batches)
        {
            var items = batch.DistinctBy(n => NameOf(n.Notice), StringComparer.OrdinalIgnoreCase).ToList();
            var first = items[0].Notice;
            var cause = PlainCause.Text(items.GroupBy(n => n.Cause).OrderByDescending(g => g.Count()).First().Key, culture);
            toasts.Add(kind switch
            {
                NoticeKind.ConflictCopy => (UiText.Get(culture, "Copy.Title"),
                    items.Count == 1 ? UiText.Get(culture, "Copy.One", first.Name, first.Detail) : UiText.Get(culture, "Copy.Many", items.Count)),
                NoticeKind.NotDeleted => (UiText.Get(culture, "NotDeleted.Title"),
                    items.Count == 1 ? UiText.Get(culture, "NotDeleted.One", first.Name, cause) : UiText.Get(culture, "NotDeleted.Many", items.Count)),
                NoticeKind.UploadWaiting => (UiText.Get(culture, "Waiting.Title"),
                    items.Count == 1 ? UiText.Get(culture, "Waiting.One", first.Name, cause) : UiText.Get(culture, "Waiting.Many", items.Count, cause)),
                _ => (UiText.Get(culture, "Uploaded.Title"),
                    items.Count == 1 ? UiText.Get(culture, "Uploaded.One", first.Name) : UiText.Get(culture, "Uploaded.Many", items.Count)),
            });
        }
        return toasts;
    }
}
