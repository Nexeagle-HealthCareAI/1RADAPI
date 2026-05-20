namespace _1Rad.Application.Features.Staff.Queries.GetStaffDocuments;

public record StaffDocumentDto(
    Guid DocumentId,
    string FileName,
    string? ContentType,
    int? FileSizeBytes,
    string Category,
    string VerificationStatus,
    string? Notes,
    string? BlobUrl,
    DateTime UploadedAt
);
