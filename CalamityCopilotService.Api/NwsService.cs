using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CalamityCopilotService.Api;

/// <summary>
/// Queries the National Weather Service (NWS) REST API.
/// </summary>
public class NwsService(IHttpClientFactory httpFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Calls https://api.weather.gov/points/{lat},{lng} and returns the deserialized response.
    /// </summary>
    /// <param name="lat">Latitude in decimal degrees.</param>
    /// <param name="lng">Longitude in decimal degrees.</param>
    public async Task<NwsPointProperties?> GetNwsPointAsync(double lat, double lng)
    {
        var url = $"https://api.weather.gov/points/" +
                  $"{lat.ToString(CultureInfo.InvariantCulture)}," +
                  $"{lng.ToString(CultureInfo.InvariantCulture)}";

        var http = httpFactory.CreateClient();

        // NWS requires a User-Agent header identifying the application.
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "CalamityCopilotService <github.com/tagr>");

        var json = await http.GetStringAsync(url);

        var response = JsonSerializer.Deserialize<NwsPointResponse>(json, JsonOptions)
                       ?? throw new InvalidOperationException("NWS /points response was null or empty.");

        return response.Properties;
    }

    /// <summary>
    /// Calls https://api.weather.gov/zones/{type}/{zoneId} and returns the deserialized response.
    /// </summary>
    /// <param name="zoneId">NWS zone identifier, e.g. "MNZ001".</param>
    /// <param name="type">Zone type: land, county, fire, or coastal. Defaults to land.</param>
    public async Task<ZoneResponse> GetNwsZoneAsync(string zoneId, NwsZoneType type = NwsZoneType.Land)
    {
        var url = $"https://api.weather.gov/zones/{type.ToString().ToLowerInvariant()}/{Uri.EscapeDataString(zoneId)}";

        var http = httpFactory.CreateClient();

        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "CalamityCopilotService <github.com/tagr>");

        string? json;
        try
        {
            json = await http.GetStringAsync(url);
        } catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { //check if coastal
            url = $"https://api.weather.gov/zones/{NwsZoneType.Coastal.ToString().ToLowerInvariant()}/{Uri.EscapeDataString(zoneId)}";
            json = await http.GetStringAsync(url);
        }

        return JsonSerializer.Deserialize<ZoneResponse>(json, JsonOptions)
               ?? throw new InvalidOperationException("NWS zone response was null or empty.");
    }

    /// <summary>
    /// Builds one <c>&amp;path=</c> segment per polygon ring in a NWS zone geometry, suitable for
    /// appending directly to an Azure Maps static-image URL.
    /// </summary>
    /// <remarks>
    /// NWS returns GeoJSON Polygon or MultiPolygon coordinates in [longitude, latitude] order.
    /// <see cref="Geometry.coordinates"/> is held as a <see cref="JsonElement"/> because the two
    /// geometry types have different depths (3-D vs 4-D arrays); parsing at runtime avoids the
    /// dimension mismatch that would crash a strongly-typed <c>float[][][]</c> property on a
    /// MultiPolygon response.  Only the outer ring of each polygon is rendered; inner rings
    /// (holes) are skipped.
    /// <para>
    /// Azure Maps caps each <c>&amp;path=</c> segment at <see cref="MaxPathLocations"/> locations.
    /// Rings that exceed this limit are simplified with the Ramer-Douglas-Peucker algorithm and
    /// then stride-capped as a hard safety net.
    /// </para>
    /// </remarks>
    /// <param name="geometry">Zone geometry returned by <see cref="GetNwsZoneAsync"/>.</param>
    public static string BuildAlertZonePaths(Geometry geometry)
    {
        if (geometry?.type is null) return string.Empty;

        // Semi-transparent red fill with a solid red border — visually distinct from fire squares.
        const string style = "lcCC0000|fcCC0000|la0.5|fa0.15|lw2";

        var sb = new StringBuilder();

        if (geometry.type.Equals("Polygon", StringComparison.OrdinalIgnoreCase))
        {
            // coordinates = [ ring, ring, ... ] — render the outer ring only (index 0).
            var outerRing = ParseRing(geometry.coordinates[0]);
            AppendRing(sb, outerRing, style);
        }
        else if (geometry.type.Equals("MultiPolygon", StringComparison.OrdinalIgnoreCase))
        {
            // coordinates = [ polygon, polygon, ... ] where polygon = [ ring, ring, ... ].
            // Render the outer ring (index 0) of each polygon as a separate path segment.
            foreach (var polygon in geometry.coordinates.EnumerateArray())
                AppendRing(sb, ParseRing(polygon[0]), style);
        }

        return sb.ToString();

        static float[][] ParseRing(JsonElement ringElement) =>
            ringElement.EnumerateArray()
                .Select(pt => pt.EnumerateArray().Select(c => c.GetSingle()).ToArray())
                .ToArray();

        static void AppendRing(StringBuilder sb, float[][] ring, string style)
        {
            if (ring is not { Length: > 1 }) return;
            var simplified = SimplifyRing(ring);
            var positions = string.Join("|", simplified.Select(p =>
                $"{p[0].ToString(CultureInfo.InvariantCulture)} {p[1].ToString(CultureInfo.InvariantCulture)}"));
            sb.Append($"&path={style}||{positions}");
        }
    }

    /// <summary>Azure Maps hard limit on locations per <c>&amp;path=</c> segment.</summary>
    private const int MaxPathLocations = 100;

    /// <summary>
    /// Reduces a polygon ring to at most <see cref="MaxPathLocations"/> points using
    /// Ramer-Douglas-Peucker followed by a stride stride-cap as a hard safety net.
    /// The closing point (first == last) is preserved.
    /// </summary>
    private static float[][] SimplifyRing(float[][] ring)
    {
        if (ring.Length <= MaxPathLocations)
            return ring;

        // RDP pass — start with a tolerance scaled to degree-space (~1 km ≈ 0.009°).
        // Double the tolerance until we're under the cap.
        double epsilon = 0.01;
        float[][] result;
        do
        {
            result = RamerDouglasPeucker(ring, epsilon);
            epsilon *= 2;
        }
        while (result.Length > MaxPathLocations);

        // Hard stride-cap as an absolute safety net.
        if (result.Length > MaxPathLocations)
            result = StrideCap(result, MaxPathLocations);

        return result;
    }

    /// <summary>
    /// Iterative Ramer-Douglas-Peucker simplification.
    /// Coordinates are treated as 2-D planar points in degree-space (acceptable at zone scale).
    /// </summary>
    private static float[][] RamerDouglasPeucker(float[][] points, double epsilon)
    {
        if (points.Length <= 2) return points;

        var keep = new bool[points.Length];
        keep[0] = true;
        keep[points.Length - 1] = true;

        // Iterative stack-based approach to avoid stack overflow on large rings.
        var stack = new Stack<(int start, int end)>();
        stack.Push((0, points.Length - 1));

        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            if (end - start <= 1) continue;

            double maxDist = 0;
            int maxIdx = start;

            // Perpendicular distance from each intermediate point to the chord start→end.
            double x1 = points[start][0], y1 = points[start][1];
            double x2 = points[end][0],   y2 = points[end][1];
            double dx = x2 - x1, dy = y2 - y1;
            double chordLen = Math.Sqrt(dx * dx + dy * dy);

            for (int i = start + 1; i < end; i++)
            {
                double dist = chordLen > 0
                    ? Math.Abs(dy * points[i][0] - dx * points[i][1] + x2 * y1 - y2 * x1) / chordLen
                    : Math.Sqrt(Math.Pow(points[i][0] - x1, 2) + Math.Pow(points[i][1] - y1, 2));

                if (dist > maxDist) { maxDist = dist; maxIdx = i; }
            }

            if (maxDist > epsilon)
            {
                keep[maxIdx] = true;
                stack.Push((start, maxIdx));
                stack.Push((maxIdx, end));
            }
        }

        return points.Where((_, i) => keep[i]).ToArray();
    }

    /// <summary>Hard-caps a ring to <paramref name="max"/> points by uniform stride, closing it.</summary>
    private static float[][] StrideCap(float[][] ring, int max)
    {
        // Reserve one slot for the explicit closing point.
        int stride = (int)Math.Ceiling((double)(ring.Length - 1) / (max - 1));
        var result = new List<float[]>(max);
        for (int i = 0; i < ring.Length - 1; i += stride)
            result.Add(ring[i]);
        result.Add(ring[^1]); // close
        return [.. result];
    }

    /// <summary>
    /// Calls https://api.weather.gov/alerts/active/zone/{zoneId} and returns the deserialized response.
    /// </summary>
    /// <param name="zoneId">NWS zone identifier, e.g. "PZZ745".</param>
    public async Task<NwsActiveAlertsResponse> GetActiveAlertsAsync(string zoneId)
    {
        var url = $"https://api.weather.gov/alerts/active/zone/{Uri.EscapeDataString(zoneId)}";

        var http = httpFactory.CreateClient();

        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "CalamityCopilotService <github.com/tagr>");

        var json = await http.GetStringAsync(url);

        return JsonSerializer.Deserialize<NwsActiveAlertsResponse>(json, JsonOptions)
               ?? throw new InvalidOperationException("NWS active alerts response was null or empty.");
    }
}


