namespace Vfs365.Core;

/// <summary>
/// Marks work nobody is waiting for (change feed reads, folders loaded ahead, refreshes, discovery), so a request budget can serve
/// what users wait for first.
/// </summary>
public static class RequestPriority
{
    static readonly AsyncLocal<bool> background = new();

    public static bool IsBackground => background.Value;

    /// <summary>Marks the rest of the calling async method, and what it calls, as background. Only call it from async methods.</summary>
    public static void MarkBackground() => background.Value = true;
}
