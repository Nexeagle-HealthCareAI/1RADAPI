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

        var blobUrl = await _blobService.UploadFileAsync(
            request.FileStream,
            request.FileName,
            request.ContentType,
            BlobContainer);

        var doc = new StaffDocument
        {
            StaffId            = request.StaffId,
            HospitalId         = request.HospitalId,
            FileName           = request.FileName,
            ContentType        = request.ContentType,
            FileSizeBytes      = request.FileSizeBytes,
            Category           = request.Category,
            BlobUrl            = blobUrl,
            VerificationStatus = "Pending",
            UploadedAt         = DateTime.UtcNow,
            UploadedByUserId   = request.UploadedByUserId == Guid.Empty ? null : request.UploadedByUserId,
        };

        _context.StaffDocuments.Add(doc);
        await _context.SaveChangesAsync(cancellationToken);

        return (doc.DocumentId, null);
    }
}
