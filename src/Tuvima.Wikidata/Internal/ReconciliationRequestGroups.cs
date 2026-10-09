using System.Text;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Groups identical reconciliation requests in a batch so each distinct request runs once.
/// Requests are identical when every option matches; query cleaners compare by delegate identity.
/// </summary>
internal sealed class ReconciliationRequestGroups
{
    /// <summary>The first request of each distinct group, in first-seen order.</summary>
    public required List<ReconciliationRequest> Representatives { get; init; }

    /// <summary>Input indices belonging to each group.</summary>
    public required List<List<int>> Members { get; init; }

    /// <summary>Group index for each input index.</summary>
    public required int[] GroupOf { get; init; }

    public static ReconciliationRequestGroups Create(IReadOnlyList<ReconciliationRequest> requests)
    {
        var representatives = new List<ReconciliationRequest>();
        var members = new List<List<int>>();
        var groupOf = new int[requests.Count];
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var delegateIds = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            ArgumentNullException.ThrowIfNull(request);
            var key = BuildKey(request, delegateIds);
            if (!byKey.TryGetValue(key, out var group))
            {
                group = representatives.Count;
                byKey[key] = group;
                representatives.Add(request);
                members.Add([]);
            }
            members[group].Add(i);
            groupOf[i] = group;
        }

        return new ReconciliationRequestGroups
        {
            Representatives = representatives,
            Members = members,
            GroupOf = groupOf
        };
    }

    private static string BuildKey(ReconciliationRequest request, Dictionary<object, int> delegateIds)
    {
        var sb = new StringBuilder();
        Append(sb, request.Query);
        AppendList(sb, request.Types);
        AppendList(sb, request.ExcludeTypes);
        Append(sb, request.TypeHierarchyDepth?.ToString() ?? "-");
        Append(sb, request.Limit.ToString());
        Append(sb, request.Language ?? "-");
        AppendList(sb, request.Languages);
        Append(sb, request.DiacriticInsensitive ? "1" : "0");

        if (request.Properties is { Count: > 0 } properties)
        {
            foreach (var property in properties)
            {
                Append(sb, property.PropertyId);
                AppendList(sb, property.Values);
            }
        }
        sb.Append('|');

        if (request.Cleaners is { Count: > 0 } cleaners)
        {
            foreach (var cleaner in cleaners)
            {
                if (!delegateIds.TryGetValue(cleaner, out var id))
                {
                    id = delegateIds.Count;
                    delegateIds[cleaner] = id;
                }
                Append(sb, id.ToString());
            }
        }

        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            Append(sb, "-");
            return;
        }
        sb.Append('[');
        foreach (var value in values)
            Append(sb, value);
        sb.Append(']');
    }

    // Length-prefixed so values containing separators can't collide.
    private static void Append(StringBuilder sb, string? value)
    {
        value ??= "";
        sb.Append(value.Length).Append(':').Append(value).Append(';');
    }
}
