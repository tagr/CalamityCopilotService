using System.Globalization;
using System.Net.Http.Json;
using CalamityCopilotService.Api;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddScoped<ViirsService>();
builder.Services.AddScoped<NwsService>();
builder.Services.AddHttpClient();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

/// <summary>
/// Returns an 800×800 PNG static map image from Azure Maps centered on the given coordinates.
/// An optional overlay can add fire detections (VIIRS) or NWS alert zone polygons to the image.
/// </summary>
/// <param name="lat">Latitude of the map center in decimal degrees.</param>
/// <param name="lon">Longitude of the map center in decimal degrees.</param>
/// <param name="overlay">
/// Overlay type to render on top of the base map:
/// <list type="bullet">
///   <item><term>fire</term><description>Top-10 VIIRS fire detections within a 100 km bounding box, sized and colored by Fire Radiative Power.</description></item>
///   <item><term>alert</term><description>NWS active-alert zone polygon(s) for the forecast, county, and fire-weather zones at the given point.</description></item>
///   <item><term>(anything else)</term><description>Base road map with a location pin only.</description></item>
/// </list>
/// </param>
app.MapGet("/map/static", async (
    double lat,
    double lon,
    string overlay,
    ViirsService viirsService,
    NwsService nwsService,
    IConfiguration config,
    IHttpClientFactory httpFactory) =>
{
    var key = config["AzureMaps:Key"];
    var http = httpFactory.CreateClient();
    var isFireMap = overlay.Equals("fire", StringComparison.OrdinalIgnoreCase);
    var zoomLevel = isFireMap ? 12 : 10;

    // Azure Maps wants center as lon,lat
    var url =
        "https://atlas.microsoft.com/map/static" +
        "?api-version=2024-04-01" +
        "&tilesetId=microsoft.base.road" +
        $"&center={lon},{lat}" +
        $"&zoom={zoomLevel}" +
        "&width=800" +
        "&height=800" +
        $"&pins=default||'{Uri.EscapeDataString("Location")}'{lon} {lat}" +
        $"&subscription-key={key}";

    if (overlay.Equals("fire", StringComparison.OrdinalIgnoreCase))
    {
        var box = GetBoundingBox(lat, lon);
        var fires = await viirsService.GetViirsFireDataAsync(box);

        if (fires.Count > 0)
            url += ViirsService.BuildFireCirclePaths(fires);
    }
    else if (overlay.Equals("alert", StringComparison.OrdinalIgnoreCase))
    {
        var props = await nwsService.GetNwsPointAsync(lat, lon);
        if (props is not null)
        {
            static async Task<NwsActiveAlertsResponse?> TryGetAlerts(NwsService nws, string? zoneId)
            {
                if (zoneId is null) return null;
                return await nws.GetActiveAlertsAsync(zoneId);
            }

            var results = await Task.WhenAll(
                TryGetAlerts(nwsService, props.ForecastZoneId),
                TryGetAlerts(nwsService, props.CountyZoneId),
                TryGetAlerts(nwsService, props.FireWeatherZoneId)
            );
            var (forecastAlerts, countyAlerts, fireAlerts) = (results[0], results[1], results[2]);

            ZoneResponse? zone = null;
            if (forecastAlerts?.features is { Length: > 0 })
                zone = await nwsService.GetNwsZoneAsync(props.ForecastZoneId!, NwsZoneType.Land);
            else if (countyAlerts?.features is { Length: > 0 })
                zone = await nwsService.GetNwsZoneAsync(props.CountyZoneId!, NwsZoneType.County);
            else if (fireAlerts?.features is { Length: > 0 })
                zone = await nwsService.GetNwsZoneAsync(props.FireWeatherZoneId!, NwsZoneType.Fire);

            if (zone?.geometry is not null)
                url += NwsService.BuildAlertZonePaths(zone.geometry);
        }
    }

    var bytes = await http.GetByteArrayAsync(url);
    return Results.File(bytes, "image/png");
})
.WithName("GetStaticMap")
.WithSummary("Get a static map image with an optional fire or alert overlay")
.WithDescription(
    "Returns an 800×800 PNG from Azure Maps centered on the supplied coordinates. " +
    "Pass overlay=fire to render the top-10 VIIRS fire detections (sized by FRP) within 100 km, " +
    "or overlay=alert to shade the NWS active-alert zone polygon for the point.");

