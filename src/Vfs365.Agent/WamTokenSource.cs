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
/// tokens are handed out without waiting for anything.
/// </summary>
sealed class WamTokenSource(string clientId, string tenant) : ITokenSource
{
    /// <summary>After a sign-in window that opened by itself failed, or any that was cancelled, none opens by itself for this long (Sign in still works).</summary>
    public static readonly TimeSpan PromptCooldown = TimeSpan.FromMinutes(15);

    /// <summary>After a silent sign-in found it needs the user, requests fail at once for this long before trying silently again.</summary>
    static readonly TimeSpan SilentRetry = TimeSpan.FromSeconds(30);

    /// <summary>AADSTS codes that a sign-in window can solve, when WAM reports them as a provider error.</summary>
    static readonly string[] NeedUser = ["AADSTS50076", "AADSTS50079", "AADSTS50058", "AADSTS50158", "AADSTS50173", "AADSTS50133", "AADSTS65001", "AADSTS700082", "AADSTS70043", "AADSTS50089"];

    readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresOn)> tokens = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, SemaphoreSlim> silentGates = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim interactiveGate = new(1, 1);
    readonly SemaphoreSlim clientGate = new(1, 1);
    WamClient? wam;
    WamAccount? account;
    bool accountChosen;
    string? problemResource, problemClaims;
    DateTimeOffset silentAgainAt, promptAgainAt;

    public SignInResult? LastResult { get; private set; }

    /// <summary>A request that needs a sign-in may open the window itself (the console tool runs in the foreground).</summary>
    public bool PromptWhenNeeded { get; set; }

    /// <summary>Sign-in failed or is needed (prompt cancelled, Conditional Access, no consent, password changed), for a notification.</summary>
    public Action<SignInException>? SignInFailed { get; set; }

    /// <summary>The first token, and each time the account changes: for the log.</summary>
    public Action<SignInResult>? SignedIn { get; set; }

    /// <summary>True from a failed or needed sign-in until a token is issued again.</summary>
    public bool SignInProblem { get; private set; }

    /// <summary>Why, in plain words for the tray, while SignInProblem is set.</summary>
    public string? SignInProblemText { get; private set; }

    public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => GetAsync(resource, null, ct);

    public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct) => GetAsync(resource, claims, ct);

    async ValueTask<string> GetAsync(string resource, string? claims, CancellationToken ct)
    {
        if (claims is null && Cached(resource) is { } cached)
        {
            return cached;
        }
        if (SignInProblem && DateTimeOffset.UtcNow < silentAgainAt && !PromptWhenNeeded)
        {
            throw new SignInException(SignInException.InteractionRequired, "Sign-in needed: choose Sign in in the VFS365 tray menu.", true);
        }

        var gate = silentGates.GetOrAdd(resource, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (claims is null && Cached(resource) is { } meanwhile)
            {
                return meanwhile;
            }
            return Accept(resource, await RequestAsync(resource, claims, IntPtr.Zero, ct));
        }
        catch (SignInException e) when (e.NeedsUser)
        {
            if (!PromptWhenNeeded)
            {
                Problem(e, resource, claims);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
        return await InteractiveAsync(resource, claims, userAsked: false, ct) ?? throw new SignInException(SignInException.InteractionRequired,
            $"Sign-in needed ({SignInProblemText}); no sign-in window opens again for {PromptCooldown.TotalMinutes:0} minutes after one was cancelled.", true);
    }

    /// <summary>
    /// Opens the sign-in window for what last needed it (the tray's Sign in, or the start of the agent). One window at a time; after a
    /// cancel, only <paramref name="userAsked"/> opens one within PromptCooldown. True when a token was issued. <paramref name="force"/>
    /// shows the window even when the Windows account would do (the console's whoami --sign-in).
    /// </summary>
    public async Task<bool> SignInAsync(bool userAsked, CancellationToken ct, bool force = false) =>
        await InteractiveAsync(problemResource ?? M365Client.GraphResource, problemClaims, userAsked, ct, force) is not null;

    async Task<string?> InteractiveAsync(string resource, string? claims, bool userAsked, CancellationToken ct, bool force = false)
    {
        if (!userAsked && DateTimeOffset.UtcNow < promptAgainAt)
        {
            return null;
        }
        if (!await interactiveGate.WaitAsync(userAsked ? 0 : Timeout.Infinite, ct))
        {
            return null; // a window is open already
        }
        try
        {
            if (claims is null && !force && Cached(resource) is { } meanwhile)
            {
                return meanwhile;
            }
            var result = Accept(resource, await RequestAsync(resource, claims, ParentWindow(), ct, force));
            accountChosen = true;
            return result;
        }
        catch (SignInException e)
        {
            if (!userAsked || e.Code == SignInException.Cancelled)
            {
                promptAgainAt = DateTimeOffset.UtcNow + PromptCooldown; // no window storm: the next one opens on Sign in, or after the cooldown
            }
            Problem(e, resource, claims);
            if (userAsked)
            {
                return null;
            }
            throw;
        }
        finally
        {
            interactiveGate.Release();
        }
    }

    /// <summary>One token request; a window (non-zero) shows the sign-in. Failures become SignInException, or HttpRequestException when offline.</summary>
    async Task<WamResult> RequestAsync(string resource, string? claims, IntPtr window, CancellationToken ct, bool forcePrompt = false)
    {
        var properties = new Dictionary<string, string>
        {
            ["wam_compat"] = "2.0", // scopes and responses as with the v2.0 endpoint
            ["authority"] = $"https://login.microsoftonline.com/{tenant}",
            ["claims"] = Wam.Claims(claims),
        };
        WamResult result;
        try
        {
            var client = await ClientAsync(ct);
            result = await client.RequestAsync($"{resource.TrimEnd('/')}/.default", clientId, properties, account, window, ct, forcePrompt);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            throw new SignInException(SignInException.Unavailable, $"Windows' sign-in (WAM) failed: {e.Message}");
        }
        return result.Status switch
        {
            WamStatus.Success or WamStatus.AccountSwitch when result.Token is { Length: > 0 } => result,
            WamStatus.UserCancel => throw new SignInException(SignInException.Cancelled, "The sign-in was cancelled", true),
            WamStatus.UserInteractionRequired => throw new SignInException(CodeOf(result) ?? SignInException.InteractionRequired, TextOf(result), true),
            WamStatus.AccountProviderNotAvailable => throw new SignInException(SignInException.Unavailable, TextOf(result)),
            _ when IsNetworkError(result.ErrorCode) => throw new HttpRequestException($"Sign-in: no connection ({TextOf(result)})"),
            _ when CodeOf(result) is { } code && NeedUser.Contains(code) => throw new SignInException(code, TextOf(result), true),
            _ => throw new SignInException(CodeOf(result) ?? $"wam_{result.ErrorCode:X8}", TextOf(result)),
        };
    }

    async Task<WamClient> ClientAsync(CancellationToken ct)
    {
        if (wam is { } known)
        {
            return known;
        }
        await clientGate.WaitAsync(ct);
        try
        {
            return wam ??= await WamClient.CreateAsync(ct);
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
        tokens.TryGetValue(resource, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5) ? cached.Token : null;

    string Accept(string resource, WamResult result)
    {
        var tenantId = result.Properties.GetValueOrDefault("TenantId");
        var username = result.Properties.GetValueOrDefault("UPN") ?? result.Account?.UserName ?? result.Properties.GetValueOrDefault("UserName") ?? "";
        if (result.Properties.GetValueOrDefault("HomeTenantId") is { Length: > 0 } home && tenantId is { Length: > 0 } && !home.Equals(tenantId, StringComparison.OrdinalIgnoreCase))
        {
            // A guest (B2B) or an account synced in from another tenant: not supported (only members in their own tenant)
            result.Account?.Dispose();
            var guest = new SignInException(SignInException.Guest,
                $"{username} belongs to tenant {home}, not to tenant {tenantId}. VFS365 works with member accounts in their own tenant only.");
            Problem(guest, resource, null);
            throw guest;
        }
        var signedIn = new SignInResult(username, tenantId, result.Properties.GetValueOrDefault("OID"));
        var changed = LastResult is null || !string.Equals(LastResult.ObjectId ?? LastResult.Username, signedIn.ObjectId ?? signedIn.Username, StringComparison.OrdinalIgnoreCase);
        if (account is null || changed)
        {
            account = result.Account; // the account later silent requests use; a replaced one is kept alive (requests may still use it)
        }
        else
        {
            result.Account?.Dispose();
        }
        tokens[resource] = (result.Token!, Wam.ExpiresOn(result.Token!) ?? DateTimeOffset.UtcNow.AddMinutes(30));
        LastResult = signedIn;
        (SignInProblem, SignInProblemText, problemResource, problemClaims) = (false, null, null, null);
        if (changed)
        {
            SignedIn?.Invoke(signedIn);
        }
        return result.Token!;
    }

    void Problem(SignInException e, string resource, string? claims)
    {
        var known = SignInProblem ? SignInProblemText : null;
        (SignInProblem, SignInProblemText, problemResource, problemClaims) = (true, SignInText.Of(e), resource, claims);
        silentAgainAt = DateTimeOffset.UtcNow + SilentRetry;
        if (known != SignInProblemText)
        {
            SignInFailed?.Invoke(e); // once per problem, not on every retry
        }
    }

    /// <summary>Forgets this app's tokens; an account chosen in a sign-in window is signed out of VFS365 (the Windows account itself stays signed in).</summary>
    public async Task SignOutAsync()
    {
        if (accountChosen && account is { } chosen)
        {
            try
            {
                await WamClient.SignOutAsync(chosen, clientId, default);
            }
            catch (Exception e) when (e is COMException or OperationCanceledException)
            {
                // Signing out of WAM is best effort: the tokens below are forgotten anyway
            }
        }
        tokens.Clear();
        (account, accountChosen, LastResult) = (null, false, null);
    }

    /// <summary>Owner of the sign-in window: the console (GA_ROOTOWNER also works under Windows Terminal), else the foreground window (background agent).</summary>
    static IntPtr ParentWindow() =>
        GetConsoleWindow() is var console && console != IntPtr.Zero ? GetAncestor(console, 3)
        : GetForegroundWindow() is var foreground && foreground != IntPtr.Zero ? foreground
        : GetDesktopWindow();

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
