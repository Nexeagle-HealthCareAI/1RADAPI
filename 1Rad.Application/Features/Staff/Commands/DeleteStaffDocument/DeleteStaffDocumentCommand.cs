using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.DeleteStaffDocument;

public record DeleteStaffDocumentCommand(Guid DocumentId, Guid StaffId, Guid HospitalId)
    : IRequest<(bool Success, string? Error)>;
