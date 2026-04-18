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
    string SourceOfInfo
);
