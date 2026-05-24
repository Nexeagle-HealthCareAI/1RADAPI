using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.DeleteSalaryDisbursement;

public record DeleteSalaryDisbursementCommand(Guid DisbursementId, Guid StaffId, Guid HospitalId) : IRequest<(bool Success, string? Error)>;
