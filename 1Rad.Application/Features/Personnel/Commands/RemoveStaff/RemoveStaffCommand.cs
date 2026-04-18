using MediatR;

namespace _1Rad.Application.Features.Personnel.Commands.RemoveStaff;

public record RemoveStaffCommand(Guid UserId, Guid HospitalId) : IRequest<(bool Success, string? Error)>;
