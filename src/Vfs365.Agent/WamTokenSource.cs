using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>A sign-in problem. Code is an AADSTS code or a name such as <see cref="Cancelled"/>; NeedsUser when only a sign-in window solves it.</summary>
public class SignInException(string code, string message, bool needsUser = false) : Exception(message)
{
    public const string Cancelled = "authentication_canceled";
    public const string Guest = "guest_account";
    public const string Unavailable = "wam_unavailable";
    public const string InteractionRequired = "interaction_required";

    public string Code { get; } = code;

    public bool NeedsUser { get; } = needsUser;
}

/// <summary>Who a token was issued to.</summary>
public sealed record SignInResult(string Username, string? TenantId, string? ObjectId);

/// <summary>
/// Tokens from Windows' Web Account Manager: silent with the Windows account. A sign-in window appears only from SignInAsync (the tray's
/// Sign in, or the start of the agent) or, with PromptWhenNeeded (the console tool), from a request; never while a lock is held. Requests
/// that need a sign-in otherwise fail at once and set SignInProblem, so file system and background work never wait for a user. Cached
/// tokens are handed out without waiting for anything. Requests for a resource share one silent request, which gives up after
/// RequestTimeout. A failure is remembered per resource and thrown again at once meanwhile: a network failure (or WAM not answering) for
/// NetworkRetry; one that needs the user for SilentRetry; anything else (Conditional Access, a disabled account, WAM's own errors) with
/// a back-off from FailureRetry up to MaxFailureRetry, and like the ones that need the user it is a SignInProblem (log, monitoring, tray).
/// </summary>
sealed class WamTokenSource : ITokenSource
{
    /// <summary>After a sign-in window that opened by itself failed, or any that was cancelled, none opens by itself for this long (Sign in still works).</summary>
    public static readonly TimeSpan PromptCooldown = TimeSpan.FromMinutes(15);

    /// <summary>After a silent sign-in found it needs the user, requests fail at once for this long before trying silently again.</summary>
    internal static readonly TimeSpan SilentRetry = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan NetworkRetry = TimeSpan.FromSeconds(10), FailureRetry = TimeSpan.FromMinutes(1), MaxFailureRetry = TimeSpan.FromMinutes(15);

    /// <summary>AADSTS codes that a sign-in window can solve, when WAM reports them as a provider error.</summary>
    static readonly string[] NeedUser = ["AADSTS50076", "AADSTS50079", "AADSTS50058", "AADSTS50158", "AADSTS50173", "AADSTS50133", "AADSTS65001", "AADSTS700082", "AADSTS70043", "AADSTS50089"];

    /// <summary>A failed request, thrown again until <see cref="Until"/>; <see cref="Wait"/> grows for repeated failures of the same kind.</summary>
    sealed record Failure(Exception Error, DateTimeOffset Until, TimeSpan Wait);

