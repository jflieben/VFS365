using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Vfs365.Agent;

/// <summary>
/// Windows' Web Account Manager (WAM), called directly through its WinRT interfaces in combase.dll: Windows' own sign-in broker, with
/// no broker runtime or projection assembly shipped. Interface IDs and method slots are from Windows' metadata
/// (System32\WinMetadata\Windows.Security.winmd and Windows.Foundation.winmd); slot 6 is the first method after IInspectable.
/// </summary>
public static class Wam
{
    public const string MicrosoftProvider = "https://login.microsoft.com";

    /// <summary>The ID of a parameterized WinRT interface (such as IAsyncOperation of a class), derived from its signature.</summary>
    public static Guid ParameterizedIid(string signature)
    {
        byte[] winRtNamespace = [0x11, 0xf4, 0x7a, 0xd5, 0x7b, 0x73, 0x42, 0xc0, 0xab, 0xae, 0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee];
        var hash = SHA1.HashData([.. winRtNamespace, .. Encoding.UTF8.GetBytes(signature)]);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>
    /// The claims for a request: the client capability cp1 (VFS365 answers claims challenges, so tokens are revoked at once instead of at
    /// expiry: CAE) merged with a claims challenge's own.
    /// </summary>
    public static string Claims(string? challenge)
    {
        var claims = new JsonObject { ["access_token"] = new JsonObject { ["xms_cc"] = new JsonObject { ["values"] = new JsonArray("cp1") } } };
        if (challenge is not null && JsonNode.Parse(challenge) is JsonObject asked)
        {
            foreach (var (section, value) in asked)
            {
                if (value is JsonObject entries && claims[section] is JsonObject known)
                {
                    foreach (var (name, entry) in entries)
                    {
                        known[name] = entry?.DeepClone();
                    }
                }
                else
                {
                    claims[section] = value?.DeepClone();
                }
            }
        }
        return claims.ToJsonString();
    }

    /// <summary>When a JWT expires (its exp claim), or null when it can't be read.</summary>
    public static DateTimeOffset? ExpiresOn(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            var json = JsonNode.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            return json?["exp"]?.GetValue<long>() is { } exp ? DateTimeOffset.FromUnixTimeSeconds(exp) : null;
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>WebTokenRequestStatus.</summary>
enum WamStatus { Success, UserCancel, AccountSwitch, UserInteractionRequired, AccountProviderNotAvailable, ProviderError }

/// <summary>A WebAccount; holds a reference until disposed.</summary>
sealed class WamAccount(IntPtr account, string userName) : IDisposable
{
    IntPtr pointer = account;

    public string UserName { get; } = userName;

    public IntPtr Pointer => pointer;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref pointer, IntPtr.Zero) is var owned && owned != IntPtr.Zero)
        {
            Marshal.Release(owned);
        }
    }
}

/// <summary>What a token request returned. Account is set on success (dispose it when not kept).</summary>
sealed record WamResult(WamStatus Status, string? Token, WamAccount? Account, IReadOnlyDictionary<string, string> Properties, uint ErrorCode, string? ErrorMessage);

/// <summary>Token requests to the Microsoft work or school account provider. WAM's objects are agile: calls work from any thread.</summary>
sealed class WamClient
{
    const string ManagerClass = "Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager";
    const string RequestClass = "Windows.Security.Authentication.Web.Core.WebTokenRequest";

    static readonly Guid IWebAuthenticationCoreManagerStatics = new("6aca7c92-a581-4479-9c10-752eff44fd34");
    static readonly Guid IWebAuthenticationCoreManagerInterop = new("f4b8e804-811e-4436-b69c-44cb67b72084");
    static readonly Guid IWebTokenRequestFactory = new("6cf2141c-0ff0-4c67-b84f-99ddbe4a72c9");
    static readonly Guid IWebAccount2 = new("7b56d6f8-990b-4eb5-94a7-5621f3a8b824");

