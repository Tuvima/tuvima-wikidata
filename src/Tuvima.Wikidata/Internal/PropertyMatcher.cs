using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Matches query property values against Wikidata claim values based on data type.
/// Returns scores from 0-100 for each comparison.
/// </summary>
internal static class PropertyMatcher
{
    private static readonly Regex QidPattern = new(@"^Q\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Scores how well a query value matches a Wikidata data value (0-100).
    /// Dispatches to type-specific matching based on the data type.
    /// </summary>
    public static int Match(string queryValue, DataValue dataValue, string? dataType)
    {
        if (dataValue.Value is not JsonElement element)
            return 0;

        return dataType switch
        {
            "wikibase-item" => MatchItem(queryValue, element),
            "external-id" => MatchExternalId(queryValue, element),
            "string" => MatchString(queryValue, element),
            "monolingualtext" => MatchMonolingualText(queryValue, element),
            "quantity" => MatchQuantity(queryValue, element),
            "time" => MatchTime(queryValue, element),
            "url" => MatchUrl(queryValue, element),
            "globe-coordinate" => MatchCoordinates(queryValue, element),
            _ => MatchString(queryValue, element) // fallback to fuzzy string match
        };
    }

    internal static bool IsQid(string value) => QidPattern.IsMatch(value.Trim());

    private static int MatchItem(string queryValue, JsonElement element)
    {
        // Extract the QID from the item value
        if (!element.TryGetProperty("id", out var idProp))
            return 0;

        var entityId = idProp.GetString() ?? "";

        // QID comparison only. Name-valued constraints (e.g. "Neil Gaiman") are scored against the
        // referenced item's labels by ReconciliationScorer, which has access to fetched entities.
        return string.Equals(queryValue.Trim(), entityId, StringComparison.OrdinalIgnoreCase) ? 100 : 0;
    }

    private static int MatchExternalId(string queryValue, JsonElement element)
    {
        var value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        return string.Equals(queryValue.Trim(), value?.Trim(), StringComparison.OrdinalIgnoreCase) ? 100 : 0;
    }

    private static int MatchString(string queryValue, JsonElement element)
    {
        var value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        if (string.IsNullOrEmpty(value))
            return 0;

        return FuzzyMatcher.TokenSortRatio(queryValue, value);
    }

    private static int MatchMonolingualText(string queryValue, JsonElement element)
    {
        if (!element.TryGetProperty("text", out var textProp))
            return 0;

        var text = textProp.GetString() ?? "";
        return FuzzyMatcher.TokenSortRatio(queryValue, text);
    }

    private static int MatchQuantity(string queryValue, JsonElement element)
    {
        if (!element.TryGetProperty("amount", out var amountProp))
            return 0;

        var amountStr = amountProp.GetString() ?? "";
        // Wikidata prefixes positive amounts with '+'
        amountStr = amountStr.TrimStart('+');

        if (!double.TryParse(amountStr, CultureInfo.InvariantCulture, out var entityAmount))
            return 0;

        if (!double.TryParse(queryValue.Trim().TrimStart('+'), CultureInfo.InvariantCulture, out var queryAmount))
            return 0;

        return MatchDoubles(queryAmount, entityAmount);
    }

    /// <summary>
    /// Log-decay curve for numeric matching, from openrefine-wikibase:
    /// 100 * (atan(-log(|diff|)) / pi + 0.5)
    /// </summary>
    internal static int MatchDoubles(double a, double b)
    {
        if (Math.Abs(a - b) < 1e-10)
            return 100;

        var diff = Math.Abs(a - b);
        if (diff == 0)
            return 100;

        var score = 100.0 * (Math.Atan(-Math.Log(diff)) / Math.PI + 0.5);
        return (int)Math.Round(Math.Max(0, Math.Min(100, score)));
    }

    private static int MatchTime(string queryValue, JsonElement element)
    {
        if (!element.TryGetProperty("time", out var timeProp) ||
            !element.TryGetProperty("precision", out var precisionProp))
            return 0;

        var timeStr = timeProp.GetString() ?? "";
        var precision = precisionProp.GetInt32();

        // Parse Wikidata time format: +YYYY-MM-DDT00:00:00Z
        if (!TryParseWikidataTime(timeStr, out var entityYear, out var entityMonth, out var entityDay))
            return 0;

        // Parse query date (supports YYYY, YYYY-MM, YYYY-MM-DD)
        if (!TryParseQueryDate(queryValue.Trim(), out var queryYear, out var queryMonth, out var queryDay))
            return 0;

        // Coarser than a year (8=decade, 7=century, 6=millennium, ...): compare at that granularity.
        if (precision < 9)
            return MatchCoarseYear(queryYear, entityYear, precision) ? 100 : 0;

        // Compare based on precision: 9=year, 10=year+month, 11=full date
        if (precision >= 9 && entityYear != queryYear)
            return 0;
        if (precision >= 10 && queryMonth.HasValue && entityMonth != queryMonth)
            return 0;
        if (precision >= 11 && queryDay.HasValue && entityDay != queryDay)
            return 0;

        return 100;
    }

    /// <summary>
    /// Decades match by floor(year / 10) ("1980s" is stored as 1980). Centuries and wider are
    /// stored inconsistently on Wikidata (first or last year of the span), so they match when
    /// the query lies within one span width of the stored year.
    /// </summary>
    internal static bool MatchCoarseYear(int queryYear, int entityYear, int precision)
    {
        if (precision == 8)
            return FloorDiv(queryYear, 10) == FloorDiv(entityYear, 10);

        var span = Math.Pow(10, Math.Min(9, 9 - precision));
        return Math.Abs((double)queryYear - entityYear) < span;
    }

    private static int FloorDiv(int value, int divisor)
        => (int)Math.Floor((double)value / divisor);

    private static bool TryParseWikidataTime(string timeStr, out int year, out int month, out int day)
    {
        year = month = day = 0;

        // Format: +YYYY-MM-DDT00:00:00Z or -YYYY-MM-DDT00:00:00Z
        var s = timeStr.TrimStart('+', '-');
        var parts = s.Split('T')[0].Split('-');

        if (parts.Length < 3)
            return false;

        if (!int.TryParse(parts[0], out year) ||
            !int.TryParse(parts[1], out month) ||
            !int.TryParse(parts[2], out day))
            return false;

        if (timeStr.StartsWith('-'))
            year = -year;

        return true;
    }

    private static bool TryParseQueryDate(string dateStr, out int year, out int? month, out int? day)
    {
        year = 0;
        month = null;
        day = null;

        var parts = dateStr.Split('-');
        if (parts.Length == 0 || !int.TryParse(parts[0], out year))
            return false;

        if (parts.Length >= 2 && int.TryParse(parts[1], out var m))
            month = m;
        if (parts.Length >= 3 && int.TryParse(parts[2], out var d))
            day = d;

        return true;
    }

    private static int MatchUrl(string queryValue, JsonElement element)
    {
        var entityUrl = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        if (string.IsNullOrEmpty(entityUrl))
            return 0;

        // Strip scheme for comparison
        var normalizedQuery = StripScheme(queryValue.Trim());
        var normalizedEntity = StripScheme(entityUrl.Trim());

        return string.Equals(normalizedQuery, normalizedEntity, StringComparison.OrdinalIgnoreCase) ? 100 : 0;
    }

    private static string StripScheme(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url[8..];
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return url[7..];
        return url;
    }

    private static int MatchCoordinates(string queryValue, JsonElement element)
    {
        if (!element.TryGetProperty("latitude", out var latProp) ||
            !element.TryGetProperty("longitude", out var lonProp))
            return 0;

        var entityLat = latProp.GetDouble();
        var entityLon = lonProp.GetDouble();

        // Parse query as "lat,lon"
        var parts = queryValue.Split(',');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var queryLat) ||
            !double.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var queryLon))
            return 0;

        // Flat-earth distance approximation in km
        var distKm = FlatEarthDistanceKm(queryLat, queryLon, entityLat, entityLon);

        // Score linearly decreases to 0 at 1 km distance
        return (int)Math.Round(100.0 * Math.Max(0, 1.0 - distKm));
    }

    private static double FlatEarthDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var avgLat = (lat1 + lat2) / 2.0 * Math.PI / 180.0;

        var x = dLon * Math.Cos(avgLat);
        var y = dLat;

        return earthRadiusKm * Math.Sqrt(x * x + y * y);
    }
}
