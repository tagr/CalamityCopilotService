using System.Globalization;

namespace CalamityCopilotService.Api;

/// <summary>
/// Queries the NASA FIRMS VIIRS SNPP NRT area API and parses the CSV response.
/// </summary>
public class ViirsService(IConfiguration config, IHttpClientFactory httpFactory)
{
    /// <summary>
    /// Fetches fire detections for the given bounding box and time window.
    /// </summary>
    /// <param name="bbox">[west, south, east, north] in decimal degrees.</param>
    /// <param name="numDays">Number of days back to include (1–10).</param>
    /// <param name="date">Reference end date for the query window.</param>
    public async Task<List<ViirsFireDetection>> GetViirsFireDataAsync(
        double[] bbox,
        int numDays = 5)
    {
        var baseUrl = config["NasaFirms:BaseUrl"] ?? "https://firms.modaps.eosdis.nasa.gov";
        var apiKey  = config["NasaFirms:ApiKey"]
                      ?? throw new InvalidOperationException("NasaFirms:ApiKey is not configured.");

        var url = $"{baseUrl}/api/area/csv/{apiKey}/VIIRS_NOAA21_NRT" +
                  $"/{bbox[0].ToString(CultureInfo.InvariantCulture)}" +
                  $",{bbox[1].ToString(CultureInfo.InvariantCulture)}" +
                  $",{bbox[2].ToString(CultureInfo.InvariantCulture)}" +
                  $",{bbox[3].ToString(CultureInfo.InvariantCulture)}" +
                  $"/{numDays}";

        var http = httpFactory.CreateClient();
        var csv  = await http.GetStringAsync(url);

        return ParseViirsCsv(csv);
    }

    /// <summary>
    /// Builds a single &amp;path= convex-hull polygon that surrounds all fire detections.
    /// The hull is reduced to at most 10 vertices via Visvalingam-Whyatt simplification.
    /// Azure Maps static paths do not support gradients; a solid semi-transparent orange is used.
    /// </summary>
    /// <param name="fires">All fire detections to enclose.</param>
    public static string BuildFireCirclePaths(List<ViirsFireDetection> fires)
    {
        if (fires.Count == 0) return string.Empty;

        var points = fires.Select(f => (lat: f.Latitude, lng: f.Longitude)).Distinct().ToList();

        if (points.Count > 3)
        {
            double meanLat = points.Average(p => p.lat);
            double meanLng = points.Average(p => p.lng);
            int outlier = 0;
            double maxDist = -1;
            for (int i = 0; i < points.Count; i++)
            {
                double d = Dist2D(points[i], (meanLat, meanLng));
                if (d > maxDist) { maxDist = d; outlier = i; }
            }
            points.RemoveAt(outlier);
        }

        var hull = points.Count <= 2 ? points : ComputeConvexHull(points);

        while (hull.Count > 10)
            RemoveMinAreaVertex(hull);

        static string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);
        string positions = string.Join("|",
            hull.Append(hull[0]).Select(p => $"{Fmt(p.lng)} {Fmt(p.lat)}"));