    /// <summary>IAsyncOperation of WebTokenRequestResult, which RequestTokenForWindowAsync asks for.</summary>
    internal static readonly Guid IAsyncOperationOfResult = Wam.ParameterizedIid(
        "pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};rc(Windows.Security.Authentication.Web.Core.WebTokenRequestResult;{c12a8305-d1f8-4483-8d54-38fe292784ff}))");

    /// <summary>Response properties read into WamResult.Properties.</summary>
    static readonly string[] Wanted = ["TenantId", "HomeTenantId", "UPN", "UserName", "OID"];

    readonly IntPtr statics, interop, requests, provider;

    WamClient(IntPtr statics, IntPtr interop, IntPtr requests, IntPtr provider) =>
        (this.statics, this.interop, this.requests, this.provider) = (statics, interop, requests, provider);

    /// <summary>The client for the Microsoft work or school account provider; lives as long as the process.</summary>
    public static async Task<WamClient> CreateAsync(CancellationToken ct)
    {
        WinRt.JoinMta();
        var statics = WinRt.Factory(ManagerClass, IWebAuthenticationCoreManagerStatics);
        var interop = WinRt.Factory(ManagerClass, IWebAuthenticationCoreManagerInterop);
        var requests = WinRt.Factory(RequestClass, IWebTokenRequestFactory);
        // FindAccountProviderAsync(id, authority)
        var operation = WinRt.WithStrings([Wam.MicrosoftProvider, "organizations"], s => WinRt.Out(statics, 12, s[0], s[1]));
        var provider = await ResultAsync(operation, ct);
        return provider == IntPtr.Zero
            ? throw new InvalidOperationException("Windows has no provider for work or school accounts")
            : new WamClient(statics, interop, requests, provider);
    }

    /// <summary>
    /// A token for <paramref name="scope"/>: silently when <paramref name="window"/> is zero, else with a sign-in window owned by it.
    /// Without <paramref name="account"/>, the Windows account (silent) or the user's choice (window).
    /// </summary>
    public async Task<WamResult> RequestAsync(string scope, string clientId, IReadOnlyDictionary<string, string> properties, WamAccount? account,
        IntPtr window, CancellationToken ct, bool forcePrompt = false)
    {
        var request = WinRt.WithStrings([scope, clientId], s => forcePrompt
            ? WinRt.CreateRequest(requests, provider, s[0], s[1], 1) // CreateWithPromptType(..., ForceAuthentication)
            : WinRt.Out(requests, 6, provider, s[0], s[1])); // Create(provider, scope, clientId)
        IntPtr operation;
        try
        {
            var map = WinRt.Out(request, 10); // get_Properties
            try
            {
                foreach (var (key, value) in properties)
                {
                    WinRt.Insert(map, key, value);
                }
            }
            finally
            {
                Marshal.Release(map);
            }
            var accountPointer = account?.Pointer ?? IntPtr.Zero;
            operation = window != IntPtr.Zero ? WinRt.RequestForWindow(interop, window, request, accountPointer, IAsyncOperationOfResult)
                : accountPointer == IntPtr.Zero ? WinRt.Out(statics, 6, request) // GetTokenSilentlyAsync(request)
                : WinRt.Out(statics, 7, request, accountPointer); // GetTokenSilentlyAsync(request, account)
        }
        finally
        {
            Marshal.Release(request);
        }
        var result = await ResultAsync(operation, ct);
        try
        {
            return Read(result);
        }
        finally
        {
            Marshal.Release(result);
        }
    }

