namespace _1Rad.Application.Interfaces;

/// <summary>
/// Level of access a hospital has to a product module.
/// </summary>
public enum ModuleAccess
{
    /// <summary>Module not enabled and no active grace — block.</summary>
    None = 0,
    /// <summary>Fully enabled — allow everything.</summary>
    Full = 1,
    /// <summary>PACS downgrade grace window — allow READS (and cleanup deletes)
    /// of existing studies, but block ingestion / reporting writes.</summary>
    GraceRead = 2,
}

/// <summary>
/// Resolves which product modules (RIS / PACS — see
/// <c>ModuleConstants</c>) a hospital's current subscription enables.
/// Backed by a short-lived cache so the per-request authorization check
/// ([RequiresModule]) doesn't hit the database every call, while SKU
/// changes still propagate within a minute without re-login.
/// </summary>
public interface IModuleEntitlementService
{
    /// <summary>Normalised set of enabled module codes for the hospital (upper-cased).</summary>
    Task<IReadOnlySet<string>> GetEnabledModulesAsync(Guid hospitalId, CancellationToken cancellationToken = default);

    /// <summary>True if the hospital's subscription includes the given module code.</summary>
    Task<bool> HasModuleAsync(Guid hospitalId, string module, CancellationToken cancellationToken = default);

    /// <summary>
    /// Access level for a module: Full when enabled, GraceRead when PACS was
    /// removed but is still inside its read-only grace window, else None.
    /// </summary>
    Task<ModuleAccess> GetModuleAccessAsync(Guid hospitalId, string module, CancellationToken cancellationToken = default);

    /// <summary>Drop the cached entry (call after a subscription/SKU change).</summary>
    void Invalidate(Guid hospitalId);
}
