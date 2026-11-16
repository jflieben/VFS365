namespace Vfs365.Agent;

/// <summary>
/// The tray's "Add a library by link" (what `vfs365 pin` does: the link goes in the user's pins and shows after a restart) and "Why is a
/// site missing?" (what `vfs365 why` prints, without --check). Each asks with the agent's small dialog; results and refusals are
/// notifications, the explanation a read-only window.
/// </summary>
/// <param name="ask">Title, prompt, button text, the suggested text (read on the dialog's thread) and the cancellation: the answer, or null.</param>
/// <param name="pin">Adds a link to the user's pins; false when it was there already.</param>
/// <param name="explain">The explanation's lines for a link or name.</param>
/// <param name="notify">Title, text and whether it is a problem.</param>
/// <param name="show">Title and text of the explanation window.</param>
sealed class LocationActions(Func<string, string, string, Func<string?>?, CancellationToken, Task<string?>> ask, Func<string, bool> pin,
    Func<string, IReadOnlyList<string>> explain, Action<string, string, bool> notify, Action<string, string> show, Action<string> log)
{
    public async Task AddLibraryAsync(CancellationToken ct)
    {
        var title = UiText.Get("AddLibrary.Title");
        if (await ask(title, UiText.Get("AddLibrary.Prompt"), UiText.Get("AddLibrary.Button"), ClipboardLink, ct) is not { } answer)
        {
            return;
        }
        if (UserPins.Parse(answer) is not { } location)
        {
            notify(title, UiText.Get("AddLibrary.Refused"), true);
            return;
        }
        try
        {
            var added = pin(location.ToString());
            log($"Tray: {(added ? "pinned" : "already pinned")} {location}");
            notify(title, UiText.Get(added ? "AddLibrary.Added" : "AddLibrary.Already", location), false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Tray: pin {location} failed: {e.Message}");
            notify(title, UiText.Get("AddLibrary.Failed", e.Message), true);
        }
    }

    public async Task WhyAsync(CancellationToken ct)
    {
        var title = UiText.Get("Why.Title");
        if (await ask(title, UiText.Get("Why.Prompt"), UiText.Get("Why.Button"), ClipboardLink, ct) is not { } query)
        {
            return;
        }
        try
        {
            show(title, string.Join("\r\n", explain(query.Trim())));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"Tray: why {query} failed: {e.Message}");
            notify(title, UiText.Get("Why.Failed", e.Message), true);
        }
    }

    /// <summary>A web address on the clipboard, as the suggested text; null when there is none. Needs an STA thread.</summary>
    internal static string? ClipboardLink()
    {
        var text = System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText().Trim() : null;
        return text is { Length: > 0 and <= AskWindow.MaxText } && UserPins.Parse(text) is not null ? text : null;
    }
}