    /// <summary>Signs <paramref name="account"/> out of this app (WebAccount.SignOutAsync(clientId)); the Windows account stays signed in.</summary>
    public static async Task SignOutAsync(WamAccount account, string clientId, CancellationToken ct)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(account.Pointer, in IWebAccount2, out var account2));
        IntPtr action;
        try
        {
            action = WinRt.WithStrings([clientId], s => WinRt.Out(account2, 10, s[0]));
        }
        finally
        {
            Marshal.Release(account2);
        }
        await ResultAsync(action, ct, hasResult: false);
    }

    static WamResult Read(IntPtr result)
    {
        var status = (WamStatus)WinRt.OutInt(result, 7); // get_ResponseStatus
        var (code, message) = (0u, (string?)null);
        if (WinRt.Out(result, 8) is var error && error != IntPtr.Zero) // get_ResponseError
        {
            try
            {
                code = (uint)WinRt.OutInt(error, 6);
                message = WinRt.ReadString(WinRt.Out(error, 7));
            }
            finally
            {
                Marshal.Release(error);
            }
        }
        var none = new Dictionary<string, string>();
        if (status != WamStatus.Success)
        {
            return new(status, null, null, none, code, message);
        }

        var responses = WinRt.Out(result, 6); // get_ResponseData
        try
        {
            if (WinRt.OutInt(responses, 7) == 0) // get_Size
            {
                return new(WamStatus.ProviderError, null, null, none, code, message ?? "No token in the response");
            }
            var response = WinRt.GetAt(responses, 0);
            try
            {
                var token = WinRt.ReadString(WinRt.Out(response, 6)); // get_Token
                var account = WinRt.Out(response, 8); // get_WebAccount
                var userName = account == IntPtr.Zero ? "" : WinRt.ReadString(WinRt.Out(account, 7)); // IWebAccount.get_UserName
                var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var map = WinRt.Out(response, 9); // get_Properties
                try
                {
                    foreach (var key in Wanted)
                    {
                        if (WinRt.Lookup(map, key) is { } value)
                        {
                            found[key] = value;
                        }
                    }
                }
                finally
                {
                    Marshal.Release(map);
                }
                return new(status, token, account == IntPtr.Zero ? null : new WamAccount(account, userName), found, code, message);
            }
            finally
            {
                Marshal.Release(response);
            }
        }
        finally
        {
            Marshal.Release(responses);
        }
    }

    /// <summary>
    /// Waits for an IAsyncOperation (or IAsyncAction) and returns its result (an owned pointer; zero for an action). Polls its status:
    /// a completion handler would need a COM object of our own, and requests take milliseconds (cached) to minutes (a sign-in window).
    /// </summary>
    static async Task<IntPtr> ResultAsync(IntPtr operation, CancellationToken ct, bool hasResult = true)
    {
        var info = IntPtr.Zero;
        try
        {
            info = WinRt.AsyncInfo(operation);
            var delay = 1;
            var cancelled = false;
            int status;
            while ((status = WinRt.OutInt(info, 7)) == 0) // get_Status: Started
            {
                if (ct.IsCancellationRequested && !cancelled)
                {
                    WinRt.Call(info, 9); // Cancel
                    cancelled = true;
                }
                await Task.Delay(delay, CancellationToken.None);
                delay = Math.Min(delay * 2, 50);
            }
            return status switch
            {
                1 => hasResult ? WinRt.Out(operation, 8) : IntPtr.Zero, // Completed: GetResults
                2 => throw new OperationCanceledException(ct),
                _ => throw Marshal.GetExceptionForHR(WinRt.OutInt(info, 8)) ?? new COMException("The WAM request failed"), // get_ErrorCode
            };
        }
        finally
        {
            if (info != IntPtr.Zero)
            {
                WinRt.Call(info, 10); // Close
                Marshal.Release(info);
            }
            Marshal.Release(operation);
        }
    }
}

/// <summary>Calls on WinRT interfaces by vtable slot, and HSTRINGs. Every returned interface pointer is owned by the caller.</summary>
static unsafe class WinRt
{
    static readonly Guid IAsyncInfo = new("00000036-0000-0000-c000-000000000046");
    static int mta;

    /// <summary>Threads that never initialized COM join the process's multithreaded apartment.</summary>
    public static void JoinMta()
    {
        if (Interlocked.Exchange(ref mta, 1) == 0)
        {
            Check(CoIncrementMTAUsage(out _));
        }
    }

    public static IntPtr Factory(string className, Guid iid) => WithStrings([className], s =>
    {
        Check(RoGetActivationFactory(s[0], in iid, out var factory));
        return factory;
    });

