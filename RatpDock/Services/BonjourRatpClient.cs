using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RatpDock;

/// <summary>
/// Journey client speaking the real Bonjour RATP backend (bff.bonjour-ratp.fr) —
/// the exact same API the bonjour-ratp.fr web app calls for its itinerary
/// planner, so the dock shows the same itineraries as the official app.
/// Reverse-engineered contract (2026-10):
///   POST /pois/search        {"maxResponses":5,"text":"…"}
///       → application/vnd.rss.bff.pois-search.v6+json, pois[0] = {id,type,coordinates}
///   POST /itineraries/stream {"origin":{id,type,coordinates},"destination":{…},
///                             "withReducedMobility":false,"excludedLinesIds":[],
///                             "excludedStationsIds":[],"transportModes":["ALL"]}
///       → text/vnd.rss.bff.itinerary-stream.v12+event-stream (SSE: several
///         "data:" events, each = {metadata,itineraries,providers,lines,…};
///         lines[] maps lineId → {displayCode,color{background,font},businessMode}).
/// The BFF sits behind Cloudflare bot detection that fingerprints the client
/// stack: .NET's SocketsHttpHandler is blocked (403) while the OS WinHTTP
/// stack over HTTP/2 passes — WinHttpHandler is therefore mandatory here.
/// Etiquette: journeys are polled at most once per refresh interval (default
/// 5 minutes), i.e. the load of one open browser tab.
/// </summary>
internal sealed partial class BonjourRatpClient : IDisposable
{
    private const string BaseUrl = "https://bff.bonjour-ratp.fr";
    private const string ApiKey = "rHaqHU7laBW3WANbmWUwvrAOoLrdU8pC5aLLkS6e";
    private const string ClientVersion = "master-5fedf78c";
    private const string PoisAccept = "application/vnd.rss.bff.pois-search.v6+json";
    private const string StreamAccept = "text/vnd.rss.bff.itinerary-stream.v12+event-stream";

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Poi> _geocodeCache = new(StringComparer.OrdinalIgnoreCase);

