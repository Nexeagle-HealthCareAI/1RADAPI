using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.DeleteStaffDocument;

public class DeleteStaffDocumentCommandHandler : IRequestHandler<DeleteStaffDocumentCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;
    private readonly IBlobService _blobService;

    public DeleteStaffDocumentCommandHandler(IApplicationDbContext context, IBlobService blobService)
    {
        _context = context;
        _blobService = blobService;
    }

    public async Task<(bool Success, string? Error)> Handle(DeleteStaffDocumentCommand request, CancellationToken cancellationToken)
    {
        var doc = await _context.StaffDocuments
            .FirstOrDefaultAsync(
                d => d.DocumentId == request.DocumentId
                  && d.StaffId    == request.StaffId
                  && d.HospitalId == request.HospitalId,
                cancellationToken);

        if (doc == null)
            return (false, "Document not found.");

        // Prefer the stored BlobPath + container (set on new uploads) so we delete the
        // exact blob even if the public URL format changes. Fall back to BlobUrl parsing
        // for legacy rows uploaded before the BlobPath column existed.
        var container = !string.IsNullOrWhiteSpace(doc.BlobContainer) ? doc.BlobContainer : "staff-documents";
        if (!string.IsNullOrWhiteSpace(doc.BlobUrl))
        {
            try { await _blobService.DeleteFileAsync(doc.BlobUrl, container); }
            catch { /* blob may already be gone — continue with DB removal */ }
        }

        _context.StaffDocuments.Remove(doc);
        await _context.SaveChangesAsync(cancellationToken);

        return (true, null);
    }
}