    public static IntPtr AsyncInfo(IntPtr operation)
    {
        Check(Marshal.QueryInterface(operation, in IAsyncInfo, out var info));
        return info;
    }

    public static void Call(IntPtr instance, int slot) => Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(instance, slot))(instance));

    public static IntPtr Out(IntPtr instance, int slot)
    {
        IntPtr value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(instance, slot))(instance, &value));
        return value;
    }

    public static IntPtr Out(IntPtr instance, int slot, IntPtr a)
    {
        IntPtr value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(instance, slot))(instance, a, &value));
        return value;
    }

    public static IntPtr Out(IntPtr instance, int slot, IntPtr a, IntPtr b)
    {
        IntPtr value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Slot(instance, slot))(instance, a, b, &value));
        return value;
    }

    public static IntPtr Out(IntPtr instance, int slot, IntPtr a, IntPtr b, IntPtr c)
    {
        IntPtr value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr*, int>)Slot(instance, slot))(instance, a, b, c, &value));
        return value;
    }

    public static int OutInt(IntPtr instance, int slot)
    {
        int value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(instance, slot))(instance, &value));
        return value;
    }

    /// <summary>IWebTokenRequestFactory.CreateWithPromptType.</summary>
    public static IntPtr CreateRequest(IntPtr factory, IntPtr provider, IntPtr scope, IntPtr clientId, int promptType)
    {
        IntPtr value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, int, IntPtr*, int>)Slot(factory, 7))(factory, provider, scope, clientId, promptType, &value));
        return value;
    }

    /// <summary>IVectorView.GetAt.</summary>
    public static IntPtr GetAt(IntPtr vector, uint index)
    {
        IntPtr value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(vector, 6))(vector, index, &value));
        return value;
    }

    /// <summary>IMap of strings: Insert.</summary>
    public static void Insert(IntPtr map, string key, string value) => WithStrings([key, value], s =>
    {
        byte replaced;
        return Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, byte*, int>)Slot(map, 10))(map, s[0], s[1], &replaced));
    });

    /// <summary>IMap of strings: HasKey, then Lookup; null when the key is missing.</summary>
    public static string? Lookup(IntPtr map, string key) => WithStrings([key], s =>
    {
        byte has;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)Slot(map, 8))(map, s[0], &has));
        return has == 0 ? null : ReadString(Out(map, 6, s[0]));
    });

    /// <summary>IWebAuthenticationCoreManagerInterop.RequestTokenForWindowAsync, or RequestTokenWithWebAccountForWindowAsync with an account.</summary>
    public static IntPtr RequestForWindow(IntPtr interop, IntPtr window, IntPtr request, IntPtr account, Guid iid)
    {
        IntPtr operation;
        Check(account == IntPtr.Zero
            ? ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(interop, 6))(interop, window, request, &iid, &operation)
            : ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(interop, 7))(interop, window, request, account, &iid, &operation));
        return operation;
    }

    /// <summary>Runs <paramref name="use"/> with HSTRINGs of <paramref name="values"/>, deleted afterwards.</summary>
    public static T WithStrings<T>(string[] values, Func<IntPtr[], T> use)
    {
        var strings = new IntPtr[values.Length];
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                Check(WindowsCreateString(values[i], (uint)values[i].Length, out strings[i]));
            }
            return use(strings);
        }
        finally
        {
            foreach (var hstring in strings)
            {
                WindowsDeleteString(hstring);
            }
        }
    }

    /// <summary>The text of an owned HSTRING, which is deleted.</summary>
    public static string ReadString(IntPtr hstring)
    {
        try
        {
            var buffer = WindowsGetStringRawBuffer(hstring, out var length);
            return buffer is null ? "" : new string(buffer, 0, (int)length);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    static IntPtr Slot(IntPtr instance, int slot) => (*(IntPtr**)instance)[slot];

    static int Check(int hresult)
    {
        Marshal.ThrowExceptionForHR(hresult);
        return hresult;
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int CoIncrementMTAUsage(out IntPtr cookie);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}
