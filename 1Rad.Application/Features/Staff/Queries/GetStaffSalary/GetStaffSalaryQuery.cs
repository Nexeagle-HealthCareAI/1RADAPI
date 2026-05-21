using MediatR;

namespace _1Rad.Application.Features.Staff.Queries.GetStaffSalary;

public record GetStaffSalaryQuery(Guid StaffId, Guid HospitalId) : IRequest<StaffSalaryDto?>;
