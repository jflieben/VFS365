using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// Throttled responses grouped into episodes for monitoring and the log: an episode starts with a throttled response and ends when
/// nothing was throttled for <see cref="Quiet"/> after its last pause ended. A response after that starts a new episode even when
/// nobody asked for the old one yet (a late tick after sleep).
/// </summary>
public sealed class ThrottleEpisodes
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);

    public sealed class Episode
    {
        public DateTimeOffset From { get; init; }

        /// <summary>When the last pause ended: requests were held back from <see cref="From"/> until then.</summary>
        public DateTimeOffset Until { get; set; }
        public int Responses { get; set; }
        public int Status429 { get; set; }
        public int Status503 { get; set; }
        public int Graph { get; set; }
        public int SharePoint { get; set; }
        public int Background { get; set; }
        public TimeSpan LongestPause { get; set; }

        /// <summary>The pause every request had, as Microsoft asked (Retry-After); background work waits ThrottlePauseMinutes on top, not counted here.</summary>
        public TimeSpan Held => Until - From;

        public string Describe()
        {
            var statuses = string.Join(", ", new[] { (429, Status429), (503, Status503) }.Where(s => s.Item2 > 0).Select(s => $"{s.Item1} x{s.Item2}"));
            return $"Microsoft throttled VFS365 for {Span(Held)} (the pause it asked for; background work waits longer): {Responses} response(s) ({statuses}); " +
                $"SharePoint {SharePoint}, Graph {Graph}; background work {Background}, user actions {Responses - Background}; longest wait {Span(LongestPause)}.";
        }

        static string Span(TimeSpan span) => span < TimeSpan.FromSeconds(90) ? $"{Math.Max(1, (int)span.TotalSeconds)} s" : $"{(int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero)} min";
    }

    readonly Lock gate = new();
    readonly List<Episode> ended = [];
    Episode? open;

    public void Add(ThrottleNotice notice)
    {
        lock (gate)
        {
            if (open is not null && notice.At >= open.Until + Quiet)
            {
                ended.Add(open);
                open = null;
            }
            open ??= new Episode { From = notice.At, Until = notice.At };
            open.Responses++;
            if (notice.Status == 429)
            {
                open.Status429++;
            }
            else
            {
                open.Status503++;
            }
            if (notice.Graph)
            {
                open.Graph++;
            }
            else
            {
                open.SharePoint++;
            }
            if (notice.Background)
            {
                open.Background++;
            }
            if (notice.Pause > open.LongestPause)
            {
                open.LongestPause = notice.Pause;
            }
            if (notice.At + notice.Pause > open.Until)
            {
                open.Until = notice.At + notice.Pause;
            }
        }
    }

    /// <summary>Episodes that ended by <paramref name="now"/>, each once, oldest first; MaxValue ends the one still going on (a stop).</summary>
    public IReadOnlyList<Episode> Ended(DateTimeOffset now)
    {
        lock (gate)
        {
            if (open is not null && (now == DateTimeOffset.MaxValue || now >= open.Until + Quiet))
            {
                ended.Add(open);
                open = null;
            }
            if (ended.Count == 0)
            {
                return [];
            }
            var episodes = ended.ToList();
            ended.Clear();
            return episodes;
        }
    }
}
