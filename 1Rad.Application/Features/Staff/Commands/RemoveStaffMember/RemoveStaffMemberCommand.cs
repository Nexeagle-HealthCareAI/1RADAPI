using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.RemoveStaffMember;

public record RemoveStaffMemberCommand(Guid StaffId, Guid HospitalId) : IRequest<(bool Success, string? Error)>;
