namespace _1Rad.Domain.Constants;

/// <summary>
/// Product modules a hospital's subscription can enable. Stored on
/// <see cref="Entities.HospitalSubscription.Modules"/> as a comma-separated
/// list (e.g. "RIS,PACS") so a center can run RIS-only, PACS-only, or both.
/// Per-hospital (not per group/user) — centers in one HospitalGroup may run
/// different SKUs.
///
/// Reporting is deliberately NOT a module: every SKU includes it (RIS-only
/// reports against visits, PACS-only against studies).
/// </summary>
public static class ModuleConstants
{
    /// <summary>
    /// Radiology Information System — appointments, worklist boards, visit
    /// billing, referrals. RIS-only customers may attach small non-DICOM
    /// documents (PDF/JPG) to visits but cannot upload or view DICOM.
    /// </summary>
    public const string Ris = "RIS";

    /// <summary>
    /// Cloud PACS — DICOM ingestion (bridge + web upload), extraction/HTJ2K
    /// pipeline, slice manifest, viewer/MPR.
    /// </summary>
    public const string Pacs = "PACS";

    /// <summary>Default for new and legacy subscriptions: the full product.</summary>
    public const string DefaultModules = "RIS,PACS";

    /// <summary>Parse a stored comma-list into a normalised set (upper-cased, trimmed).</summary>
    public static HashSet<string> Parse(string? modules)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in (modules ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(m.ToUpperInvariant());
        return set;
    }
}
