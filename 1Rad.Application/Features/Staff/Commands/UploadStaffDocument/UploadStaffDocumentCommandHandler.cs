using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.UploadStaffDocument;

public class UploadStaffDocumentCommandHandler : IRequestHandler<UploadStaffDocumentCommand, (Guid DocumentId, string? Error)>
{
    private readonly IApplicationDbContext _context;
    private readonly IBlobService _blobService;

    private const string BlobContainer = "staff-documents";

    public UploadStaffDocumentCommandHandler(IApplicationDbContext context, IBlobService blobService)
    {
        _context = context;
        _blobService = blobService;
    }

    public async Task<(Guid DocumentId, string? Error)> Handle(UploadStaffDocumentCommand request, CancellationToken cancellationToken)
    {
        var staffExists = await _context.StaffMembers
            .AnyAsync(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId, cancellationToken);

        if (!staffExists)
            return (Guid.Empty, "Staff member not found.");

        // Folder layout inside the container:
        //   {hospitalId}/{staffId}/{documentId}_{sanitised-fileName}
        // Each staff member's documents live in their own virtual folder, scoped under
        // the hospital so multi-tenant data is isolated even if container ACLs are misconfigured.
        var documentId = Guid.NewGuid();
        var safeName   = Path.GetFileName(request.FileName); // strip any client-side path
        var blobPath   = $"{request.HospitalId}/{request.StaffId}/{documentId}_{safeName}";

        var blobUrl = await _blobService.UploadFileAtPathAsync(
            request.FileStream,
            blobPath,
            request.ContentType,
            BlobContainer);

        var doc = new StaffDocument
        {
            DocumentId         = documentId,
            StaffId            = request.StaffId,
            HospitalId         = request.HospitalId,
            FileName           = safeName,
            ContentType        = request.ContentType,
            FileSizeBytes      = request.FileSizeBytes,
            Category           = request.Category,
            BlobUrl            = blobUrl,
            BlobPath           = blobPath,
            BlobContainer      = BlobContainer,
            VerificationStatus = "Pending",
            UploadedAt         = DateTime.UtcNow,
            UploadedByUserId   = request.UploadedByUserId == Guid.Empty ? null : request.UploadedByUserId,
        };

        _context.StaffDocuments.Add(doc);
        await _context.SaveChangesAsync(cancellationToken);

        return (doc.DocumentId, null);
    }
}
