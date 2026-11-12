using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// An API key a centre's admin issues to an outside system — a hospital HIS, a
/// DICOMweb viewer, a modality or the on-prem bridge (DB script 101).
///
/// The key opens only the integration endpoints (DICOMweb, modality worklist,
/// FHIR) of its own centre, for the scopes in <see cref="Scopes"/>. The secret is
/// never stored: <see cref="SecretHash"/> is SHA-256 of the whole key.
/// </summary>
public class IntegrationClient : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Public, unique part of the key (1rk_&lt;KeyId&gt;_&lt;secret&gt;).</summary>
    public string KeyId { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    /// <summary>Comma list of <see cref="Constants.IntegrationScopes"/> values.</summary>
    public string Scopes { get; set; } = string.Empty;
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
