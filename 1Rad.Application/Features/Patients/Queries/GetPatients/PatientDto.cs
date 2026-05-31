namespace _1Rad.Application.Features.Patients.Queries.GetPatients;

public record PatientDto(
    Guid PatientId,
    string FullName,
    string Mobile,
    string Age,
    string Gender,
    string Village,
    string District,
    string Address,
    string PatientIdentifier,
    string SourceOfInfo,
    DateTime RegisteredAt,
    // Sync engine fields (Phase B1 Slice 2). UpdatedAt drives the client's
    // ?updatedAfter= high-water mark; DeletedAt is the tombstone marker so
    // the local cache can purge cancelled / merged records.
    DateTime? UpdatedAt = null,
    DateTime? DeletedAt = null
);
