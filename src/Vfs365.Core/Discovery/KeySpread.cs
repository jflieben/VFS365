namespace Vfs365.Core.Discovery;

/// <summary>
/// A fixed share (0 up to 1) per key (FNV-1a over the lowercased key). Jitter taken from it is the same in every run, so first-read
/// times spread over a period without a random number to keep. A final mix (Murmur3's) so keys that differ only in their last
/// characters don't land next to each other.
/// </summary>
static class KeySpread
{
    public static double Share(string key)
    {
        var hash = 2166136261u;
        foreach (var c in key.ToLowerInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }
        hash ^= hash >> 16;
        hash *= 0x85ebca6bu;
        hash ^= hash >> 13;
        hash *= 0xc2b2ae35u;
        hash ^= hash >> 16;
        return hash / 4294967296.0;
    }
}
