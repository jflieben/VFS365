using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>Tokens through the WAM broker: silent with the Windows account, a sign-in prompt only when needed.</summary>
sealed class MsalTokenSource : ITokenSource
{
    readonly IPublicClientApplication app;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<string, AuthenticationResult> tokens = new(StringComparer.OrdinalIgnoreCase);
    IAccount? account;

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

    /// <summary>Sign-in failed (prompt cancelled, Conditional Access, no consent), for a notification.</summary>
    public Action<MsalException>? SignInFailed { get; set; }

    /// <summary>True from a failed sign-in until a token is issued again.</summary>
    public bool SignInProblem { get; private set; }

    public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => GetAsync(resource, null, ct);

    public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct) => GetAsync(resource, claims, ct);

    async ValueTask<string> GetAsync(string resource, string? claims, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (claims is null && tokens.TryGetValue(resource, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return cached.AccessToken;
            }

            string[] scopes = [$"{resource.TrimEnd('/')}/.default"];
            AuthenticationResult result;
            try
            {
                var silent = app.AcquireTokenSilent(scopes, account ?? PublicClientApplication.OperatingSystemAccount);
                if (claims is not null)
                {
                    silent = silent.WithClaims(claims);
                }
                result = await silent.ExecuteAsync(ct);
            }
            catch (MsalUiRequiredException)
            {
                var interactive = app.AcquireTokenInteractive(scopes);
                if (account is not null)
                {
                    interactive = interactive.WithAccount(account);
                }
                if (claims is not null)
                {
                    interactive = interactive.WithClaims(claims);
                }
                try
                {
                    result = await interactive.ExecuteAsync(ct);
                }
                catch (MsalException e)
                {
                    SignInProblem = true;
                    SignInFailed?.Invoke(e);
                    throw;
                }
            }

            if (result.Account?.HomeAccountId?.TenantId is { Length: > 0 } home && !home.Equals(result.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                // A guest (B2B) or an account synced in from another tenant: not supported (only members in their own tenant)
                SignInProblem = true;
                throw new MsalClientException("guest_account",
                    $"{result.Account.Username} belongs to tenant {home}, not to tenant {result.TenantId}. VFS365 works with member accounts in their own tenant only.");
            }
            account = result.Account;
            tokens[resource] = LastResult = result;
            SignInProblem = false;
            return result.AccessToken;
        }
        finally
        {
            gate.Release();
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
