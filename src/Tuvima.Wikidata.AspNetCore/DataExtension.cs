using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tuvima.Wikidata.AspNetCore;

/// <summary>
/// W3C data extension ("Add columns from reconciled values" in OpenRefine): property proposals
/// for a type, and property values for reconciled entity IDs.
/// </summary>
internal static class DataExtension
{
    /// <summary>Properties proposed when the type has no P1963 ("properties for this type") statements.</summary>
    private static readonly string[] GenericProperties = ["P31", "P279", "P17", "P571", "P18", "P856", "P646"];

    public static bool IsEntityId(string? value, char prefix)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2 || char.ToUpperInvariant(value[0]) != prefix || value[1] == '0')
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
                return false;
        }
        return true;
    }

    public static async Task<W3cProposeResponse> ProposeAsync(
        WikidataReconciler reconciler, string? type, int limit, string? language, CancellationToken cancellationToken)
    {
        var propertyIds = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (type is not null)
        {
            var typeClaims = await reconciler.Entities.GetPropertiesAsync([type], ["P1963"], language, cancellationToken)
                .ConfigureAwait(false);
            if (typeClaims.TryGetValue(type, out var claims) && claims.TryGetValue("P1963", out var forType))
            {
                foreach (var value in BestRank(forType))
                {
                    if (value.EntityId is { } pid && IsEntityId(pid, 'P') && !names.ContainsKey(pid))
                    {
                        propertyIds.Add(pid);
                        names[pid] = value.EntityLabel ?? pid;
                    }
                }
            }
        }

        if (propertyIds.Count == 0)
        {
            propertyIds.AddRange(GenericProperties);
            foreach (var (pid, label) in await reconciler.Entities.GetPropertyLabelsAsync(GenericProperties, language, cancellationToken)
                         .ConfigureAwait(false))
                names[pid] = label;
        }

        return new W3cProposeResponse
        {
            Type = type,
            Limit = limit,
            Properties = propertyIds.Take(limit)
                .Select(pid => new W3cProposedProperty { Id = pid, Name = names.GetValueOrDefault(pid, pid) })
                .ToList()
        };
    }

    public static async Task<W3cExtendResponse> ExtendAsync(
        WikidataReconciler reconciler, W3cExtendRequest request, string? language, CancellationToken cancellationToken)
    {
        var ids = request.Ids!.Select(id => id.ToUpperInvariant()).Distinct().ToList();
        var properties = request.Properties!;
        var propertyIds = properties.Select(p => p.Id!.ToUpperInvariant()).Distinct().ToList();

        var valuesTask = reconciler.Entities.GetPropertiesAsync(ids, propertyIds, language, cancellationToken);
        var labelsTask = reconciler.Entities.GetPropertyLabelsAsync(propertyIds, language, cancellationToken);
        await Task.WhenAll(valuesTask, labelsTask).ConfigureAwait(false);
        var values = await valuesTask.ConfigureAwait(false);
        var labels = await labelsTask.ConfigureAwait(false);

        var rows = new Dictionary<string, Dictionary<string, List<W3cExtendValue>>>(StringComparer.Ordinal);
        foreach (var id in request.Ids!)
        {
            var row = new Dictionary<string, List<W3cExtendValue>>(StringComparer.Ordinal);
            values.TryGetValue(id.ToUpperInvariant(), out var claimsByProperty);
            foreach (var property in properties)
            {
                var pid = property.Id!.ToUpperInvariant();
                var cells = new List<W3cExtendValue>();
                if (claimsByProperty is not null && claimsByProperty.TryGetValue(pid, out var claims))
                {
                    var limit = ReadLimit(property.Settings);
                    foreach (var value in BestRank(claims))
                    {
                        if (limit > 0 && cells.Count >= limit)
                            break;
                        cells.Add(ToCell(value));
                    }
                }
                row[property.Id!] = cells;
            }
            rows[id] = row;
        }

        return new W3cExtendResponse
        {
            Meta = properties.Select(p => new W3cExtendMeta
            {
                Id = p.Id!,
                Name = labels.GetValueOrDefault(p.Id!.ToUpperInvariant(), p.Id!),
                Settings = p.Settings
            }).ToList(),
            Rows = rows
        };
    }

    /// <summary>Preferred-rank values if any exist, otherwise normal-rank; deprecated values are never returned.</summary>
    private static IEnumerable<WikidataValue> BestRank(IReadOnlyList<WikidataClaim> claims)
    {
        var usable = claims.Where(c => c.Value is not null && !c.Rank.Equals("deprecated", StringComparison.OrdinalIgnoreCase)).ToList();
        var preferred = usable.Where(c => c.Rank.Equals("preferred", StringComparison.OrdinalIgnoreCase)).ToList();
        return (preferred.Count > 0 ? preferred : usable).Select(c => c.Value!);
    }

    private static int ReadLimit(Dictionary<string, JsonElement>? settings)
    {
        if (settings is null || !settings.TryGetValue("limit", out var limit))
            return 0;
        return limit.ValueKind switch
        {
            JsonValueKind.Number when limit.TryGetInt32(out var n) => Math.Max(0, n),
            JsonValueKind.String when int.TryParse(limit.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => Math.Max(0, n),
            _ => 0
        };
    }

    internal static W3cExtendValue ToCell(WikidataValue value) => value.Kind switch
    {
        WikidataValueKind.EntityId => new W3cExtendValue { Id = value.EntityId, Name = value.EntityLabel ?? value.EntityId },
        WikidataValueKind.Quantity when value.Amount is { } amount => new W3cExtendValue { Float = (double)amount },
        WikidataValueKind.Time => ToDate(value.RawValue) is { } date ? new W3cExtendValue { Date = date } : new W3cExtendValue { Str = value.ToDisplayString() },
        _ => new W3cExtendValue { Str = value.ToDisplayString() }
    };

    /// <summary>
    /// Converts a Wikidata time ("+1952-03-11T00:00:00Z", or "+1952-00-00T00:00:00Z" for year precision)
    /// to an ISO-8601 date. Unknown month/day components become 01.
    /// </summary>
    internal static string? ToDate(string raw)
    {
        if (raw.Length < 2)
            return null;
        var sign = raw[0] == '-' ? "-" : "";
        var body = raw.TrimStart('+', '-');
        var t = body.IndexOf('T');
        var datePart = t >= 0 ? body[..t] : body;
        var parts = datePart.Split('-');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var year) ||
            !int.TryParse(parts[1], out var month) || !int.TryParse(parts[2], out var day))
            return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"{sign}{year:D4}-{Math.Max(1, month):D2}-{Math.Max(1, day):D2}T00:00:00+00:00");
    }
}

