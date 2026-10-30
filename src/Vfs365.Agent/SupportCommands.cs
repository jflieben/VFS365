using Vfs365.Core.Discovery;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>The helpdesk and admin commands: `vfs365 health`, `vfs365 support-bundle` and `vfs365 why`.</summary>
static class SupportCommands
{
    public static async Task<int> RunAsync(string command, string[] args, Action<string> say, TextWriter error)
    {
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);
        try
        {
            switch (command)
            {
                case "health":
                    if (args.Skip(1).Any(a => a is not ("--json" or "--all-users")))
                    {
                        error.WriteLine("Usage: vfs365 health [--json] [--all-users]");
                        return 2;
                    }
                    if (Flag("--all-users") && !Health.Privileged())
                    {
                        error.WriteLine("vfs365 health --all-users reads every signed-in user's profile: run it as SYSTEM or as an elevated administrator.");
                        return 2;
                    }
                    var report = Flag("--all-users") ? Health.AllUsers(DateTimeOffset.UtcNow) : Health.CurrentUser(DateTimeOffset.UtcNow);
                    if (Flag("--json"))
                    {
                        say(report.ToJson());
                    }
                    else
                    {
                        foreach (var line in report.ToText())
                        {
                            say(line);
                        }
                    }
                    return report.Healthy ? 0 : 1;

                case "support-bundle":
                    var folder = Option("--out") ?? SupportBundle.LocalFolder();
                    if (args.Skip(1).Any(a => a.StartsWith("--", StringComparison.Ordinal) && a is not ("--out" or "--config")) || Flag("--out") && Option("--out") is null)
                    {
                        error.WriteLine("Usage: vfs365 support-bundle [--out <folder>]");
                        return 2;
                    }
                    if (SupportBundle.Refusal(folder, StatusFile.Read(StatusFile.DefaultPath)?.DriveLetter) is { } refused)
                    {
                        error.WriteLine(refused);
                        return 2;
                    }
                    var zip = new SupportBundle(DiscoveryStore.DataDirectory, () => AgentSettings.Load(Option("--config"))).Create(folder);
                    say($"Saved:      {zip}");
                    say("            Tokens and secrets are removed; nothing was uploaded. Send the file to your helpdesk.");
                    SupportBundle.Show(zip);
                    return 0;

                default:
                    if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
                    {
                        error.WriteLine("Usage: vfs365 why <browser link of a site, library, folder or file, or part of a name> [--check]");
                        return 2;
                    }
                    return await WhyAsync(args[1], Option("--config"), Flag("--check"), say);
            }
        }
        catch (Exception e) when (e is SignInException or M365RequestException or HttpRequestException or FileNotFoundException or System.Text.Json.JsonException
            or IOException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine(e.Message);
            return 1;
        }
    }

    static async Task<int> WhyAsync(string query, string? configPath, bool check, Action<string> say)
    {
        var settings = AgentSettings.Load(configPath);
        var state = DiscoveryStore.Load();
        var effective = settings.Effective();
        var context = new WhyContext(AgentCommands.DiscoveryOptionsOf(settings), settings.PinnedLocations, UserPins.Load(), settings.Scope, state,
            DiscoveryReport.Load(), StatusFile.Read(StatusFile.DefaultPath)?.DriveLetter ?? $@"\{AgentCommands.UncPrefix(Environment.UserName)}",
            name => effective.FirstOrDefault(s => s.Name == name).Source ?? AgentSettings.Default, settings.SharedWithMe);
        var answer = LocationExplainer.Explain(query, context);
        foreach (var line in answer.Lines)
        {
            say(line);
        }
        if (!check)
        {
            return answer.Shown ? 0 : 1;
        }
        if (!Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            say("Check:      --check needs a browser link (https://...)");
            return answer.Shown ? 0 : 1;
        }

        // A few delegated calls: the location itself, then search below its web
        var tokens = new WamTokenSource(settings.ClientId.Value!, TenantResolver.Resolve(settings, DeviceJoin.Read()).Tenant) { PromptWhenNeeded = true };
        using var http = new HttpClient(M365Client.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new M365Client(http, tokens);
        var api = new SharePointDiscoveryApi(client);
        var tenantRoot = state?.TenantRoot;
        if (tenantRoot is null)
        {
            tenantRoot = (await api.GetMyHomeAsync(default)).TenantRoot;
        }
        client.TenantRootHost ??= new Uri(tenantRoot).Host;
        var location = $"https://{uri.Host}{LocationExplainer.SharePointPath(uri)}";
        foreach (var line in await LocationExplainer.CheckAsync(location, api.ResolveLocationAsync, (web, ct) => api.SearchWebLibrariesAsync(tenantRoot, web, ct), default))
        {
            say(line);
        }
        say($"Requests:   Graph {client.GraphRequests}, SharePoint {client.SharePointRequests}");
        return answer.Shown ? 0 : 1;
    }
}
