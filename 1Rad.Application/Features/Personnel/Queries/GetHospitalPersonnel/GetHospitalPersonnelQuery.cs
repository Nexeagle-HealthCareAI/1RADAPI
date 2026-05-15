using MediatR;

namespace _1Rad.Application.Features.Personnel.Queries.GetHospitalPersonnel;

public record GetHospitalPersonnelQuery(Guid HospitalId) : IRequest<List<PersonnelDto>>;

public record PersonnelDto(
    Guid UserId,
    string FullName,
    string Email,
    string Mobile,
    string Password,
    List<string> Roles,
    string? Specialization,
    string? Degree,
    string? LicenseNo,
    string Status,
    DateTime CreatedAt);