        const string style = "lcFF4500|fcFF4500|la0.85|fa0.35|lw2";
        return $"&path={style}||{positions}";
    }

    // Gift wrapping (Jarvis march) producing a CCW convex hull.
    private static List<(double lat, double lng)> ComputeConvexHull(List<(double lat, double lng)> points)
    {
        int n = points.Count;
        int startIdx = 0;
        for (int i = 1; i < n; i++)
            if (points[i].lng < points[startIdx].lng ||
                (points[i].lng == points[startIdx].lng && points[i].lat < points[startIdx].lat))
                startIdx = i;

        var hull = new List<(double lat, double lng)>();
        int cur = startIdx;
        do
        {
            hull.Add(points[cur]);
            int nxt = (cur + 1) % n;
            for (int i = 0; i < n; i++)
            {
                double cross = Cross2D(points[cur], points[nxt], points[i]);
                if (cross < 0 || (cross == 0 && Dist2D(points[cur], points[i]) > Dist2D(points[cur], points[nxt])))
                    nxt = i;
            }
            cur = nxt;
        } while (cur != startIdx && hull.Count <= n);

        return hull;
    }

    // Removes the hull vertex whose removal causes the smallest area change (Visvalingam-Whyatt).
    private static void RemoveMinAreaVertex(List<(double lat, double lng)> hull)
    {
        int n = hull.Count, minIdx = 0;
        double minArea = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double area = Math.Abs(Cross2D(hull[(i - 1 + n) % n], hull[i], hull[(i + 1) % n]));
            if (area < minArea) { minArea = area; minIdx = i; }
        }
        hull.RemoveAt(minIdx);
    }

    private static double Cross2D(
        (double lat, double lng) o,
        (double lat, double lng) a,
        (double lat, double lng) b)
        => (a.lng - o.lng) * (b.lat - o.lat) - (a.lat - o.lat) * (b.lng - o.lng);

    private static double Dist2D((double lat, double lng) a, (double lat, double lng) b)
    {
        double dLat = a.lat - b.lat, dLng = a.lng - b.lng;
        return dLat * dLat + dLng * dLng;
    }

    /// <summary>Parses a raw VIIRS CSV response body into fire detection records.</summary>
    private static List<ViirsFireDetection> ParseViirsCsv(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Require at least a header row + one data row
        if (lines.Length < 2)
            return [];

        var result = new List<ViirsFireDetection>(lines.Length - 1);

        for (int i = 1; i < lines.Length; i++)
        {
            var cols = lines[i].Split(',');
            if (cols.Length < 14)
                continue;

            result.Add(new ViirsFireDetection(
                Latitude:   double.Parse(cols[0],  CultureInfo.InvariantCulture),
                Longitude:  double.Parse(cols[1],  CultureInfo.InvariantCulture),
                BrightTi4:  double.Parse(cols[2],  CultureInfo.InvariantCulture),
                Scan:       double.Parse(cols[3],  CultureInfo.InvariantCulture),
                Track:      double.Parse(cols[4],  CultureInfo.InvariantCulture),
                AcqDate:    DateOnly.Parse(cols[5], CultureInfo.InvariantCulture),
                AcqTime:    int.Parse(cols[6],     CultureInfo.InvariantCulture),
                Satellite:  cols[7].Trim(),
                Instrument: cols[8].Trim(),
                Confidence: cols[9].Trim(),
                Version:    cols[10].Trim(),
                BrightTi5:  double.Parse(cols[11], CultureInfo.InvariantCulture),
                Frp:        double.Parse(cols[12], CultureInfo.InvariantCulture),
                DayNight:   cols[13].Trim()
            ));
        }

        return result;
    }
}

/// <summary>One VIIRS SNPP NRT fire detection from the NASA FIRMS area API.</summary>
public record ViirsFireDetection(
    double   Latitude,
    double   Longitude,
    /// <summary>Brightness temperature channel I-4 (Kelvin).</summary>
    double   BrightTi4,
    /// <summary>Along-scan pixel size (km).</summary>
    double   Scan,
    /// <summary>Along-track pixel size (km).</summary>
    double   Track,
    DateOnly AcqDate,
    /// <summary>Acquisition time in HHMM UTC.</summary>
    int      AcqTime,
    /// <summary>Satellite identifier (e.g. "N" for Suomi-NPP).</summary>
    string   Satellite,
    string   Instrument,
    /// <summary>Detection confidence: "l" low, "n" nominal, "h" high.</summary>
    string   Confidence,
    string   Version,
    /// <summary>Brightness temperature channel I-5 (Kelvin).</summary>
    double   BrightTi5,
    /// <summary>Fire Radiative Power (MW).</summary>
    double   Frp,
    /// <summary>"D" daytime acquisition, "N" nighttime.</summary>
    string   DayNight
);
