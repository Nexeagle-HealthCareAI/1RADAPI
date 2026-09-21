namespace _1Rad.Application.Common;

public enum SourceKind
{
    /// <summary>A real referral partner (merge-resolved to its primary).</summary>
    Partner,
    /// <summary>Walk-in / self referral - a bucket, not a partner.</summary>
    Self,
    /// <summary>The visit names a referrer that has no partner record (imports, hand-typed names).</summary>
    Unlinked,
    /// <summary>The visit records no referrer at all.</summary>
    Unattributed,
}

/// <param name="Key">Stable grouping key: root id, "self", "name:XYZ" or "unattributed".</param>
/// <param name="RootId">The primary partner's id for a Partner; Guid.Empty otherwise.</param>
public sealed record SourceRef(string Key, SourceKind Kind, string DisplayName, Guid RootId);

/// <summary>
/// The ONE definition of "which referral source does this visit belong to". The
/// Source Analytics query and the Volume Matrix used to attribute differently (one by the
/// visit's referrer name, one by the patient's current referrer), so the same visit could
/// land under different partners on different tabs.
///
/// Rules, in order:
///   1. The visit's own ReferredBy name wins. "Self" is the walk-in bucket; a name that
///      matches a partner record (live preferred over deleted) resolves to that partner's
///      merge-root.
///   2. Otherwise the patient's referrer link is used (ChangeReferrer re-points it, so it is
///      only a fallback).
///   3. A named referrer with no partner record is an UNLINKED source - kept separate and
///      labelled, never mistaken for Self or dropped.
///   4. No name and no link: UNATTRIBUTED, so missing data is measurable instead of hidden.
/// </summary>
public sealed class ReferralAttribution
{
    public const string SelfKey = "self";
    public const string UnattributedKey = "unattributed";

    public sealed record Entry(Guid ReferrerId, Guid? MergedIntoId, string? Name, string? Contact, string? Address, DateTime? DeletedAt);

    private readonly Dictionary<Guid, Entry> _byId;
    private readonly Dictionary<string, Guid> _nameToId = new(StringComparer.OrdinalIgnoreCase);

    public ReferralAttribution(IEnumerable<Entry> referrers)
    {
        _byId = referrers.ToDictionary(r => r.ReferrerId);
        // Prefer a live partner over a tombstoned one of the same name.
        foreach (var r in _byId.Values.OrderBy(r => r.DeletedAt != null).ThenBy(r => r.ReferrerId))
        {
            var key = (r.Name ?? string.Empty).Trim();
            if (key.Length > 0) _nameToId.TryAdd(key, r.ReferrerId);
        }
    }

    public IReadOnlyDictionary<Guid, Entry> ById => _byId;

    /// <summary>Follows MergedIntoId to the primary partner (cycle-safe).</summary>
    public Guid Resolve(Guid id)
    {
        var visited = new HashSet<Guid>();
        var current = id;
        while (_byId.TryGetValue(current, out var e) && e.MergedIntoId.HasValue && visited.Add(current))
            current = e.MergedIntoId.Value;
        return current;
    }

    /// <summary>Group key for a referrer id: merge-resolved, the Self record collapsed into <see cref="SelfKey"/>. Null for an empty id.</summary>
    public string? KeyForReferrer(Guid id)
    {
        if (id == Guid.Empty) return null;
        var root = Resolve(id);
        // An id that is not in this centre's registry (a stale link, another centre's record)
        // is not a partner we can name - callers treat it as "no usable link".
        if (!_byId.TryGetValue(root, out var e)) return null;
        if (NameNormalizer.SameName(e.Name, "Self")) return SelfKey;
        return root.ToString();
    }

    public SourceRef Attribute(string? referredBy, Guid? patientReferrerId)
    {
        var name = (referredBy ?? string.Empty).Trim();

        if (name.Length > 0)
        {
            if (NameNormalizer.SameName(name, "Self")) return SelfRef();
            if (_nameToId.TryGetValue(name, out var byName) && KeyForReferrer(byName) is { } nameKey)
                return FromKey(nameKey);
        }

        if (patientReferrerId.HasValue && KeyForReferrer(patientReferrerId.Value) is { } patientKey)
            return FromKey(patientKey);

        return name.Length > 0
            ? new SourceRef("name:" + name.ToUpperInvariant(), SourceKind.Unlinked, name, Guid.Empty)
            : new SourceRef(UnattributedKey, SourceKind.Unattributed, "Unattributed", Guid.Empty);
    }

    /// <summary>Every referrer id that resolves to the same primary as <paramref name="anyMember"/> (the primary and its merged duplicates).</summary>
    public List<Guid> AliasIdsOf(Guid anyMember)
    {
        var root = Resolve(anyMember);
        return _byId.Keys.Where(id => Resolve(id) == root).ToList();
    }

    /// <summary>The names those aliases go by on visits (for a name-based pre-filter).</summary>
    public List<string> AliasNamesOf(Guid anyMember) =>
        AliasIdsOf(anyMember)
            .Select(id => _byId[id].Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public Entry? RootEntry(Guid anyMember) => _byId.TryGetValue(Resolve(anyMember), out var e) ? e : null;

    private static SourceRef SelfRef() => new(SelfKey, SourceKind.Self, "Self / Walk-in", Guid.Empty);

    private SourceRef FromKey(string key)
    {
        if (key == SelfKey) return SelfRef();
        var root = Guid.Parse(key);
        var name = _byId.TryGetValue(root, out var e) && !string.IsNullOrWhiteSpace(e.Name) ? e.Name! : "Unknown";
        return new SourceRef(key, SourceKind.Partner, name, root);
    }
}
