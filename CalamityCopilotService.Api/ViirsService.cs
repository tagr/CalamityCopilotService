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
    /// Builds &amp;pins= query-string segments placing a custom flame icon at each fire detection,
    /// scaled small / medium / large by FRP (Fire Radiative Power).
    /// Capped at 100 total distinct locations to stay within Azure Maps static-image URL limits.
    /// </summary>
    /// <param name="fires">Fire detections to mark.</param>
    public string BuildFireCirclePaths(List<ViirsFireDetection> fires)
    {
        if (fires.Count == 0) return string.Empty;

        var iconUrl = config["FirePin:IconUrl"]
                      ?? throw new InvalidOperationException("FirePin:IconUrl is not configured.");

        static string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);
        string encodedIcon = Uri.EscapeDataString(iconUrl);

        // Deduplicate by location, keep strongest detection, cap at 100.
        var pts = fires
            .GroupBy(f => (f.Longitude, f.Latitude))
            .Select(g => g.MaxBy(f => f.Frp)!)
            .OrderByDescending(f => f.Frp)
            .Take(100)
            .OrderBy(f => f.Frp)
            .ToList();

        // Split into equal thirds by FRP rank: small / medium / large.
        int n = pts.Count;
        int third = n / 3;
        var small  = pts.Take(third).ToList();
        var medium = pts.Skip(third).Take(third).ToList();
        var large  = pts.Skip(2 * third).ToList();

        static string Pins(string scale, List<ViirsFireDetection> group, string icon) =>
            $"&pins=custom|sc{scale}||" +
            string.Join("|", group.Select(f => $"{Fmt(f.Longitude)} {Fmt(f.Latitude)}")) +
            $"||{icon}";

        var sb = new System.Text.StringBuilder();
        if (small.Count  > 0) sb.Append(Pins("0.5", small,  encodedIcon));
        if (medium.Count > 0) sb.Append(Pins("0.9", medium, encodedIcon));
        if (large.Count  > 0) sb.Append(Pins("1.3", large,  encodedIcon));

        return sb.ToString();
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
