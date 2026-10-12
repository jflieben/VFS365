using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// Tokens through the WAM broker: silent with the Windows account. A sign-in window appears only from SignInAsync (the tray's Sign in,
/// or the start of the agent) or, with PromptWhenNeeded (the console tool), from a request; never while a lock is held. Requests that
/// need a sign-in otherwise fail at once and set SignInProblem, so file system and background work never wait for a user. Cached
/// tokens are handed out without waiting for anything.
/// </summary>
sealed class MsalTokenSource : ITokenSource
{
    /// <summary>After a sign-in window that opened by itself failed, or any that was cancelled, none opens by itself for this long (Sign in still works).</summary>
    public static readonly TimeSpan PromptCooldown = TimeSpan.FromMinutes(15);

    /// <summary>After a silent sign-in found it needs the user, requests fail at once for this long before trying silently again.</summary>
    static readonly TimeSpan SilentRetry = TimeSpan.FromSeconds(30);

    readonly IPublicClientApplication app;
    readonly ConcurrentDictionary<string, AuthenticationResult> tokens = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, SemaphoreSlim> silentGates = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim interactiveGate = new(1, 1);
    IAccount? account;
    string? problemResource, problemClaims;
    DateTimeOffset silentAgainAt, promptAgainAt;

    public MsalTokenSource(string clientId, string tenant)
    {
        app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenant)
            .WithDefaultRedirectUri()
            .WithParentActivityOrWindow(ConsoleWindow)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "VFS365" })
            .WithClientCapabilities(["cp1"]) // can answer claims challenges: tokens are revoked at once instead of at expiry (CAE)
            .Build();
    }

    public AuthenticationResult? LastResult { get; private set; }

    /// <summary>A request that needs a sign-in may open the window itself (the console tool runs in the foreground).</summary>
    public bool PromptWhenNeeded { get; set; }

    /// <summary>Sign-in failed or is needed (prompt cancelled, Conditional Access, no consent, password changed), for a notification.</summary>
    public Action<MsalException>? SignInFailed { get; set; }

    /// <summary>The first token, and each time the account changes: for the log.</summary>
    public Action<AuthenticationResult>? SignedIn { get; set; }

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
        string[] scopes = [$"{resource.TrimEnd('/')}/.default"];
        if (SignInProblem && DateTimeOffset.UtcNow < silentAgainAt && !PromptWhenNeeded)
        {
            throw new MsalUiRequiredException(MsalError.NoTokensFoundError, "Sign-in needed: choose Sign in in the VFS365 tray menu.");
        }

        var gate = silentGates.GetOrAdd(resource, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (claims is null && Cached(resource) is { } meanwhile)
            {
                return meanwhile;
            }
            var silent = app.AcquireTokenSilent(scopes, account ?? PublicClientApplication.OperatingSystemAccount);
            if (claims is not null)
            {
                silent = silent.WithClaims(claims);
            }
            return Accept(resource, await silent.ExecuteAsync(ct));
        }
        catch (MsalUiRequiredException e)
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
        return await InteractiveAsync(resource, claims, userAsked: false, ct) ?? throw new MsalUiRequiredException(MsalError.NoTokensFoundError,
            $"Sign-in needed ({SignInProblemText}); no sign-in window opens again for {PromptCooldown.TotalMinutes:0} minutes after one was cancelled.");
    }

    /// <summary>
    /// Opens the sign-in window for what last needed it (the tray's Sign in, or the start of the agent). One window at a time; after a
    /// cancel, only <paramref name="userAsked"/> opens one within PromptCooldown. True when a token was issued.
    /// </summary>
    public async Task<bool> SignInAsync(bool userAsked, CancellationToken ct) =>
        await InteractiveAsync(problemResource ?? M365Client.GraphResource, problemClaims, userAsked, ct) is not null;

    async Task<string?> InteractiveAsync(string resource, string? claims, bool userAsked, CancellationToken ct)
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
            string[] scopes = [$"{resource.TrimEnd('/')}/.default"];
            if (claims is null && Cached(resource) is { } meanwhile)
            {
                return meanwhile;
            }
            var interactive = app.AcquireTokenInteractive(scopes);
            if (account is not null)
            {
                interactive = interactive.WithAccount(account);
            }
            if (claims is not null)
            {
                interactive = interactive.WithClaims(claims);
            }
            return Accept(resource, await interactive.ExecuteAsync(ct));
        }
        catch (MsalException e)
        {
            if (!userAsked || e.ErrorCode == MsalError.AuthenticationCanceledError)
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

    string? Cached(string resource) =>
        tokens.TryGetValue(resource, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5) ? cached.AccessToken : null;

    string Accept(string resource, AuthenticationResult result)
    {
        if (result.Account?.HomeAccountId?.TenantId is { Length: > 0 } home && !home.Equals(result.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            // A guest (B2B) or an account synced in from another tenant: not supported (only members in their own tenant)
            var guest = new MsalClientException("guest_account",
                $"{result.Account.Username} belongs to tenant {home}, not to tenant {result.TenantId}. VFS365 works with member accounts in their own tenant only.");
            Problem(guest, resource, null);
            throw guest;
        }
        var changed = account is null || account.HomeAccountId?.Identifier != result.Account?.HomeAccountId?.Identifier;
        account = result.Account;
        tokens[resource] = LastResult = result;
        (SignInProblem, SignInProblemText, problemResource, problemClaims) = (false, null, null, null);
        if (changed)
        {
            SignedIn?.Invoke(result);
        }
        return result.AccessToken;
    }

    void Problem(MsalException e, string resource, string? claims)
    {
        var known = SignInProblem ? SignInProblemText : null;
        (SignInProblem, SignInProblemText, problemResource, problemClaims) = (true, SignInText.Of(e), resource, claims);
        silentAgainAt = DateTimeOffset.UtcNow + SilentRetry;
        if (known != SignInProblemText)
        {
            SignInFailed?.Invoke(e); // once per problem, not on every retry
        }
    }

    /// <summary>Forgets the accounts this app signed in with (the Windows account itself stays signed in).</summary>
    public async Task SignOutAsync()
    {
        foreach (var known in await app.GetAccountsAsync())
        {
            await app.RemoveAsync(known);
        }
        tokens.Clear();
        account = null;
    }

    /// <summary>Parent for the sign-in prompt: the console (GA_ROOTOWNER also works under Windows Terminal), else the foreground window (background agent).</summary>
    static IntPtr ConsoleWindow() => GetConsoleWindow() is var console && console != IntPtr.Zero ? GetAncestor(console, 3) : GetForegroundWindow();

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
