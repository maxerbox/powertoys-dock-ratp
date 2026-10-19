using System.Text.Json;

namespace RatpDock;

/// <summary>
/// Persistent, ordered list of saved routes, stored as JSON under
/// <c>%USERPROFILE%\.ratpdock\routes.json</c>. The file deliberately lives
/// OUTSIDE <c>%LOCALAPPDATA%</c>: inside an MSIX package, AppData writes are
/// virtualized into the package's private LocalCache folder, which is deleted
/// whenever the package is uninstalled (Remove-AppxPackage before reinstalling
/// an update) — that would silently wipe the routes on every version bump.
/// </summary>
internal sealed class RoutesStore
{
    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ratpdock");

    private static readonly string StorePath = Path.Combine(StoreDir, "routes.json");

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private List<SavedRoute> _routes = [];

    /// <summary>Raised whenever a route is added, edited or removed.</summary>
    public event EventHandler? RoutesChanged;

    public RoutesStore()
    {
        Load();
    }

    public IReadOnlyList<SavedRoute> Routes
    {
        get
        {
            lock (_lock)
            {
                return [.. _routes];
            }
        }
    }

    /// <summary>Adds a route, or replaces the existing route with the same id.</summary>
    public void Upsert(SavedRoute route)
    {
        lock (_lock)
        {
            var index = _routes.FindIndex(r => r.Id == route.Id);
            if (index >= 0)
            {
                _routes[index] = route;
            }
            else
            {
                _routes.Add(route);
            }

            Save();
        }

        RoutesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_routes.RemoveAll(r => r.Id == id) == 0)
            {
                return;
            }

            Save();
        }

        RoutesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        lock (_lock)
        {
            if (File.Exists(StorePath))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<List<SavedRoute>>(File.ReadAllText(StorePath), JsonOpts);
                    if (loaded is not null)
                    {
                        _routes = loaded;
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // fall through to the empty list
                }
            }

            _routes = [];
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(StoreDir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(_routes, JsonOpts));
    }
}
