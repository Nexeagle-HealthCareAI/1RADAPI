namespace _1Rad.Application.Features.Referrers.Queries.GetReferrers;

public record ReferrerDto(
    Guid ReferrerId,
    string Name,
    string Contact,
    string Address,
    // Sync fields (Phase B3 Slice 4).
    DateTime? UpdatedAt = null,
    DateTime? DeletedAt = null,
    // Optional referring-doctor profile.
    string? Email = null,
    string? Specialty = null,
    string? Degree = null,
    // Payee-first model: kind of referral source + the doctor an agent supports.
    bool IsDoctor = true,
    string? SupportedByDoctor = null,
    Guid? MergedIntoId = null
);
