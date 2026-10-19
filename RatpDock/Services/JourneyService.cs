using System.Collections.Concurrent;

namespace RatpDock;

/// <summary>
/// Polls the Bonjour RATP itinerary planner (the real bff.bonjour-ratp.fr
/// backend used by the official web app) for every saved route on a timer
/// (default: every 5 minutes) and exposes the latest statuses. Falls back to
/// the keyless Transitous planner if the Bonjour RATP backend ever rejects the
/// client. Mirrors the GoogleTrafficDock TrafficService design.
/// </summary>
internal sealed class JourneyService : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly RoutesStore _store;
    private readonly BonjourRatpClient _client = new();
    private readonly TransitousClient _fallbackClient = new();
    private readonly JourneyHistoryStore _history = new();
    private readonly ConcurrentDictionary<string, JourneyStatus> _statuses = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;

    /// <summary>Raised whenever new journey data has been fetched.</summary>
    public event EventHandler? StatusesChanged;

    public JourneyService(SettingsManager settings, RoutesStore store)
    {
        _settings = settings;
        _store = store;

        _store.RoutesChanged += (_, _) => RefreshNow();
        _settings.SettingsUpdated += (_, _) =>
        {
            ResetTimer();
            RefreshNow();
        };

        ResetTimer();
        RefreshNow();
    }

    public bool HasRoutes => _store.Routes.Count > 0;

    /// <summary>Recorded journey durations, for the details-pane chart.</summary>
    public JourneyHistoryStore History => _history;

    public IReadOnlyList<JourneyStatus> Statuses => [.. _statuses.Values];

    /// <summary>Triggers an immediate refresh of all routes (fire-and-forget, deduplicated).</summary>
    public void RefreshNow()
    {
        if (!_refreshLock.Wait(0))
        {
            return; // a refresh is already in flight
        }

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var route in _store.Routes)
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    var status = await GetJourneyAsync(route, cts.Token);
                    _statuses[route.Id] = status;

                    // Record successful durations so the details pane can
                    // chart "duration by time of day".
                    if (status.Error is null && status.DurationMinutes is { } recordedMinutes)
                    {
                        _history.Record(route.Id, recordedMinutes);
                    }

                    StatusesChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (ObjectDisposedException)
            {
                // extension shutting down
            }
            finally
            {
                _refreshLock.Release();
            }
        });
    }

    /// <summary>Fetches a journey from the Bonjour RATP backend, falling back to Transitous on failure.</summary>
    private async Task<JourneyStatus> GetJourneyAsync(SavedRoute route, CancellationToken cancellationToken)
    {
        var status = await _client.GetJourneyAsync(route, cancellationToken);
        if (status.Error is not null)
        {
            // The Bonjour RATP BFF is Cloudflare-gated; if it ever blocks this
            // client again, keep the dock alive on the keyless Transitous feed.
            var fallback = await _fallbackClient.GetJourneyAsync(route, cancellationToken);
            if (fallback.Error is null)
            {
                return fallback;
            }
        }

        return status;
    }

    private void ResetTimer()
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshMinutes));
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => RefreshNow(), null, interval, interval);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _cts?.Dispose();
        _refreshLock.Dispose();
        _client.Dispose();
        _fallbackClient.Dispose();
    }
}