/// <summary>
/// Resolves the NWS forecast, county, and fire-weather zones for a lat/lon coordinate
/// and returns any currently active alerts for each zone.
/// </summary>
/// <param name="lat">Latitude in decimal degrees.</param>
/// <param name="lon">Longitude in decimal degrees.</param>
/// <returns>
/// Zone IDs and three arrays of active alert headlines/descriptions — one per zone type.
/// Returns 404 if NWS cannot resolve the point (e.g. outside the US).
/// </returns>
app.MapGet("/nws/zone", async (double lat, double lon, NwsService nws) =>
{
    var props = await nws.GetNwsPointAsync(lat, lon);
    if (props is null) return Results.NotFound();

    static async Task<object[]?> GetAlerts(NwsService nws, string? zoneId)
    {
        if (zoneId is null) return null;
        var alerts = await nws.GetActiveAlertsAsync(zoneId);
        return alerts.features?
            .Select(f => new { f.properties.headline, f.properties.description })
            .ToArray<object>();
    }

    var results = await Task.WhenAll(
        GetAlerts(nws, props.ForecastZoneId),
        GetAlerts(nws, props.CountyZoneId),
        GetAlerts(nws, props.FireWeatherZoneId)
    );
    var (forecastAlerts, countyAlerts, fireWeatherAlerts) = (results[0], results[1], results[2]);

    return Results.Ok(new
    {
        zoneId            = props.ForecastZoneId,
        countyZoneId      = props.CountyZoneId,
        fireWeatherZoneId = props.FireWeatherZoneId,
        forecastAlerts,
        countyAlerts,
        fireWeatherAlerts
    });
})
.WithName("GetNwsPoint")
.WithSummary("Get NWS zone IDs and active alerts for a coordinate")
.WithDescription(
    "Calls the NWS /points endpoint to resolve the forecast, county, and fire-weather zone IDs " +
    "for the given lat/lon, then fetches active alerts for each zone in parallel. " +
    "Returns 404 for coordinates outside NWS coverage (non-US locations).");

/// <summary>
/// Returns active NWS alerts for a specific zone ID.
/// </summary>
/// <param name="zoneId">
/// NWS zone identifier (e.g. <c>PZZ745</c>, <c>MNZ001</c>).
/// Obtain zone IDs from the <c>/nws/zone</c> endpoint.
/// </param>
/// <returns>Array of alert headlines and descriptions; empty array if no active alerts.</returns>
app.MapGet("/nws/alerts", async (string zoneId, NwsService nws) =>
{
    var alerts = await nws.GetActiveAlertsAsync(zoneId);
    var result = alerts.features?
        .Select(f => new { f.properties.headline, f.properties.description })
        .ToArray();
    return Results.Ok(result);
})
.WithName("GetNwsActiveAlerts")
.WithSummary("Get active NWS alerts for a zone ID")
.WithDescription(
    "Calls the NWS /alerts/active/zone/{zoneId} endpoint and returns the headline and description " +
    "for each active alert. Use the /nws/zone endpoint to look up zone IDs for a lat/lon.");

/// <summary>
/// Returns VIIRS NOAA-21 Near Real-Time fire detections within a 100 km bounding box
/// of the given coordinate for the specified number of days.
/// </summary>
/// <param name="lat">Latitude of the search center in decimal degrees.</param>
/// <param name="lon">Longitude of the search center in decimal degrees.</param>
/// <param name="numDays">
/// Number of days back from today to include in the query window (1–10, default 5).
/// </param>
/// <returns>List of <see cref="ViirsFireDetection"/> records ordered as returned by NASA FIRMS.</returns>
app.MapGet("/fires/viirs", async (
    double lat,
    double lon,
    ViirsService viirsService,
    int numDays = 5) =>
{
    var bbox  = GetBoundingBox(lat, lon);
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var fires = await viirsService.GetViirsFireDataAsync(bbox, numDays);
    return Results.Ok(fires);
})
.WithName("GetViirsFireData")
.WithSummary("Get VIIRS fire detections near a coordinate")
.WithDescription(
    "Queries the NASA FIRMS VIIRS NOAA-21 NRT API for fire detections within a 100 km bounding box " +
    "around the given lat/lon over the last numDays days (default 5, max 10). " +
    "Each detection includes coordinates, brightness temperatures, Fire Radiative Power (FRP), " +
    "acquisition time, and confidence level.");

