using MediatR;

namespace _1Rad.Application.Features.LeavePolicy.Queries.GetLeavePolicy;

public record GetLeavePolicyQuery(Guid HospitalId) : IRequest<LeavePolicyDto>;

public record LeavePolicyDto(Guid? PolicyId, string LeaveTypesJson, DateTime? UpdatedAt);
