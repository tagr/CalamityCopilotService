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
    /// Builds one &amp;path= square polygon per fire detection (top 10 by FRP).
    /// Azure Maps supports fc/fa fill on closed path geometries.
    /// <para>
    /// Both opacity and square size are driven by FRP — higher power = larger and darker square.
    /// </para>
    /// </summary>
    /// <param name="fires">Detections to render; only the 10 highest-FRP are used.</param>
    public static string BuildFireCirclePaths(List<ViirsFireDetection> fires)
    {
        const double EarthRadius = 6_371_000.0;

        // Higher FRP → larger square (half-side length in metres)
        const int minHalfSideM = 1_000;
        const int maxHalfSideM = 5_000;

        // Higher FRP → higher opacity (darker)
        const double minAlpha = 0.1;
        const double maxAlpha = 0.5;

        var top10 = fires.OrderByDescending(f => f.Frp).Take(10).ToList();

        double minFrp  = top10.Min(f => f.Frp);
        double maxFrp  = top10.Max(f => f.Frp);
        double frpSpan = maxFrp - minFrp;

        return string.Concat(top10.Select(f =>
        {
            // Normalise FRP 0–1 within this set; guard against all fires having equal FRP.
            double t = frpSpan > 0 ? (f.Frp - minFrp) / frpSpan : 0.5;

            double alpha    = minAlpha + t * (maxAlpha - minAlpha);
            string a        = alpha.ToString("F2", CultureInfo.InvariantCulture);
            double halfSide = minHalfSideM + t * (maxHalfSideM - minHalfSideM);

            // Convert half-side to degree offsets using spherical-Earth approximation.
            double latDelta = halfSide / EarthRadius * (180.0 / Math.PI);
            double lngDelta = halfSide / EarthRadius / Math.Cos(f.Latitude * Math.PI / 180.0) * (180.0 / Math.PI);

            double n = f.Latitude  + latDelta;
            double s = f.Latitude  - latDelta;
            double e = f.Longitude + lngDelta;
            double w = f.Longitude - lngDelta;

            string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);

            // NW → NE → SE → SW → NW (closed)
            string positions = $"{Fmt(w)} {Fmt(n)}|{Fmt(e)} {Fmt(n)}|{Fmt(e)} {Fmt(s)}|{Fmt(w)} {Fmt(s)}|{Fmt(w)} {Fmt(n)}";

            // lc/fc = border/fill color, la/fa = per-fire alpha, lw = border width px
            string style = $"lcFF4500|fcFF4500|la{a}|fa{a}|lw1";

            return $"&path={style}||{positions}";
        }));
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
