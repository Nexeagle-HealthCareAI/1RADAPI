using MediatR;

namespace _1Rad.Application.Features.Staff.Queries.GetHospitalStaff;

public record GetHospitalStaffQuery(Guid HospitalId) : IRequest<List<StaffMemberDto>>;
