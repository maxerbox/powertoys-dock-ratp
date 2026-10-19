using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace RatpDock;

public partial class RatpDockCommandsProvider : CommandProvider
{
    private readonly SettingsManager _settingsManager = new();
    private readonly RoutesStore _routesStore;
    private readonly JourneyService _journeyService;
    private readonly JourneyPage _journeyPage;
    private readonly JourneyDockPage _journeyDockPage;
    private readonly ICommandItem[] _commands;

    public RatpDockCommandsProvider()
    {
        DisplayName = "RATP Journeys";
        Id = "com.ratpdock";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        _routesStore = new RoutesStore();
        _journeyService = new JourneyService(_settingsManager, _routesStore);
        _journeyPage = new JourneyPage(_journeyService, _settingsManager, _routesStore);
        _journeyDockPage = new JourneyDockPage(_journeyService, _settingsManager, _routesStore);

        _commands =
        [
            new CommandItem(_journeyPage)
            {
                // JourneyPage.Id matches JourneyDockPage.Id (see GetDockBands):
                // when the user pins this top-level command, the palette
                // reuses the dock band instead of creating a second RATP entry.
                Title = "RATP Journeys",
                Subtitle = "Live transit journey times for your saved routes",
                MoreCommands =
                [
                    new CommandContextItem(new RoutesPage(_routesStore))
                    {
                        Title = "Manage journeys",
                        Icon = new IconInfo("\uE71D"), // List
                    },
                    new CommandContextItem(new RouteFormPage(_routesStore))
                    {
                        Title = "Add a journey",
                        Icon = new IconInfo("\uE710"), // Add
                    },
                    new CommandContextItem(_settingsManager.SettingsPage)
                    {
                        Title = "Settings",
                        Icon = new IconInfo("\uE713"), // Settings
                    },
                    new CommandContextItem(new RefreshCommand(_journeyService))
                    {
                        Title = "Refresh now",
                        Icon = new IconInfo("\uE72C"), // Refresh
                    },
                ],
            },
        ];

        Settings = _settingsManager.Settings;
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    /// <summary>
    /// Exposes the saved journeys as a dock band whose buttons show a mini
    /// RATP display board (amber time on black) as their icon.
    /// </summary>
    public override ICommandItem[]? GetDockBands()
    {
        return
        [
            new CommandItem(_journeyDockPage)
            {
                // Id matches the top-level JourneyPage — pinning the top-level
                // command reuses this band instead of adding a second entry.
                Title = "RATP Journeys",
            },
        ];
    }
}
