using MediatR;

namespace _1Rad.Application.Features.LeavePolicy.Commands.SaveLeavePolicy;

public record SaveLeavePolicyCommand(
    Guid HospitalId,
    Guid? UpdatedByUserId,
    string LeaveTypesJson
) : IRequest<(Guid PolicyId, string? Error)>;
