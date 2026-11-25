using System.Collections.Concurrent;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Remembers entities this reconciler has already fetched, so asking again for the same QID, with the same or a smaller
/// set of properties, does not cost another request. The response cache cannot do this: it is keyed by the whole URL,
/// so the same QID asked for as "labels" after "claims" or "claims" after "claims and sitelinks" is a different key.
/// A held entity answers a request when it was fetched with every requested property and either in the same
/// language list or in all languages. Bounded and expiring.
/// </summary>
internal sealed class EntityMemory
{
    private const int MaxRichEntries = 2_500;     // Entities with claims: about 30 kB each, so this is a few tens of MB at most.
    private const int MaxLightEntries = 60_000;   // Labels or sitelinks only: a few hundred bytes each.

    private readonly ConcurrentDictionary<string, List<Held>> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _clock;
    private int _richCount;
    private int _lightCount;

    public EntityMemory(TimeSpan lifetime, TimeProvider? clock = null)
    {
        _lifetime = lifetime;
        _clock = clock ?? TimeProvider.System;
    }

    public int RichCount => Volatile.Read(ref _richCount);

    public int LightCount => Volatile.Read(ref _lightCount);

    /// <summary>Returns the held entity that answers a request, or null.</summary>
    public WikidataEntity? TryGet(string id, string? language, string props)
    {
        if (_lifetime <= TimeSpan.Zero || !_held.TryGetValue(id, out var list))
            return null;

        var wanted = Split(props);
        var now = _clock.GetUtcNow();
        lock (list)
        {
            foreach (var held in list)
            {
                if (held.ExpiresAt <= now)
                    continue;
                if (held.Language is not null && !string.Equals(held.Language, language, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (wanted.All(held.Props.Contains))
                    return held.Entity;
            }
        }

        return null;
    }

    public void Remember(string id, string? language, string props, WikidataEntity entity)
    {
        if (_lifetime <= TimeSpan.Zero || string.IsNullOrWhiteSpace(id))
            return;

        var rich = props.Contains("claims", StringComparison.Ordinal);
        if (rich ? RichCount >= MaxRichEntries : LightCount >= MaxLightEntries)
            Prune(rich);
        if (rich ? RichCount >= MaxRichEntries : LightCount >= MaxLightEntries)
            return;

        var list = _held.GetOrAdd(id, _ => []);
        lock (list)
        {
            list.RemoveAll(held => held.Rich == rich && held.Language == language && held.Props.SetEquals(Split(props)));
            list.Add(new Held(language, Split(props), entity, rich, _clock.GetUtcNow().Add(_lifetime)));
            if (rich) Interlocked.Increment(ref _richCount); else Interlocked.Increment(ref _lightCount);
        }
    }

    private void Prune(bool rich)
    {
        var now = _clock.GetUtcNow();
        foreach (var pair in _held)
        {
            lock (pair.Value)
            {
                var removed = pair.Value.RemoveAll(held => held.Rich == rich && held.ExpiresAt <= now);
                Adjust(rich, -removed);
                if (pair.Value.Count == 0)
                    _held.TryRemove(pair.Key, out _);
            }
        }

        // Still full: drop the oldest half so the memory keeps room for what is being fetched now.
        var limit = rich ? MaxRichEntries : MaxLightEntries;
        if ((rich ? RichCount : LightCount) < limit)
            return;

        var oldest = _held
            .SelectMany(pair => pair.Value.Where(held => held.Rich == rich).Select(held => (pair.Key, held)))
            .OrderBy(entry => entry.held.ExpiresAt)
            .Take(limit / 2)
            .ToList();
        foreach (var (key, held) in oldest)
        {
            if (_held.TryGetValue(key, out var list))
            {
                lock (list)
                {
                    if (list.Remove(held))
                        Adjust(rich, -1);
                }
            }
        }
    }

    private void Adjust(bool rich, int delta)
    {
        if (rich) Interlocked.Add(ref _richCount, delta); else Interlocked.Add(ref _lightCount, delta);
    }

    private static HashSet<string> Split(string props) =>
        new(props.Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private sealed record Held(string? Language, HashSet<string> Props, WikidataEntity Entity, bool Rich, DateTimeOffset ExpiresAt);
}
