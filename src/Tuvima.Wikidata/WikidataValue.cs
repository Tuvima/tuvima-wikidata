namespace Tuvima.Wikidata;

/// <summary>
/// The kind of value stored in a Wikidata claim.
/// </summary>
public enum WikidataValueKind
{
    /// <summary>Plain string value.</summary>
    String,

    /// <summary>Reference to another Wikidata entity (QID).</summary>
    EntityId,

    /// <summary>Date/time value with precision.</summary>
    Time,

    /// <summary>Numeric quantity, optionally with a unit.</summary>
    Quantity,

    /// <summary>Geographic coordinates (latitude/longitude).</summary>
    GlobeCoordinate,

    /// <summary>Text tagged with a language code.</summary>
    MonolingualText,

    /// <summary>Value type not recognized by this library version.</summary>
    Unknown
}

/// <summary>
/// A typed value from a Wikidata claim or qualifier.
/// Use <see cref="Kind"/> to determine which properties are populated.
/// </summary>
public sealed class WikidataValue
{
    /// <summary>
    /// The kind of value, determining which typed properties are populated.
    /// </summary>
    public WikidataValueKind Kind { get; init; }

    /// <summary>
    /// Raw string representation of the value. Always populated.
    /// </summary>
    public required string RawValue { get; init; }

    /// <summary>
    /// For EntityId values: the entity ID (e.g., "Q42"). Null for other kinds.
    /// </summary>
    public string? EntityId { get; init; }

    /// <summary>
    /// For EntityId values: the human-readable label of the referenced entity
    /// in the requested language. Automatically populated by
    /// <see cref="WikidataReconciler.GetEntitiesAsync(IReadOnlyList{string}, bool, string?, CancellationToken)"/> (with resolveEntityLabels)
    /// and <see cref="WikidataReconciler.GetPropertiesAsync"/>.
    /// Can also be set by consumers for custom label resolution scenarios.
    /// </summary>
    public string? EntityLabel { get; set; }

    /// <summary>
    /// For Time values: precision level (9=year, 10=month, 11=day). Null for other kinds.
    /// </summary>
    public int? TimePrecision { get; init; }

    /// <summary>
    /// For Quantity values: the numeric amount. Null for other kinds.
    /// </summary>
    public decimal? Amount { get; init; }

    /// <summary>
    /// For Quantity values: the unit entity URI (e.g., "http://www.wikidata.org/entity/Q11573" for metres).
    /// Null if dimensionless or for other kinds.
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>
    /// For GlobeCoordinate values: latitude in decimal degrees. Null for other kinds.
    /// </summary>
    public double? Latitude { get; init; }

    /// <summary>
    /// For GlobeCoordinate values: longitude in decimal degrees. Null for other kinds.
    /// </summary>
    public double? Longitude { get; init; }

    /// <summary>
    /// For MonolingualText values: the language code. Null for other kinds.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Returns a human-readable display string for this value.
    /// For dates: formats based on precision (e.g., "11 March 1952" for day precision, "1952" for year).
    /// For quantities: formats with unit QID if present (e.g., "42", "1.8 Q11573").
    /// For coordinates: formats as "51.5074, -0.1278".
    /// For entity IDs: returns the QID (e.g., "Q42").
    /// For strings/monolingual text: returns the raw text.
    /// </summary>
    public string ToDisplayString()
    {
        return Kind switch
        {
            WikidataValueKind.Time => FormatTime(),
            WikidataValueKind.Quantity => FormatQuantity(),
            WikidataValueKind.GlobeCoordinate => FormatCoordinates(),
            WikidataValueKind.EntityId => EntityLabel ?? EntityId ?? RawValue,
            WikidataValueKind.MonolingualText => RawValue,
            _ => RawValue
        };
    }

    private string FormatTime()
    {
        // Parse Wikidata time format: +YYYY-MM-DDT00:00:00Z
        var timeStr = RawValue.TrimStart('+', '-');
        var parts = timeStr.Split('T')[0].Split('-');

        if (parts.Length < 3)
            return RawValue;

        if (!int.TryParse(parts[0], out var year) ||
            !int.TryParse(parts[1], out var month) ||
            !int.TryParse(parts[2], out var day))
            return RawValue;

        var isNegative = RawValue.StartsWith('-');
        var precision = TimePrecision ?? 11;

        return precision switch
        {
            >= 11 when month >= 1 && month <= 12 =>
                $"{day} {MonthName(month)} {(isNegative ? $"{year} BCE" : year.ToString())}",
            10 when month >= 1 && month <= 12 =>
                $"{MonthName(month)} {(isNegative ? $"{year} BCE" : year.ToString())}",
            _ => isNegative ? $"{year} BCE" : year.ToString()
        };
    }

    private static string MonthName(int month) => month switch
    {
        1 => "January", 2 => "February", 3 => "March", 4 => "April",
        5 => "May", 6 => "June", 7 => "July", 8 => "August",
        9 => "September", 10 => "October", 11 => "November", 12 => "December",
        _ => month.ToString()
    };

    private string FormatQuantity()
    {
        var amount = Amount?.ToString() ?? RawValue;
        if (Unit is not null)
        {
            // Extract QID from unit URI (e.g., "http://www.wikidata.org/entity/Q11573" -> "Q11573")
            var lastSlash = Unit.LastIndexOf('/');
            var unitId = lastSlash >= 0 ? Unit[(lastSlash + 1)..] : Unit;
            return $"{amount} {unitId}";
        }
        return amount;
    }

    private string FormatCoordinates()
    {
        if (Latitude.HasValue && Longitude.HasValue)
            return $"{Latitude.Value:F4}, {Longitude.Value:F4}";
        return RawValue;
    }

    /// <summary>
    /// For string/external-id/commonsMedia values: constructs a Wikimedia Commons file URL.
    /// Returns null if this value is not a Commons filename.
    /// </summary>
    public string? ToCommonsImageUrl()
    {
        if (Kind != WikidataValueKind.String || string.IsNullOrWhiteSpace(RawValue))
            return null;

        return $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(RawValue)}";
    }
}
