using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace RatpDock;

internal sealed partial class JourneyPage : ListPage
{
    private readonly JourneyService _service;
    private readonly SettingsManager _settings;
    private readonly RoutesStore _store;

    // Details (chart + stats) are rebuilt only when new history samples
    // arrive — GetItems runs on every palette open and poll completion.
    private readonly Dictionary<string, (string Key, Details Details)> _detailsCache = [];

    /// <summary>Serializes GetItems builds (details cache + chart render) across host RPC threads.</summary>
    private readonly object _gate = new();

    public JourneyPage(JourneyService service, SettingsManager settings, RoutesStore store)
    {
        _service = service;
        _settings = settings;
        _store = store;
        Title = "RATP Journeys";
        Name = "Open";
        // Same Id as JourneyDockPage — see the comment there.
        Id = "com.ratpdock.journeys";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        // Expands the details pane (duration-history chart + stats) for the
        // selected item — the "when should I leave" view.
        ShowDetails = true;

        // Raised from the fetch threadpool thread — posted to the page
        // thread so raises are serialized off the fetch pipeline. GetItems
        // itself never blocks on the page thread (see the note in
        // JourneyDockPage): the host re-enters GetItems from ItemsChanged,
        // so waiting for the raising thread would deadlock.
        _service.StatusesChanged += (_, _) => UiThread.Post(() => RaiseItemsChanged());
        _store.RoutesChanged += (_, _) => UiThread.Post(() => RaiseItemsChanged());
    }

    public override IListItem[] GetItems()
    {
        // The build touches the details cache and renders charts — serialize
        // concurrent host RPC threads, but never wait for the page thread.
        lock (_gate)
        {
            if (!_service.HasRoutes)
        {
            return
            [
                new ListItem(new RouteFormPage(_store))
                {
                    Title = "Add your first journey",
                    Subtitle = "Name, from address, to address — e.g. Paris to Versailles",
                    Icon = new IconInfo("\uE710"), // Add
                },
                new ListItem(_settings.SettingsPage)
                {
                    Title = "Open RATP Dock settings",
                    Subtitle = "Refresh rate and click behavior",
                    Icon = new IconInfo("\uE713"), // Settings
                },
            ];
        }

        return _service.Statuses
            .Select(CreateListItem)
            .ToArray();
        }
    }

    private ListItem CreateListItem(JourneyStatus status)
    {
        return new ListItem(
            _settings.OpenOnActivate
                ? new OpenItineraryCommand(status.ItineraryUrl)
                : new RefreshCommand(_service))
        {
            Title = status.Route.Name,
            Subtitle = status.Subtitle,
            Icon = RatpDisplayRenderer.PageIcon(status),
            Details = BuildDetails(status),
            // The palette already shows the item's main command first in the
            // right-click menu — only add an explicit "Open itinerary" entry
            // when the main command is NOT the open command, or it appears twice.
            MoreCommands =
            [
                .. (!_settings.OpenOnActivate
                    ?
                    [
                        new CommandContextItem(new OpenItineraryCommand(status.ItineraryUrl))
                        {
                            Title = "Open itinerary",
                            Icon = new IconInfo("\uE8A7"), // Map
                        },
                    ]
                    : Array.Empty<CommandContextItem>()),
                new CommandContextItem(new RefreshCommand(_service))
                {
                    Title = "Refresh journeys",
                    Icon = new IconInfo("\uE72C"), // Refresh
                },
                new CommandContextItem(new RouteFormPage(_store, status.Route))
                {
                    Title = "Edit journey",
                    Icon = new IconInfo("\uE70F"), // Edit
                },
                new CommandContextItem(new DeleteRouteCommand(_store, status.Route))
                {
                    Title = "Delete journey",
                    Icon = new IconInfo("\uE74D"), // Delete
                },
                new CommandContextItem(new RoutesPage(_store))
                {
                    Title = "Manage journeys",
                    Icon = new IconInfo("\uE71D"), // List
                },
            ],
        };
    }
    /// <summary>
    /// Details pane for a journey: duration-history chart (HeroImage) plus
    /// best/worst-time-of-day stats. Cached until new samples arrive.
    /// </summary>
    private Details BuildDetails(JourneyStatus status)
    {
        var samples = _service.History.Samples(status.Route.Id);
        var key = $"{samples.Count}|{status.DurationMinutes}";
        if (_detailsCache.TryGetValue(status.Route.Id, out var cached) && cached.Key == key)
        {
            return cached.Details;
        }

        var chartPath = JourneyChart.Render(status.Route.Id, samples);
        var stats = JourneyChart.Summarize(samples);

        var body = status.Error is not null
            ? $"⚠ {status.Error}"
            : $"**Now:** {status.DurationText}";

        if (stats is { SampleCount: >= 4 })
        {
            var current = stats.CurrentBucketAverage is { } avg
                ? $" — typically {avg} min at this hour"
                : string.Empty;
            body += $"{current}\n\n"
                + $"**Fastest window:** {stats.BestLabel} — avg {stats.BestAverage} min\n"
                + $"**Slowest window:** {stats.WorstLabel} — avg {stats.WorstAverage} min\n";
            if (stats.BestDayAverage is { } bestDay)
            {
                body += $"**Best day:** {stats.BestDayLabel} — avg {bestDay} min\n"
                    + $"**Worst day:** {stats.WorstDayLabel} — avg {stats.WorstDayAverage} min\n";
            }

            body += "\n"
                + $"{stats.SampleCount} samples over {stats.Days} day{(stats.Days == 1 ? "" : "s")} — "
                + "top chart: dots are recorded journeys with the 30-minute average; bottom chart: average per day of week.";
        }
        else if (status.Error is null)
        {
            body += "\n\n_Collecting history — the chart appears after a few polls._";
        }

        var details = new Details
        {
            Title = status.Route.Name,
            Body = body,
        };
        if (chartPath is not null)
        {
            details.HeroImage = new IconInfo(chartPath);
        }

        _detailsCache[status.Route.Id] = (key, details);
        return details;
    }
}

internal sealed partial class RefreshCommand : InvokableCommand
{
    private readonly JourneyService _service;

    public RefreshCommand(JourneyService service)
    {
        _service = service;
        Name = "Refresh journeys";
        Icon = new IconInfo("\uE72C"); // Refresh
    }

    public override CommandResult Invoke()
    {
        _service.RefreshNow();
        return CommandResult.KeepOpen();
    }
}
