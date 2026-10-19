using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RatpDock;

/// <summary>
/// Keyless transit journey client built on Transitous (https://api.transitous.org),
/// the community-run open journey planner that ingests the full Île-de-France
/// Mobilités feed (metro, RER, Transilien, tram, bus, Noctilien) including
/// real-time trip updates. This is the closest reproducible equivalent of the
/// itinerary calculator behind the Bonjour RATP app (whose Mappy backend is
/// private): same IDFM data, same real-time source.
/// Etiquette: a descriptive User-Agent is sent, and journeys are polled at most
/// once per refresh interval (default 5 minutes), well under the ~20 req/min limit.
/// </summary>
internal sealed partial class TransitousClient : IDisposable
{
    private readonly HttpClient _http;

    public TransitousClient()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RatpDock/1.0 (PowerToys Command Palette extension)");
    }

    [GeneratedRegex(@"^\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*$")]
    private static partial Regex CoordinateRegex();

    /// <summary>Resolves a "lat,lng" pair or a free-text address/POI to coordinates via the Transitous geocoder.</summary>
    public async Task<(double Lat, double Lon)?> GeocodeAsync(string value, CancellationToken cancellationToken)
    {
        if (CoordinateRegex().Match(value) is { Success: true } coord)
        {
            return (
                double.Parse(coord.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(coord.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        var url = $"https://api.transitous.org/api/v1/geocode?text={Uri.EscapeDataString(value)}";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var first = doc.RootElement[0];
        if (!first.TryGetProperty("lat", out var latEl) || !first.TryGetProperty("lon", out var lonEl))
        {
            return null;
        }

        return (latEl.GetDouble(), lonEl.GetDouble());
    }

    /// <summary>Plans the best transit journey between two points and maps it to a <see cref="JourneyStatus"/>.</summary>
    public async Task<JourneyStatus> GetJourneyAsync(SavedRoute route, CancellationToken cancellationToken)
    {
        try
        {
            var from = await GeocodeAsync(route.From, cancellationToken);
            var to = await GeocodeAsync(route.To, cancellationToken);

            if (from is null || to is null)
            {
                return new JourneyStatus
                {
                    Route = route,
                    Error = from is null ? $"Origin not found: {route.From}" : $"Destination not found: {route.To}",
                };
            }

            var time = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            var url = $"https://api.transitous.org/api/v1/plan?fromPlace={Fmt(from.Value.Lat)},{Fmt(from.Value.Lon)}&toPlace={Fmt(to.Value.Lat)},{Fmt(to.Value.Lon)}&time={time}&arriveBy=false";
            using var response = await _http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return ParseJourney(route, doc.RootElement) with
            {
                // No POI ids from Transitous: deep-link as raw geo points named
                // by the saved addresses (the Bonjour RATP site accepts that).
                OriginPlace = new GeoPlace(null, "GEOLOCATION", from.Value.Lat, from.Value.Lon, route.From),
                DestinationPlace = new GeoPlace(null, "GEOLOCATION", to.Value.Lat, to.Value.Lon, route.To),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new JourneyStatus { Route = route, Error = ex is TaskCanceledException ? "Timed out" : ex.Message };
        }
    }

    private static JourneyStatus ParseJourney(SavedRoute route, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("itineraries", out var itineraries)
            || itineraries.ValueKind != JsonValueKind.Array
            || itineraries.GetArrayLength() == 0)
        {
            return new JourneyStatus { Route = route, Error = "No transit journey found" };
        }

        var best = itineraries[0];
        if (!best.TryGetProperty("duration", out var durationEl) || !durationEl.TryGetDouble(out var durationSeconds))
        {
            return new JourneyStatus { Route = route, Error = "Unexpected journey response" };
        }

        var legs = new List<TransitLeg>();
        if (best.TryGetProperty("legs", out var legsEl) && legsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var leg in legsEl.EnumerateArray())
            {
                var mode = leg.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() : null;
                if (string.IsNullOrEmpty(mode) || mode == "WALK")
                {
                    continue;
                }

                var line = leg.TryGetProperty("routeShortName", out var lineEl) ? lineEl.GetString() : null;
                var from = leg.TryGetProperty("from", out var fromEl) && fromEl.TryGetProperty("name", out var fromName) ? fromName.GetString() : "";
                var to = leg.TryGetProperty("to", out var toEl) && toEl.TryGetProperty("name", out var toName) ? toName.GetString() : "";
                var start = leg.TryGetProperty("startTime", out var startEl) && DateTimeOffset.TryParse(startEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var legStart)
                    ? legStart
                    : DateTimeOffset.UtcNow;
                var realTime = leg.TryGetProperty("realTime", out var rtEl) && rtEl.ValueKind == JsonValueKind.True;

                legs.Add(new TransitLeg
                {
                    Mode = mode!,
                    Line = line ?? mode!,
                    From = from ?? "",
                    To = to ?? "",
                    Departure = start,
                    RealTime = realTime,
                });
            }
        }

        DateTimeOffset? departure = legs.Count > 0 ? legs.Min(l => l.Departure) : null;
        DateTimeOffset? arrival = null;
        if (best.TryGetProperty("endTime", out var endEl) && DateTimeOffset.TryParse(endEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end))
        {
            arrival = end;
        }

        return new JourneyStatus
        {
            Route = route,
            DurationMinutes = (int)Math.Round(durationSeconds / 60),
            Departure = departure,
            Arrival = arrival,
            Legs = legs,
        };
    }

    private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public void Dispose() => _http.Dispose();
}
