using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.UploadStaffDocument;

public record UploadStaffDocumentCommand(
    Guid StaffId,
    Guid HospitalId,
    Guid UploadedByUserId,
    string FileName,
    string ContentType,
    int FileSizeBytes,
    string Category,
    Stream FileStream
) : IRequest<(Guid DocumentId, string? Error)>;
