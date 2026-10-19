using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace RatpDock;

/// <summary>Lists all saved journeys with add/edit/delete — the "Manage" page.</summary>
internal sealed partial class RoutesPage : ListPage
{
    private readonly RoutesStore _store;

    public RoutesPage(RoutesStore store)
    {
        _store = store;
        Title = "Manage journeys";
        Name = "Manage";
        Id = "com.ratpdock.routespage";
        Icon = new IconInfo("\uE71D"); // List

        _store.RoutesChanged += (_, _) => UiThread.Post(() => RaiseItemsChanged());
    }

    public override IListItem[] GetItems()
    {
        var routes = _store.Routes;

        if (routes.Count == 0)
        {
            return
            [
                new ListItem(new RouteFormPage(_store))
                {
                    Title = "No saved journeys yet",
                    Subtitle = "Add your first transit journey",
                    Icon = new IconInfo("\uE710"), // Add
                },
            ];
        }

        return
        [
            .. routes.Select(route => new ListItem(new RouteFormPage(_store, route))
            {
                Title = route.Name,
                Subtitle = $"{route.From} → {route.To}",
                Icon = new IconInfo("\uE70F"), // Edit
                MoreCommands =
                [
                    new CommandContextItem(new DeleteRouteCommand(_store, route))
                    {
                        Title = "Delete journey",
                        Icon = new IconInfo("\uE74D"), // Delete
                    },
                ],
            }),
            new ListItem(new RouteFormPage(_store))
            {
                Title = "Add a journey",
                Subtitle = "Name, from address, to address",
                Icon = new IconInfo("\uE710"), // Add
            },
        ];
    }
}

internal sealed partial class DeleteRouteCommand : InvokableCommand
{
    private readonly RoutesStore _store;
    private readonly SavedRoute _route;

    public DeleteRouteCommand(RoutesStore store, SavedRoute route)
    {
        _store = store;
        _route = route;
        Name = $"Delete {_route.Name}";
        Icon = new IconInfo("\uE74D"); // Delete
    }

    public override CommandResult Invoke()
    {
        _store.Remove(_route.Id);
        new ToastStatusMessage($"Deleted journey '{_route.Name}'").Show();
        return CommandResult.KeepOpen();
    }
}
