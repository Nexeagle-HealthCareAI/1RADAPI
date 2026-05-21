using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.DeleteSalaryRevision;

public record DeleteSalaryRevisionCommand(
    Guid RevisionId,
    Guid StaffId,
    Guid HospitalId
) : IRequest<(bool Success, string? Error)>;
