using System.Text.Json;

namespace RatpDock;

/// <summary>
/// Records one duration sample per successful journey poll (throttled to one
/// per minute per route) and persists them to
/// <c>%USERPROFILE%\.ratpdock\history.json</c>. The palette details pane
/// charts this series ("duration by time of day") so the user can see when
/// it is best to leave. The store deliberately lives outside the
/// MSIX-virtualized AppData — same reasoning as <see cref="RoutesStore"/>.
/// </summary>
internal sealed class JourneyHistoryStore
{
    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ratpdock");

    private static readonly string StorePath = Path.Combine(StoreDir, "history.json");

    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly object _lock = new();
    private List<Sample> _samples = [];

    /// <summary>One recorded journey duration at a point in time.</summary>
    internal sealed record Sample(string RouteId, DateTime Time, int Minutes);

    public JourneyHistoryStore()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var loaded = JsonSerializer.Deserialize<List<Sample>>(File.ReadAllText(StorePath), JsonOpts);
                if (loaded is not null)
                {
                    _samples = loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _samples = [];
        }
    }

    /// <summary>Records a successful journey duration (at most one sample per minute per route).</summary>
    public void Record(string routeId, int minutes)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            if (_samples.Any(s => s.RouteId == routeId && (now - s.Time).TotalMinutes < 1))
            {
                return;
            }

            _samples.Add(new Sample(routeId, now, minutes));

            // Prune past retention, then persist on every sample: the write
            // is a few KB once per refresh interval, and a checkpoint scheme
            // (write only every N samples) makes the file's timestamp a
            // misleading freshness signal and loses data on a crash.
            _samples.RemoveAll(s => s.Time < now - Retention);
            Save();
        }
    }

    /// <summary>All retained samples for a route, oldest first.</summary>
    public IReadOnlyList<Sample> Samples(string routeId)
    {
        lock (_lock)
        {
            return _samples.Where(s => s.RouteId == routeId).OrderBy(s => s.Time).ToList();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_samples, JsonOpts));
        }
        catch (IOException)
        {
            // best effort — the next successful poll will retry
        }
    }
}