    readonly string clientId, tenant;
    readonly Func<CancellationToken, Task<IWamBroker>> connect;
    readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresOn)> tokens = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Lazy<Task<string>>> inflight = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Failure> failures = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (SignInException Error, string? Claims)> problems = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim interactiveGate = new(1, 1), clientGate = new(1, 1);
    readonly Lock accountGate = new();
    IWamBroker? wam;
    WamAccount? account;
    int generation; // a new account (switch, sign-out): results of requests started before don't count
    volatile string? problemText;
    volatile string? problemResource;
    DateTimeOffset promptAgainAt;

    public WamTokenSource(string clientId, string tenant) : this(clientId, tenant, async ct => await WamClient.CreateAsync(ct))
    {
    }

    internal WamTokenSource(string clientId, string tenant, Func<CancellationToken, Task<IWamBroker>> connect) =>
        (this.clientId, this.tenant, this.connect) = (clientId, tenant, connect);

    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>How long a silent request may take before it counts as failed (WAM or its provider hanging).</summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public SignInResult? LastResult { get; private set; }

    /// <summary>A request that needs a sign-in may open the window itself (the console tool runs in the foreground).</summary>
    public bool PromptWhenNeeded { get; set; }

    /// <summary>Sign-in failed or is needed (prompt cancelled, Conditional Access, no consent, password changed), for a notification. Once per problem.</summary>
    public Action<SignInException>? SignInFailed { get; set; }

    /// <summary>The first token, and each time the account changes: for the log.</summary>
    public Action<SignInResult>? SignedIn { get; set; }

    /// <summary>Another account than before (from, to), through Sign in: the tokens of the one before are forgotten.</summary>
    public Action<SignInResult, SignInResult>? AccountSwitched { get; set; }

    /// <summary>The window that owns a sign-in window when there is no console (the tray's); zero or null for the desktop.</summary>
    public Func<IntPtr>? OwnerWindow { get; set; }

    /// <summary>True from a failed or needed sign-in for a resource until a token is issued for it again.</summary>
    public bool SignInProblem => !problems.IsEmpty;

    /// <summary>A problem only a sign-in window solves (MFA, an expired password, a cancelled window): one may open by itself without a tray icon.</summary>
    public bool SignInNeedsUser => problems.Values.Any(p => p.Error.NeedsUser);

    /// <summary>Why, in plain words for the tray, while SignInProblem is set.</summary>
    public string? SignInProblemText => problems.IsEmpty ? null : problemText;

    public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => GetAsync(resource, null, ct);

    public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct) => GetAsync(resource, claims, ct);

    async ValueTask<string> GetAsync(string resource, string? claims, CancellationToken ct)
    {
        if (claims is not null)
        {
            tokens.TryRemove(resource, out _); // a claims challenge revoked it: nobody gets it again
        }
        else if (Cached(resource) is { } cached)
        {
            return cached;
        }
        if (failures.TryGetValue(resource, out var failure) && Clock() < failure.Until && !(PromptWhenNeeded && failure.Error is SignInException { NeedsUser: true }))
        {
            throw Again(failure.Error);
        }
        try
        {
            return await Shared(resource, claims).WaitAsync(ct);
        }
        catch (SignInException e) when (e.NeedsUser && PromptWhenNeeded)
        {
        }
        return await InteractiveAsync(resource, claims, userAsked: false, ct) ?? throw new SignInException(SignInException.InteractionRequired,
            $"Sign-in needed ({SignInProblemText}); no sign-in window opens again for {PromptCooldown.TotalMinutes:0} minutes after one was cancelled.", true);
    }

    /// <summary>The silent request for <paramref name="resource"/> going on, or a new one: its callers share the result, and their cancellation doesn't end it.</summary>
    Task<string> Shared(string resource, string? claims)
    {
        var key = claims is null ? resource : $"{resource}\n{claims}";
        Lazy<Task<string>>? mine = null;
        mine = new(() => SilentAsync(resource, claims, key, mine!));
        return inflight.GetOrAdd(key, mine).Value;
    }

    async Task<string> SilentAsync(string resource, string? claims, string key, Lazy<Task<string>> self)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var timeout = new CancellationTokenSource(RequestTimeout);
                (WamResult Result, int Generation) answer;
                try
                {
                    answer = await RequestAsync(resource, claims, IntPtr.Zero, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new HttpRequestException($"Sign-in: Windows' sign-in (WAM) did not answer within {RequestTimeout.TotalSeconds:0} s");
                }
                if (Accept(resource, answer.Result, answer.Generation, interactive: false) is { } token)
                {
                    return token;
                }
                if (attempt > 0)
                {
                    throw new HttpRequestException("Sign-in: the signed-in account changed during the request");
                }
            }
        }
        catch (HttpRequestException e)
        {
            Remember(resource, e);
            throw;
        }
        catch (SignInException e)
        {
            Remember(resource, e);
            Problem(e, resource, claims);
            throw;
        }
        finally
        {
            inflight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(key, self));
        }
    }

    /// <summary>Requests fail at once for a while after this; the wait for failures other than the network and a needed sign-in doubles each time.</summary>
    void Remember(string resource, Exception e)
    {
        var wait = e switch
        {
            HttpRequestException => NetworkRetry,
            SignInException { NeedsUser: true } => SilentRetry,
            _ when failures.TryGetValue(resource, out var known) && known.Error is SignInException { NeedsUser: false } =>
                TimeSpan.FromTicks(Math.Min(known.Wait.Ticks * 2, MaxFailureRetry.Ticks)),
            _ => FailureRetry,
        };
        failures[resource] = new Failure(e, Clock() + wait, wait);
    }

    /// <summary>The same failure again, as a new exception (one exception thrown on several threads would share its stack trace).</summary>
    static Exception Again(Exception e) => e switch
    {
        SignInException signIn => new SignInException(signIn.Code, signIn.Message, signIn.NeedsUser),
        _ => new HttpRequestException(e.Message, e),
    };

    /// <summary>
    /// Opens the sign-in window for what last needed it (the tray's Sign in, or the start of the agent). One window at a time; after a
    /// cancel, only <paramref name="userAsked"/> opens one within PromptCooldown. True when a token was issued. <paramref name="force"/>
    /// shows the window even when the Windows account would do (the console's whoami --sign-in).
    /// </summary>
    public async Task<bool> SignInAsync(bool userAsked, CancellationToken ct, bool force = false)
    {
        var resource = problemResource ?? M365Client.GraphResource;
        return await InteractiveAsync(resource, problems.TryGetValue(resource, out var problem) ? problem.Claims : null, userAsked, ct, force) is not null;
    }

    async Task<string?> InteractiveAsync(string resource, string? claims, bool userAsked, CancellationToken ct, bool force = false)
    {
        if (!userAsked && Clock() < promptAgainAt)
        {
            return null;
        }
        if (!await interactiveGate.WaitAsync(userAsked ? 0 : Timeout.Infinite, ct))
        {
            return null; // a window is open already
        }
        SignInException? failed = null;
        try
        {
            if (claims is null && !force && Cached(resource) is { } meanwhile)
            {
                return meanwhile;
            }
            var (result, generation) = await RequestAsync(resource, claims, ParentWindow(), ct, force);
            return Accept(resource, result, generation, interactive: true)
                ?? throw new SignInException(SignInException.Cancelled, "The signed-in account changed during the sign-in", true);
        }
        catch (SignInException e)
        {
            if (!userAsked || e.Code == SignInException.Cancelled)
            {
                promptAgainAt = Clock() + PromptCooldown; // no window storm: the next one opens on Sign in, or after the cooldown
            }
            failed = e;
            if (userAsked)
            {
                return null;
            }
            throw;
        }
        finally
        {
            interactiveGate.Release();
            if (failed is not null)
            {
                Problem(failed, resource, claims); // its notification never waits under the gate
            }
        }
    }

    /// <summary>
    /// One token request; a window (non-zero) shows the sign-in. Failures become SignInException, or HttpRequestException when offline.
    /// Returns the account generation the request used.
    /// </summary>
    async Task<(WamResult Result, int Generation)> RequestAsync(string resource, string? claims, IntPtr window, CancellationToken ct, bool forcePrompt = false)
    {
        var properties = new Dictionary<string, string>
        {
            ["wam_compat"] = "2.0", // scopes and responses as with the v2.0 endpoint
            ["authority"] = $"https://login.microsoftonline.com/{tenant}",
            ["claims"] = Wam.Claims(claims),
        };
        IntPtr accountReference;
        int used;
        lock (accountGate)
        {
            accountReference = account?.AddRef() ?? IntPtr.Zero; // the request's own: a switch meanwhile may dispose the account
            used = generation;
        }
        WamResult result;
        try
        {
            var client = await ClientAsync(ct);
            result = await client.RequestAsync($"{resource.TrimEnd('/')}/.default", clientId, properties, accountReference, window, ct, forcePrompt);
        }
        catch (COMException e) when (IsNetworkError(unchecked((uint)e.HResult)))
        {
            throw new HttpRequestException($"Sign-in: no connection ({e.Message})", e);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            throw new SignInException(SignInException.Unavailable, $"Windows' sign-in (WAM) failed: {e.Message} (0x{e.HResult:X8})");
        }
        finally
        {
            if (accountReference != IntPtr.Zero)
            {
                Marshal.Release(accountReference);
            }
        }
        if (result.Status is WamStatus.Success or WamStatus.AccountSwitch && result.Token is { Length: > 0 })
        {
            return (result, used);
        }
        result.Account?.Dispose();
        Exception error = result.Status switch
        {
            WamStatus.UserCancel => new SignInException(SignInException.Cancelled, "The sign-in was cancelled", true),
            WamStatus.UserInteractionRequired => new SignInException(CodeOf(result) ?? SignInException.InteractionRequired, TextOf(result), true),
            WamStatus.AccountProviderNotAvailable => new SignInException(SignInException.Unavailable, TextOf(result)),
            _ when IsNetworkError(result.ErrorCode) => new HttpRequestException($"Sign-in: no connection ({TextOf(result)})"),
            _ when CodeOf(result) is { } code && NeedUser.Contains(code) => new SignInException(code, TextOf(result), true),
            _ => new SignInException(CodeOf(result) ?? $"wam_{result.ErrorCode:X8}", TextOf(result)),
        };
        throw error;
    }

    async Task<IWamBroker> ClientAsync(CancellationToken ct)
    {
        if (wam is { } known)
        {
            return known;
        }
        await clientGate.WaitAsync(ct);
        try
        {
            return wam ??= await connect(ct);
        }
        finally
        {
            clientGate.Release();
        }
    }

    static string? CodeOf(WamResult result) =>
        result.ErrorMessage is { } message && message.IndexOf("AADSTS", StringComparison.Ordinal) is var at and >= 0
            ? new string(message[at..].TakeWhile(char.IsLetterOrDigit).ToArray())
            : null;

    static string TextOf(WamResult result) => $"{result.ErrorMessage?.Trim() ?? result.Status.ToString()} (0x{result.ErrorCode:X8})";

    /// <summary>WinINet's no-connection errors, as Windows (0x8007....) and as the work or school account provider (0xCAA8....) report them.</summary>
    static bool IsNetworkError(uint code) =>
        (code >> 16) is 0x8007 or 0xCAA8 && (code & 0xFFFF) is 0x2EE2 or 0x2EE7 or 0x2EFD or 0x2EFE or 0x2EFF or 0x2F78;

    string? Cached(string resource) =>
        tokens.TryGetValue(resource, out var cached) && cached.ExpiresOn > Clock().AddMinutes(5) ? cached.Token : null;

    /// <summary>
    /// Takes a token: the account for later silent requests, the cache, the end of the resource's problem. Another account than before
    /// (a switch through Sign in) drops the other account's tokens. Null when the request used an account replaced meanwhile.
    /// </summary>
    string? Accept(string resource, WamResult result, int used, bool interactive)
    {
        var tenantId = result.Properties.GetValueOrDefault("TenantId");
        var username = result.Properties.GetValueOrDefault("UPN") ?? result.Account?.UserName ?? result.Properties.GetValueOrDefault("UserName") ?? "";
        if (result.Properties.GetValueOrDefault("HomeTenantId") is { Length: > 0 } home && tenantId is { Length: > 0 } && !home.Equals(tenantId, StringComparison.OrdinalIgnoreCase))
        {
            // A guest (B2B) or an account synced in from another tenant: not supported (only members in their own tenant)
            result.Account?.Dispose();
            throw new SignInException(SignInException.Guest,
                $"{username} belongs to tenant {home}, not to tenant {tenantId}. VFS365 works with member accounts in their own tenant only.");
        }
        var signedIn = new SignInResult(username, tenantId, result.Properties.GetValueOrDefault("OID"));
        SignInResult? previous = null;
        WamAccount? unused;
        bool stale, changed = false;
        lock (accountGate)
        {
            stale = used != generation;
            if (stale)
            {
                unused = result.Account;
            }
            else
            {
                previous = LastResult;
                changed = previous is null || !string.Equals(previous.ObjectId ?? previous.Username, signedIn.ObjectId ?? signedIn.Username, StringComparison.OrdinalIgnoreCase);
                if (account is null || changed)
                {
                    (unused, account) = (account, result.Account); // in-flight requests hold their own reference of the one replaced
                }
                else
                {
                    unused = result.Account;
                }
                if (changed && previous is not null)
                {
                    generation++;
                    tokens.Clear();
                    failures.Clear();
                    problems.Clear();
                }
                tokens[resource] = (result.Token!, Wam.ExpiresOn(result.Token!) ?? Clock().AddMinutes(30));
                LastResult = signedIn;
                failures.TryRemove(resource, out _);
                problems.TryRemove(resource, out _);
                if (interactive)
                {
                    // The user just signed in: what needed that is tried silently again at once
                    foreach (var (other, problem) in problems.Where(p => p.Value.Error.NeedsUser).ToList())
                    {
                        problems.TryRemove(other, out _);
                        failures.TryRemove(other, out _);
                    }
                }
            }
        }
        unused?.Dispose();
        if (stale)
        {
            return null;
        }
        if (changed)
        {
            SignedIn?.Invoke(signedIn);
            if (previous is not null)
            {
                AccountSwitched?.Invoke(previous, signedIn);
            }
        }
        return result.Token!;
    }

    void Problem(SignInException e, string resource, string? claims)
    {
        var text = SignInText.Of(e);
        var known = problems.Values.Any(p => SignInText.Of(p.Error) == text);
        problems[resource] = (e, claims);
        (problemText, problemResource) = (text, resource);
        if (!known)
        {
            SignInFailed?.Invoke(e); // once per problem, not on every retry or for each resource
        }
    }

    /// <summary>
    /// Forgets this app's tokens and signs the account out of VFS365 in WAM (WebAccount.SignOutAsync with the client ID), also in a new
    /// process (vfs365 signout), where a silent request finds the account first. The Windows account itself stays signed in.
    /// </summary>
    public async Task SignOutAsync()
    {
        WamAccount? target;
        lock (accountGate)
        {
            (target, account) = (account, null);
            generation++;
        }
        try
        {
            if (target is null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(RequestTimeout);
                    target = (await RequestAsync(M365Client.GraphResource, null, IntPtr.Zero, timeout.Token)).Result.Account;
                }
                catch (Exception e) when (e is SignInException or HttpRequestException or OperationCanceledException)
                {
                }
            }
            if (target is not null)
            {
                using var timeout = new CancellationTokenSource(RequestTimeout);
                await (await ClientAsync(timeout.Token)).SignOutAsync(target.Pointer, clientId, timeout.Token);
            }
        }
        catch (Exception e) when (e is COMException or OperationCanceledException or InvalidOperationException)
        {
            // Signing out of WAM is best effort: the tokens below are forgotten anyway
        }
        finally
        {
            target?.Dispose();
        }
        tokens.Clear();
        failures.Clear();
        problems.Clear();
        LastResult = null;
    }

    /// <summary>
    /// Owner of the sign-in window: the console (GA_ROOTOWNER also works under Windows Terminal), else a window of this process (the
    /// tray's, or the foreground one when it is ours), else the desktop. Never another program's window: owner and owned window share
    /// their input, so a program that hangs would hang the sign-in too.
    /// </summary>
    IntPtr ParentWindow()
    {
        if (GetConsoleWindow() is var console && console != IntPtr.Zero)
        {
            return GetAncestor(console, 3);
        }
        if (OwnerWindow?.Invoke() is { } own && own != IntPtr.Zero)
        {
            return own;
        }
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && GetWindowThreadProcessId(foreground, out var process) != 0 && process == Environment.ProcessId
            ? foreground
            : GetDesktopWindow();
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}