public enum NwsZoneType { Land, County, Fire, Coastal }

public class NwsActiveAlertsResponse
{
    public object[] context { get; set; }
    public string type { get; set; }
    public Feature[] features { get; set; }
    public string title { get; set; }
    public DateTime updated { get; set; }
}

public class Feature
{
    public string id { get; set; }
    public string type { get; set; }
    public object geometry { get; set; }
    public NwsAlertProperties properties { get; set; }
}

public class NwsAlertProperties
{
    public string id { get; set; }
    public string type { get; set; }
    public string areaDesc { get; set; }
    public Geocode geocode { get; set; }
    public string[] affectedZones { get; set; }
    public object[] references { get; set; }
    public DateTime sent { get; set; }
    public DateTime effective { get; set; }
    public DateTime onset { get; set; }
    public DateTime expires { get; set; }
    public DateTime? ends { get; set; }
    public string status { get; set; }
    public string messageType { get; set; }
    public string category { get; set; }
    public string severity { get; set; }
    public string certainty { get; set; }
    public string urgency { get; set; }
    public string _event { get; set; }
    public string sender { get; set; }
    public string senderName { get; set; }
    public string headline { get; set; }
    public string description { get; set; }
    public string instruction { get; set; }
    public string response { get; set; }
    public object note { get; set; }
    public Parameters parameters { get; set; }
    public string scope { get; set; }
    public string code { get; set; }
    public string language { get; set; }
    public string web { get; set; }
    public Eventcode eventCode { get; set; }
}

