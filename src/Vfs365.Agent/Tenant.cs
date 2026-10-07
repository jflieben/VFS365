using System.Runtime.InteropServices;

namespace Vfs365.Agent;

/// <summary>The device's Entra join or registration, from NetGetAadJoinInformation. Null when there is none.</summary>
sealed record DeviceJoin(string Kind, string TenantId, string? TenantName, string? DeviceId)
{
    public static DeviceJoin? Read()
    {
        if (NetGetAadJoinInformation(null, out var info) != 0 || info == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var join = Marshal.PtrToStructure<JoinInfo>(info);
            var tenantId = Marshal.PtrToStringUni(join.TenantId);
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return null;
            }
            var kind = join.JoinType switch { 1 => "joined", 2 => "registered", _ => "linked" };
            return new DeviceJoin(kind, tenantId, Marshal.PtrToStringUni(join.TenantDisplayName), Marshal.PtrToStringUni(join.DeviceId));
        }
        finally
        {
            NetFreeAadJoinInformation(info);
        }
    }

    // Leading fields of DSREG_JOIN_INFO
    [StructLayout(LayoutKind.Sequential)]
    struct JoinInfo
    {
        public int JoinType;
        public IntPtr JoinCertificate;
        public IntPtr DeviceId;
        public IntPtr IdpDomain;
        public IntPtr TenantId;
        public IntPtr JoinUserEmail;
        public IntPtr TenantDisplayName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetGetAadJoinInformation(string? tenantId, out IntPtr joinInfo);

    [DllImport("netapi32.dll")]
    static extern void NetFreeAadJoinInformation(IntPtr joinInfo);
}

sealed record TenantChoice(string Tenant, string Source);

static class TenantResolver
{
    public const string AnyOrganization = "organizations";

    /// <summary>Forced tenant from settings, else the device's Entra tenant, else any work account (the sign-in decides).</summary>
    public static TenantChoice Resolve(AgentSettings settings, DeviceJoin? join)
    {
        if (settings.TenantId.Value is { } forced)
        {
            return new(forced, $"forced by {settings.TenantId.Source}");
        }
        if (join is not null)
        {
            return new(join.TenantId, $"device {join.Kind} to {join.TenantName ?? join.TenantId}");
        }
        return new(AnyOrganization, "not detected, any work account");
    }
}
