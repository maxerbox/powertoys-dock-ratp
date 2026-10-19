using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace RatpDock;

/// <summary>
/// The page rendered inside the Command Palette Dock band. The dock only
/// renders Title + Icon, so the RATP board (amber dot-matrix time on black)
/// is rendered as the item's icon while the title stays the bare route name
/// (the time already lives in the board). Items are persistent <see cref="ListItem"/>
/// instances mutated in place — same live-update technique as the Clock and
/// GoogleTraffic dock extensions — and Icon/Command are only reassigned when
/// the state actually changes, to avoid dock flicker.
/// </summary>
internal sealed partial class JourneyDockPage : ListPage
{
    private readonly JourneyService _service;
    private readonly SettingsManager _settings;
    private readonly RoutesStore _store;

    private const int MaxNameLength = 10;

    private readonly Dictionary<string, DockEntry> _dockItems = [];

    /// <summary>Serializes item mutations between the fetch thread and host RPC threads.</summary>
    private readonly object _gate = new();

    /// <summary>Latest rendered items; what GetItems hands to the host.</summary>
    private IListItem[]? _snapshot;

    /// <summary>A dock item plus the last state it was rendered with, so we only
    /// touch Icon/Command when something actually changed.</summary>
    private sealed class DockEntry
    {
        public required ListItem Item { get; init; }

        public bool OpenOnActivate { get; set; } = true;

        public string? VisualKey { get; set; }

        /// <summary>Last itinerary URL the commands were built with.</summary>
        public string? Url { get; set; }
    }

    public JourneyDockPage(JourneyService service, SettingsManager settings, RoutesStore store)
    {
        _service = service;
        _settings = settings;
        _store = store;
        Title = "RATP Journeys";
        Name = "Open";
        // Same Id as JourneyPage: a top-level command and a dock band sharing
        // an Id makes the palette reuse the band when the command is pinned,
        // instead of showing two RATP entries.
        Id = "com.ratpdock.journeys";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        // StatusesChanged fires on the fetch threadpool thread; GetItems
        // arrives on the host's COM RPC threads. All mutations are serialized
        // by _gate and the fetch thread only POSTS updates — GetItems must
        // NEVER block on the page thread: RaiseItemsChanged is a synchronous
        // COM call into the host, and the host re-enters GetItems from that
        // event, so a GetItems that waits for the thread raising the event
        // deadlocks (dock band stuck on "loading...").
        _service.StatusesChanged += (_, _) => UiThread.Post(UpdateItems);
        _store.RoutesChanged += (_, _) => UiThread.Post(UpdateItems);
    }

    public override IListItem[] GetItems()
    {
        lock (_gate)
        {
            _snapshot ??= BuildItems();
            return _snapshot;
        }
    }

    /// <summary>Page-thread entry: rebuild under the lock, then notify (outside it).</summary>
    private void UpdateItems()
    {
        lock (_gate)
        {
            _snapshot = BuildItems();
        }

        RaiseItemsChanged();
    }

    private IListItem[] BuildItems()
    {
        var routes = _store.Routes;

        // Drop items for deleted routes.
        foreach (var staleId in _dockItems.Keys.Where(id => routes.All(r => r.Id != id)).ToArray())
        {
            _dockItems.Remove(staleId);
        }

        var statuses = _service.Statuses;
        var openOnActivate = _settings.OpenOnActivate;

        foreach (var route in routes)
        {
            if (!_dockItems.TryGetValue(route.Id, out var entry))
            {
                entry = new DockEntry
                {
                    Item = new ListItem(new OpenItineraryCommand(route.MapsUrl, "Open itinerary"))
                    {
                        // No "Open itinerary" in MoreCommands here: the palette
                        // already lists the item's main command first in the
                        // right-click menu — duplicating it shows two buttons.
                        MoreCommands = BuildMoreCommands(route, route.MapsUrl, includeOpen: false),
                    },
                };
                _dockItems[route.Id] = entry;
            }

            var item = entry.Item;
            var status = statuses.FirstOrDefault(s => s.Route.Id == route.Id);

// The dock clips long labels, so keep the title compact — just the
        // route name: the journey time already lives in the SIEL board icon.
        item.Title = status is null
            ? Abbreviate(route.Name)
            : status.Error is not null
                ? $"{Abbreviate(route.Name)} ⚠"
                : Abbreviate(route.Name);

            item.Subtitle = status?.Subtitle ?? $"{route.From} → {route.To}";

            // The RATP board icon is cached per visual state internally; only
            // reassign when that state actually changed (avoids flicker).
            var visualKey = status is null ? null : $"{status.DurationText}|{string.Join(",", status.Lines)}";
            if (entry.VisualKey != visualKey)
            {
                entry.VisualKey = visualKey;
                item.Icon = status is null
                    ? IconHelpers.FromRelativePath("Assets\\StoreLogo.png")
                    : RatpDisplayRenderer.DockIcon(status);
            }

            // The itinerary URL depends on the geocoded places, which resolve
            // with the first status — rebuild the commands when it changes.
            var url = status?.ItineraryUrl ?? route.MapsUrl;
            if (entry.Url != url)
            {
                entry.Url = url;
                item.MoreCommands = BuildMoreCommands(route, url, includeOpen: !openOnActivate);
                if (entry.OpenOnActivate)
                {
                    item.Command = new OpenItineraryCommand(url);
                }
            }

            if (entry.OpenOnActivate != openOnActivate)
            {
                entry.OpenOnActivate = openOnActivate;
                item.Command = openOnActivate
                    ? new OpenItineraryCommand(url)
                    : new RefreshCommand(_service);
                item.MoreCommands = BuildMoreCommands(route, url, includeOpen: !openOnActivate);
            }
        }

        return [.. _dockItems.Values.Select(e => e.Item)];
    }

    /// <summary>
    /// Context menu for a dock item. The palette always shows the item's main
    /// command first in the right-click menu, so the "Open itinerary" entry
    /// is only added when the main command is something else
    /// (<paramref name="includeOpen"/>) — otherwise it would appear twice.
    /// </summary>
    private ICommandContextItem[] BuildMoreCommands(SavedRoute route, string url, bool includeOpen) =>
    [
        .. (includeOpen
            ?
            [
                new CommandContextItem(new OpenItineraryCommand(url))
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
        new CommandContextItem(new RouteFormPage(_store, route))
        {
            Title = "Edit journey",
            Icon = new IconInfo("\uE70F"), // Edit
        },
        new CommandContextItem(new DeleteRouteCommand(_store, route))
        {
            Title = "Delete journey",
            Icon = new IconInfo("\uE74D"), // Delete
        },
        new CommandContextItem(new RoutesPage(_store))
        {
            Title = "Manage journeys",
            Icon = new IconInfo("\uE71D"), // List
        },
    ];

    /// <summary>Shortens a route name so dock labels don't get ellipsed.</summary>
    private static string Abbreviate(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length <= MaxNameLength ? trimmed : $"{trimmed[..(MaxNameLength - 1)]}…";
    }
}
