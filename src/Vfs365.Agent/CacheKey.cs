using System.Security.Cryptography;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>
/// The per-user key for cached content and metadata: 32 random bytes in %LOCALAPPDATA%\VFS365\cache.key, protected with DPAPI so
/// only this Windows user can unwrap it. Losing it (new profile, deleted file) only means the cache is downloaded again.
/// </summary>
static class CacheKey
{
    static readonly byte[] Entropy = "VFS365 cache key"u8.ToArray();

    public static string FilePath { get; } = Path.Combine(DiscoveryStore.DataDirectory, "cache.key");

    public static byte[] LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            }
        }
        catch (CryptographicException)
        {
        }
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(DiscoveryStore.DataDirectory);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        return key;
    }

    /// <summary>AES-GCM: nonce (12) | tag (16) | ciphertext.</summary>
    public static byte[] Seal(byte[] key, byte[] plain)
    {
        var sealedData = new byte[28 + plain.Length];
        RandomNumberGenerator.Fill(sealedData.AsSpan(0, 12));
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(sealedData.AsSpan(0, 12), plain, sealedData.AsSpan(28), sealedData.AsSpan(12, 16));
        return sealedData;
    }

    /// <summary>Null when the data was sealed with another key or changed.</summary>
    public static byte[]? Unseal(byte[] key, byte[] sealedData)
    {
        if (sealedData.Length < 28)
        {
            return null;
        }
        var plain = new byte[sealedData.Length - 28];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), plain);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }
}

/// <summary>Device enrollment in Intune (MDM), to wipe the cache when a device leaves management.</summary>
static class DeviceManagement
{
    static string Marker => Path.Combine(DiscoveryStore.DataDirectory, "enrolled");

    public static bool IsMdmEnrolled()
    {
        using var enrollments = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
        if (enrollments is null)
        {
            return false;
        }
        foreach (var name in enrollments.GetSubKeyNames())
        {
            using var enrollment = enrollments.OpenSubKey(name);
            if (enrollment?.GetValue("ProviderID") as string == "MS DM Server" && enrollment.GetValue("EnrollmentState") is int state && state == 1)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True once when the device was enrolled at the last start and isn't now; records the current state.</summary>
    public static bool LeftManagement()
    {
        var enrolled = IsMdmEnrolled();
        var was = File.Exists(Marker);
        if (enrolled && !was)
        {
            Directory.CreateDirectory(DiscoveryStore.DataDirectory);
            File.WriteAllText(Marker, DateTimeOffset.UtcNow.ToString("O"));
        }
        else if (!enrolled && was)
        {
            File.Delete(Marker);
        }
        return was && !enrolled;
    }
}
