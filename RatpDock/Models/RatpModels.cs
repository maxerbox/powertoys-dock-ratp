namespace RatpDock;

using System.Globalization;

internal sealed class SavedRoute
{
    /// <summary>Stable identity of the route (survives renaming).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string Name { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>Google Maps transit itinerary (closest clickable equivalent of the Bonjour RATP planner).</summary>
    public string MapsUrl =>
        $"https://www.google.com/maps/dir/?api=1&origin={Uri.EscapeDataString(From)}&destination={Uri.EscapeDataString(To)}&travelmode=transit";
}

/// <summary>A resolved place (geocode result) used to deep-link the Bonjour RATP planner.</summary>
internal sealed record GeoPlace(string? Id, string Type, double Lat, double Lon, string Name);

/// <summary>One transit leg of a journey (bus/metro/RER/tram); walking legs are summarized instead.</summary>
internal sealed record TransitLeg
{
    public required string Mode { get; init; }

    /// <summary>Line badge text, e.g. "M9", "B", "9160".</summary>
    public required string Line { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>Departure time of the leg (real-time when the feed provides it).</summary>
    public DateTimeOffset Departure { get; init; }

    public bool RealTime { get; init; }

    /// <summary>Official line color (hex, e.g. "#87D3DF") when the data source provides it.</summary>
    public string? BadgeColor { get; init; }

    /// <summary>True when the badge color needs dark text (light background).</summary>
    public bool? BadgeDarkText { get; init; }
}

/// <summary>A line badge for the RATP board: label plus optional official color.</summary>
internal sealed record LineBadge(string Label, string? BackgroundHex, bool? DarkText);

internal sealed record JourneyStatus
{
    public required SavedRoute Route { get; init; }

    /// <summary>Total door-to-door duration in minutes, if the API call succeeded.</summary>
    public int? DurationMinutes { get; init; }

    /// <summary>Departure time of the first transit leg (null when the whole journey is walking).</summary>
    public DateTimeOffset? Departure { get; init; }

    /// <summary>Arrival time of the journey.</summary>
    public DateTimeOffset? Arrival { get; init; }

    /// <summary>Transit legs in order (walking legs omitted).</summary>
    public IReadOnlyList<TransitLeg> Legs { get; init; } = [];

    public string? Error { get; init; }

    /// <summary>Geocoded origin, when the data source resolved it (enables Bonjour RATP deep-links).</summary>
    public GeoPlace? OriginPlace { get; init; }

    /// <summary>Geocoded destination, when the data source resolved it.</summary>
    public GeoPlace? DestinationPlace { get; init; }

    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Compact duration for the Dock band title, e.g. "47m".</summary>
    public string DurationText => Error is not null
        ? "⚠"
        : DurationMinutes is { } m ? $"{m}m" : "…";

    /// <summary>Line badge texts for the RATP board, e.g. ["9160", "4606"].</summary>
    public IReadOnlyList<string> Lines => Legs.Select(l => l.Line).Where(l => l.Length > 0).ToList();

    /// <summary>Line badges with official colors (when the data source provides them).</summary>
    public IReadOnlyList<LineBadge> LineBadges =>
        Legs.Where(l => l.Line.Length > 0)
            .Select(l => new LineBadge(l.Line, l.BadgeColor, l.BadgeDarkText))
            .ToList();

    public string Subtitle
    {
        get
        {
            if (Error is not null)
            {
                return $"⚠ {Error}";
            }

            if (DurationMinutes is not { } minutes)
            {
                return "…";
            }

            var local = UpdatedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

            if (Legs.Count == 0)
            {
                return $"🚶 {minutes} min on foot · {local}";
            }

            var dep = Departure?.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            var arr = Arrival?.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            var legs = string.Join(" → ", Legs.Select(l => $"{l.Line} {l.Departure.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)}"));
            return $"{legs} · {minutes} min · {dep}–{arr} · {local}";
        }
    }

    /// <summary>
    /// Clickable itinerary: opens the exact search on the Bonjour RATP site when
    /// the geocoded places are known (fragment format #name|TYPE|lng,lat|id/…),
    /// otherwise falls back to Google Maps transit.
    /// </summary>
    public string ItineraryUrl => OriginPlace is { } o && DestinationPlace is { } d
        ? BonjourRatpUrl(o, d)
        : Route.MapsUrl;

    private static string BonjourRatpUrl(GeoPlace origin, GeoPlace destination) =>
        "https://www.bonjour-ratp.fr/itineraires/#" + Fragment(origin) + "/" + Fragment(destination);

    private static string Fragment(GeoPlace place)
    {
        var lng = place.Lon.ToString("0.######", CultureInfo.InvariantCulture);
        var lat = place.Lat.ToString("0.######", CultureInfo.InvariantCulture);
        var fragment = Uri.EscapeDataString(place.Name) + "%7C" + place.Type + "%7C" + Uri.EscapeDataString(lng + "," + lat);
        return string.IsNullOrEmpty(place.Id) ? fragment : fragment + "%7C" + Uri.EscapeDataString(place.Id);
    }
}