public class Geocode
{
    public string[] SAME { get; set; }
    public string[] UGC { get; set; }
}

public class Parameters
{
    public string[] AWIPSidentifier { get; set; }
    public string[] WMOidentifier { get; set; }
    public string[] NWSheadline { get; set; }
    public string[] BLOCKCHANNEL { get; set; }
    public string[] VTEC { get; set; }
    public DateTime[] eventEndingTime { get; set; }
}

public class Eventcode
{
    public string[] SAME { get; set; }
    public string[] NationalWeatherService { get; set; }
}


public class NwsPointResponse
{
    public object[] Context { get; set; } = [];
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public NwsGeometry? Geometry { get; set; }
    public NwsPointProperties? Properties { get; set; }
}

public class NwsGeometry
{
    public string Type { get; set; } = string.Empty;
    public float[] Coordinates { get; set; } = [];
}

public class NwsPointProperties
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Cwa { get; set; } = string.Empty;
    public string ForecastOffice { get; set; } = string.Empty;
    public string GridId { get; set; } = string.Empty;
    public int GridX { get; set; }
    public int GridY { get; set; }
    public string Forecast { get; set; } = string.Empty;
    public string ForecastHourly { get; set; } = string.Empty;
    public string ForecastGridData { get; set; } = string.Empty;
    public string ObservationStations { get; set; } = string.Empty;
    public NwsRelativeLocation? RelativeLocation { get; set; }
    public string ForecastZone { get; set; } = string.Empty;
    public string County { get; set; } = string.Empty;
    public string FireWeatherZone { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public string RadarStation { get; set; } = string.Empty;

    /// <summary>
    /// The zone ID parsed from the tail segment of <see cref="ForecastZone"/>.
    /// e.g. "https://api.weather.gov/zones/forecast/PZZ745" → "PZZ745"
    /// </summary>
    public string? ForecastZoneId =>
        string.IsNullOrWhiteSpace(ForecastZone) ? null : ForecastZone.Split('/').Last();

    /// <summary>
    /// The zone ID parsed from the tail segment of <see cref="County"/>.
    /// e.g. "https://api.weather.gov/zones/county/NCC163" → "NCC163"
    /// </summary>
    public string? CountyZoneId =>
        string.IsNullOrWhiteSpace(County) ? null : County.Split('/').Last();

    /// <summary>
    /// The zone ID parsed from the tail segment of <see cref="FireWeatherZone"/>.
    /// e.g. "https://api.weather.gov/zones/fire/PZZ745" → "PZZ745"
    /// </summary>
    public string? FireWeatherZoneId =>
        string.IsNullOrWhiteSpace(FireWeatherZone) ? null : FireWeatherZone.Split('/').Last();
}

public class NwsRelativeLocation
{
    public string Type { get; set; } = string.Empty;
    public NwsGeometry? Geometry { get; set; }
    public NwsRelativeLocationProperties? Properties { get; set; }
}

public class NwsRelativeLocationProperties
{
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public NwsMeasurement? Distance { get; set; }
    public NwsMeasurement? Bearing { get; set; }
}

public class NwsMeasurement
{
    public string UnitCode { get; set; } = string.Empty;
    public double Value { get; set; }
}


public class ZoneResponse
{
    public Context context { get; set; }
    public string id { get; set; }
    public string type { get; set; }
    public Geometry geometry { get; set; }
    public ZoneProperties properties { get; set; }
}

public class Context
{
    public string version { get; set; }
}

public class Geometry
{
    public string type { get; set; }
    /// <summary>
    /// Raw coordinate array. GeoJSON Polygon is 3-D ([ring][point][coord]) and MultiPolygon is
    /// 4-D ([polygon][ring][point][coord]), so this is kept as <see cref="JsonElement"/> to
    /// avoid a dimension mismatch at deserialization time.
    /// </summary>
    public JsonElement coordinates { get; set; }
}

public class ZoneProperties
{
    public string id { get; set; }
    public string type { get; set; }
    public string name { get; set; }
    public DateTime effectiveDate { get; set; }
    public DateTime expirationDate { get; set; }
    public string state { get; set; }
    public string forecastOffice { get; set; }
    public string gridIdentifier { get; set; }
    public string awipsLocationIdentifier { get; set; }
    public string[] cwa { get; set; }
    public string[] forecastOffices { get; set; }
    public string[] timeZone { get; set; }
    public string[] observationStations { get; set; }
    public string radarStation { get; set; }
}