// ─── W3C data extension models ──────────────────────────────────

internal sealed class W3cExtendRequest
{
    [JsonPropertyName("ids")]
    public List<string>? Ids { get; set; }

    [JsonPropertyName("properties")]
    public List<W3cExtendProperty>? Properties { get; set; }
}

internal sealed class W3cExtendProperty
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("settings")]
    public Dictionary<string, JsonElement>? Settings { get; set; }
}

internal sealed class W3cExtendResponse
{
    [JsonPropertyName("meta")]
    public List<W3cExtendMeta> Meta { get; set; } = [];

    [JsonPropertyName("rows")]
    public Dictionary<string, Dictionary<string, List<W3cExtendValue>>> Rows { get; set; } = [];
}

internal sealed class W3cExtendMeta
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("settings")]
    public Dictionary<string, JsonElement>? Settings { get; set; }
}

internal sealed class W3cExtendValue
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("str")]
    public string? Str { get; set; }

    [JsonPropertyName("float")]
    public double? Float { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }
}

internal sealed class W3cProposeResponse
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("properties")]
    public List<W3cProposedProperty> Properties { get; set; } = [];
}

internal sealed class W3cProposedProperty
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal sealed class ExtendService
{
    [JsonPropertyName("propose_properties")]
    public SuggestService? ProposeProperties { get; set; }

    [JsonPropertyName("property_settings")]
    public List<PropertySetting> PropertySettings { get; set; } = [];
}

internal sealed class PropertySetting
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("default")]
    public int Default { get; set; }

    [JsonPropertyName("help_text")]
    public string HelpText { get; set; } = "";
}