    public BonjourRatpClient()
    {
        // WinHttpHandler (OS WinHTTP, HTTP/2) — see class remarks: Cloudflare
        // rejects the SocketsHttpHandler TLS fingerprint on this endpoint.
        _http = new HttpClient(new System.Net.Http.WinHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            CookieUsePolicy = CookieUsePolicy.IgnoreCookies,
        })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
    }

    [GeneratedRegex(@"^\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*$")]
    private static partial Regex CoordinateRegex();

    internal sealed record Poi(string Id, string Type, double Lat, double Lon, string Name);

    /// <summary>Resolves a "lat,lng" pair or a free-text address/POI to a Bonjour RATP place.</summary>
    public async Task<Poi?> GeocodeAsync(string value, CancellationToken cancellationToken)
    {
        if (CoordinateRegex().Match(value) is { Success: true } coord)
        {
            return new Poi(
                "",
                "GEOLOCATION",
                double.Parse(coord.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(coord.Groups[2].Value, CultureInfo.InvariantCulture),
                "");
        }

        if (_geocodeCache.TryGetValue(value, out var cached))
        {
            return cached;
        }

        var body = $$"""{"maxResponses":5,"text":{{JsonSerializer.Serialize(value)}}}""";
        using var response = await PostAsync($"{BaseUrl}/pois/search", body, PoisAccept, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("pois", out var pois)
            || pois.ValueKind != JsonValueKind.Array
            || pois.GetArrayLength() == 0)
        {
            return null;
        }

        var first = pois[0];
        if (!first.TryGetProperty("id", out var idEl)
            || !first.TryGetProperty("type", out var typeEl)
            || !first.TryGetProperty("coordinates", out var coordsEl)
            || !coordsEl.TryGetProperty("latitude", out var latEl)
            || !coordsEl.TryGetProperty("longitude", out var lonEl))
        {
            return null;
        }

        var name = first.TryGetProperty("name", out var nameEl) && nameEl.TryGetProperty("displayName", out var displayEl)
            ? displayEl.GetString() ?? ""
            : "";
        var poi = new Poi(idEl.GetString() ?? "", typeEl.GetString() ?? "PLACE", latEl.GetDouble(), lonEl.GetDouble(), name);
        _geocodeCache[value] = poi;
        return poi;
    }

    /// <summary>Plans the best transit journey between two places and maps it to a <see cref="JourneyStatus"/>.</summary>
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

            var body = BuildRequestBody(from, to);
            using var response = await PostAsync($"{BaseUrl}/itineraries/stream", body, StreamAccept, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new JourneyStatus
                {
                    Route = route,
                    Error = $"Bonjour RATP unavailable ({(int)response.StatusCode})",
                };
            }

            var sse = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseStream(route, sse) with
            {
                OriginPlace = ToPlace(from),
                DestinationPlace = ToPlace(to),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new JourneyStatus { Route = route, Error = ex is TaskCanceledException ? "Timed out" : ex.Message };
        }
    }

    private static GeoPlace ToPlace(Poi poi) => new(poi.Id.Length > 0 ? poi.Id : null, poi.Type, poi.Lat, poi.Lon, poi.Name);

    private async Task<HttpResponseMessage> PostAsync(string url, string json, string accept, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            Version = new Version(2, 0),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };

        // Headers exactly as the bonjour-ratp.fr web app sends them.
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.TryAddWithoutValidation("Accept-Language", "fr");
        request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
        request.Headers.TryAddWithoutValidation("x-client-guid", "bonjour-web-guid");
        request.Headers.TryAddWithoutValidation("x-client-locale", "fr_FR");
        request.Headers.TryAddWithoutValidation("x-client-platform", "bonjour_web");
        request.Headers.TryAddWithoutValidation("x-client-version", ClientVersion);
        request.Headers.TryAddWithoutValidation("Origin", "https://www.bonjour-ratp.fr");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bonjour-ratp.fr/itineraires/");

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static string BuildRequestBody(Poi from, Poi to) =>
        "{\"origin\":{\"id\":\"" + from.Id + "\",\"type\":\"" + from.Type + "\",\"coordinates\":{\"latitude\":"
        + Fmt(from.Lat) + ",\"longitude\":" + Fmt(from.Lon) + "}},"
        + "\"destination\":{\"id\":\"" + to.Id + "\",\"type\":\"" + to.Type + "\",\"coordinates\":{\"latitude\":"
        + Fmt(to.Lat) + ",\"longitude\":" + Fmt(to.Lon) + "}},"
        + "\"withReducedMobility\":false,\"excludedLinesIds\":[],\"excludedStationsIds\":[],\"transportModes\":[\"ALL\"]}";

    /// <summary>Parses the SSE itinerary stream into a <see cref="JourneyStatus"/> for the best transit itinerary.</summary>
    private static JourneyStatus ParseStream(SavedRoute route, string sse)
    {
        // lines[] (per event): lineId → official code + colors.
        var lines = new Dictionary<string, (string Code, string? Bg, string? Font)>();
        // itineraries by id; transitOnly keeps walk/bike alternatives out.
        var itineraries = new Dictionary<string, JsonElement>();
        var transitOnly = new HashSet<string>();
        // The web app highlights the PUBLIC_TRANSIT section's first itinerary.
        var sectionOrder = new List<string>();

        foreach (var raw in sse.Split('\n'))
        {
            if (!raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(raw[5..].TrimStart());
            var root = doc.RootElement;

            if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in linesEl.EnumerateArray())
                {
                    if (line.TryGetProperty("id", out var idEl)
                        && line.TryGetProperty("displayCode", out var codeEl)
                        && idEl.GetString() is { Length: > 0 } id)
                    {
                        var bg = line.TryGetProperty("color", out var colorEl) && colorEl.TryGetProperty("background", out var bgEl)
                            ? bgEl.GetString()
                            : null;
                        var font = line.TryGetProperty("color", out var colorEl2) && colorEl2.TryGetProperty("font", out var fontEl)
                            ? fontEl.GetString()
                            : null;
                        lines[id] = (codeEl.GetString() ?? "", bg, font);
                    }
                }
            }

            if (root.TryGetProperty("itineraries", out var itinsEl) && itinsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var itinerary in itinsEl.EnumerateArray())
                {
                    if (itinerary.TryGetProperty("id", out var itIdEl) && itIdEl.GetString() is { Length: > 0 } itId)
                    {
                        using var clone = JsonDocument.Parse(itinerary.GetRawText());
                        itineraries[itId] = clone.RootElement.Clone();

                        var modes = itinerary.TryGetProperty("modes", out var modesEl) ? modesEl.GetString() : null;
                        if (modes is not ("walk" or "personal bike" or "bike" or "car"))
                        {
                            transitOnly.Add(itId);
                        }
                    }
                }
            }

            // Later events refresh the section ordering — keep the latest.
            if (root.TryGetProperty("metadata", out var metaEl)
                && metaEl.TryGetProperty("sections", out var sectionsEl)
                && sectionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var section in sectionsEl.EnumerateArray())
                {
                    if (section.TryGetProperty("id", out var secIdEl) && secIdEl.GetString() == "PUBLIC_TRANSIT"
                        && section.TryGetProperty("itinerariesIds", out var idsEl)
                        && idsEl.ValueKind == JsonValueKind.Array)
                    {
                        sectionOrder.Clear();
                        sectionOrder.AddRange(idsEl.EnumerateArray().Select(i => i.GetString() ?? ""));
                    }
                }
            }
        }

        if (itineraries.Count == 0)
        {
            return new JourneyStatus { Route = route, Error = "No itinerary found" };
        }

        // Best = the transit itinerary the app shows first; fallback: shortest transit.
        JsonElement best;
        if (sectionOrder.FirstOrDefault(transitOnly.Contains) is { Length: > 0 } firstId
            && itineraries.TryGetValue(firstId, out var firstIt))
        {
            best = firstIt;
        }
        else if (transitOnly.Count > 0)
        {
            best = transitOnly
                .Where(itineraries.ContainsKey)
                .Select(id => itineraries[id])
                .OrderBy(i => i.TryGetProperty("durationInSeconds", out var d) && d.TryGetInt32(out var secs) ? secs : int.MaxValue)
                .First();
        }
        else
        {
            best = itineraries.Values.First();
        }

        return ParseItinerary(route, best, lines);
    }

    private static JourneyStatus ParseItinerary(SavedRoute route, JsonElement best, Dictionary<string, (string Code, string? Bg, string? Font)> lines)
    {
        var durationSeconds = best.TryGetProperty("durationInSeconds", out var durationEl) && durationEl.TryGetInt32(out var secs) ? secs : 0;

        var departure = ParseDate(best, "departureDateTime");
        var arrival = ParseDate(best, "arrivalDateTime");

        var legs = new List<TransitLeg>();
        if (best.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
        {
            var steps = stepsEl.EnumerateArray().ToList();

            // Boarding/alighting station names live in the ACCESS_POINT/HUB_POINT
            // steps that surround each transit segment.
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var stepType = step.TryGetProperty("stepType", out var typeEl) ? typeEl.GetString() : null;
                if (stepType is null || !stepType.EndsWith("_SEGMENT", StringComparison.Ordinal))
                {
                    continue;
                }

                var transit = step.TryGetProperty("transit", out var transitEl) ? transitEl : default;
                if (transit.ValueKind == JsonValueKind.Undefined)
                {
                    continue; // e.g. PERSONAL_BICYCLE_SEGMENT
                }

                var lineId = transit.TryGetProperty("lineId", out var lineIdEl) ? lineIdEl.GetString() : null;
                var (code, bg, font) = lineId is not null && lines.TryGetValue(lineId, out var line)
                    ? line
                    : (LineFallbackName(lineId), null, null);

                var from = PreviousStationName(steps, i) ?? StationsEdge(transit, first: true) ?? "";
                var to = NextStationName(steps, i) ?? StationsEdge(transit, first: false) ?? "";

                var start = ParseDate(step, "departureDateTime") ?? departure ?? DateTimeOffset.UtcNow;

                legs.Add(new TransitLeg
                {
                    Mode = stepType[..^"_SEGMENT".Length],
                    Line = code,
                    From = from,
                    To = to,
                    Departure = start,
                    RealTime = true,
                    BadgeColor = bg,
                    BadgeDarkText = IsDarkText(font),
                });
            }
        }

        return new JourneyStatus
        {
            Route = route,
            DurationMinutes = durationSeconds > 0 ? (int)Math.Round(durationSeconds / 60.0) : null,
            Departure = departure,
            Arrival = arrival,
            Legs = legs,
        };
    }

    private static string LineFallbackName(string? lineId) =>
        lineId is null ? "?" : lineId[(lineId.LastIndexOf(':') + 1)..];

    /// <summary>True when the line's official font color is dark (light background → dark badge text).</summary>
    private static bool IsDarkText(string? fontHex) =>
        fontHex is not null
        && ColorTranslator.FromHtml(fontHex) is { } c
        && (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) < 128;

    private static string? PreviousStationName(List<JsonElement> steps, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (StationName(steps[i]) is { } name)
            {
                return name;
            }
        }

        return null;
    }

    private static string? NextStationName(List<JsonElement> steps, int index)
    {
        for (var i = index + 1; i < steps.Count; i++)
        {
            if (StationName(steps[i]) is { } name)
            {
                return name;
            }
        }

        return null;
    }

    private static string? StationName(JsonElement step)
    {
        var type = step.TryGetProperty("stepType", out var typeEl) ? typeEl.GetString() : null;
        if (type is not ("ACCESS_POINT" or "HUB_POINT"))
        {
            return null;
        }

        return step.TryGetProperty("name", out var nameEl) && nameEl.TryGetProperty("displayName", out var displayEl)
            ? displayEl.GetString()
            : null;
    }

    private static string? StationsEdge(JsonElement transit, bool first)
    {
        if (!transit.TryGetProperty("stations", out var stationsEl) || stationsEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = stationsEl.EnumerateArray().Select(s => s.GetString()).Where(s => !string.IsNullOrEmpty(s)).ToList();
        return names.Count == 0 ? null : first ? names[0] : names[^1];
    }

    private static DateTimeOffset? ParseDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var el)
            && el.GetString() is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUniversalTime()
            : null;

    private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public void Dispose() => _http.Dispose();
}
