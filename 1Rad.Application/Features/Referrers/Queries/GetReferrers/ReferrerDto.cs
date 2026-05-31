namespace _1Rad.Application.Features.Referrers.Queries.GetReferrers;

public record ReferrerDto(
    Guid ReferrerId,
    string Name,
    string Contact,
    string Address,
    // Sync fields (Phase B3 Slice 4).
    DateTime? UpdatedAt = null,
    DateTime? DeletedAt = null
);
