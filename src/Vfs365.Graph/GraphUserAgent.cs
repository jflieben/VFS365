namespace Vfs365.Graph;

/// <summary>User-Agent for all Graph and SharePoint traffic. SharePoint prioritizes traffic decorated as ISV|Company|App/Version.</summary>
public static class GraphUserAgent
{
    public static string Value { get; } = $"ISV|JSolve|VFS365/{typeof(GraphUserAgent).Assembly.GetName().Version!.ToString(3)}";
}