/// <summary>
/// Returns a rectangular bounding box centered on the given coordinate.
/// Uses the spherical-Earth approximation (WGS-84 mean radius = 6 371 000 m).
/// </summary>
/// <param name="lat">Latitude of the center point in decimal degrees.</param>
/// <param name="lng">Longitude of the center point in decimal degrees.</param>
/// <param name="radius">
/// Distance in metres from the center to each edge of the box (default 100 000 m = 100 km).
/// </param>
/// <returns><c>[west, south, east, north]</c> in decimal degrees.</returns>
app.MapGet("/bounding-box", (double lat, double lng, int radius = 100000) =>
{
    var bbox = GetBoundingBox(lat, lng, radius);
    return Results.Ok(new double[]
    {
       bbox[0], //west
       bbox[1], //south
       bbox[2], //east
       bbox[3]  //north
    });
})
.WithName("GetBoundingBox")
.WithSummary("Compute a bounding box around a coordinate")
.WithDescription(
    "Returns [west, south, east, north] in decimal degrees for a box whose edges are exactly " +
    "radius metres from the center point. Uses the WGS-84 mean-radius spherical-Earth approximation. " +
    "Default radius is 100 000 m (100 km).");

/// <summary>
/// Geocodes a free-text address or place name to a [latitude, longitude] coordinate pair
/// using the Azure Maps Geocoding API (2023-06-01).
/// </summary>
/// <param name="query">
/// Address or place name to geocode (1–100 characters, e.g. "Seattle, WA" or "1600 Pennsylvania Ave").
/// </param>
/// <returns>
/// <c>[latitude, longitude]</c> for the best-matching result,
/// or 404 if no match is found, or 400 if the query is empty or exceeds 100 characters.
/// </returns>
app.MapGet("/geocode", async (
    string query,
    IConfiguration config,
    IHttpClientFactory httpFactory) =>
{
    if (string.IsNullOrWhiteSpace(query) || query.Length > 100)
        return Results.BadRequest("query must be 1–100 characters.");

    var key  = config["AzureMaps:Key"];
    var http = httpFactory.CreateClient();

    var url = "https://atlas.microsoft.com/geocode" +
              "?api-version=2023-06-01" +
              $"&query={Uri.EscapeDataString(query)}" +
              $"&subscription-key={key}";

    var response = await http.GetFromJsonAsync<AzureMapsGeocodeResponse>(url);
    var coords   = response?.features?.FirstOrDefault()?.geometry?.coordinates;

    if (coords is not { Length: 2 })
        return Results.NotFound("No results found for the given query.");

    // Azure Maps returns [lon, lat]; we return [lat, lon]
    return Results.Ok(new double[] { coords[1], coords[0] });
})
.WithName("Geocode")
.WithSummary("Geocode a place name or address to [lat, lon]")
.WithDescription(
    "Calls the Azure Maps Geocoding API with the supplied query string and returns [latitude, longitude] " +
    "for the top result. Returns 400 for an empty or >100-character query, and 404 when Azure Maps " +
    "finds no matching location.");

app.Run();

// Returns [west, south, east, north] — each edge exactly `radius` metres from the centre.
// Uses the spherical-Earth approximation (WGS-84 mean radius = 6 371 000 m).
static double[] GetBoundingBox(double lat, double lng, int radius = 100000)
{
    const double EarthRadius = 6_371_000.0; // metres

    // Angular distance in radians
    double delta = radius / EarthRadius;

    double latRad = lat * Math.PI / 180.0;

    double latDelta = delta * (180.0 / Math.PI);
    double lngDelta = delta / Math.Cos(latRad) * (180.0 / Math.PI);

    return
    [
        lng - lngDelta, // west
        lat - latDelta, // south
        lng + lngDelta, // east
        lat + latDelta, // north
    ];
}

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

// Azure Maps Geocoding API (2023-06-01) response shape
record AzureMapsGeocodeResponse(AzureMapsGeocodeFeature[]? features);
record AzureMapsGeocodeFeature(AzureMapsGeocodeGeometry? geometry);
record AzureMapsGeocodeGeometry(string? type, double[]? coordinates);


