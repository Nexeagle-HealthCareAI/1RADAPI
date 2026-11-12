namespace _1Rad.Domain.Constants;

/// <summary>
/// What an integration key (<see cref="Entities.IntegrationClient"/>) may do.
/// Staff sign-ins are not limited by these — they follow their roles as usual.
/// </summary>
public static class IntegrationScopes
{
    /// <summary>Search and download images (QIDO-RS, WADO-RS).</summary>
    public const string DicomWebRead = "dicomweb.read";
    /// <summary>Send images in (STOW-RS).</summary>
    public const string DicomWebWrite = "dicomweb.write";
    /// <summary>Read today's scheduled scans for the modality worklist.</summary>
    public const string WorklistRead = "worklist.read";
    /// <summary>Read patients, orders, imaging studies and signed reports as FHIR R4.</summary>
    public const string FhirRead = "fhir.read";
    /// <summary>Register patients and book imaging orders through FHIR (Patient / ServiceRequest create).</summary>
    public const string FhirWrite = "fhir.write";

    public static readonly IReadOnlyList<string> All = new[] { DicomWebRead, DicomWebWrite, WorklistRead, FhirRead, FhirWrite };

    /// <summary>The principal type claim ("type") carried by an integration key sign-in.</summary>
    public const string PrincipalType = "integration";
    /// <summary>The role claim an integration key sign-in carries, for [Authorize(Roles=…)].</summary>
    public const string Role = "Integration";
    /// <summary>The claim holding the key's scopes (one claim per scope).</summary>
    public const string ScopeClaim = "scope";

    /// <summary>Normalise a user-supplied list: known scopes only, de-duplicated, stable order.</summary>
    public static List<string> Normalise(IEnumerable<string>? scopes)
    {
        var wanted = new HashSet<string>(
            (scopes ?? Array.Empty<string>()).Select(s => (s ?? string.Empty).Trim().ToLowerInvariant()));
        return All.Where(wanted.Contains).ToList();
    }

    /// <summary>The path prefixes an integration key is accepted on (case-insensitive).</summary>
    public static readonly IReadOnlyList<string> PathPrefixes = new[]
    {
        "/api/v1/dicomweb",
        "/api/v1/worklist",
        "/api/v1/fhir",
    };
}
