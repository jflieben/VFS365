namespace Vfs365.Core;

/// <summary>
/// Marks work nobody is waiting for (change feed reads, folders loaded ahead, refreshes, discovery), so a request budget can serve
/// what users wait for first. The mark is a <see cref="PriorityHolder"/> that flows with the async call; work that a user joins later
/// (a download read ahead that is then opened) can be promoted while its requests wait.
/// </summary>
public static class RequestPriority
{
    static readonly AsyncLocal<PriorityHolder?> current = new();

    public static bool IsBackground => current.Value?.IsBackground ?? false;

    /// <summary>The holder of the calling async flow; null for foreground work.</summary>
    public static PriorityHolder? Current => current.Value;

    /// <summary>Marks the rest of the calling async method, and what it calls, as background. Only call it from async methods.</summary>
    public static void MarkBackground()
    {
        if (current.Value is not { IsBackground: true })
        {
            current.Value = new PriorityHolder();
        }
    }

    /// <summary>Runs the rest of the calling async method, and what it calls, at <paramref name="holder"/>'s priority. Only call it from async methods.</summary>
    public static void Use(PriorityHolder holder) => current.Value = holder;
}

/// <summary>A priority shared by the requests of one piece of work. Starts as background; <see cref="Promote"/> makes it foreground for good.</summary>
public sealed class PriorityHolder
{
    readonly TaskCompletionSource promoted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PriorityHolder(bool background = true)
    {
        if (!background)
        {
            promoted.TrySetResult();
        }
    }

    public bool IsBackground => !promoted.Task.IsCompleted;

    /// <summary>Completes when the work is promoted; requests waiting at background priority use it to stop waiting.</summary>
    public Task Promoted => promoted.Task;

    public void Promote() => promoted.TrySetResult();
}
