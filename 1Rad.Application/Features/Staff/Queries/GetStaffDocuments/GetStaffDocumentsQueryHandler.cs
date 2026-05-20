using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Queries.GetStaffDocuments;

public class GetStaffDocumentsQueryHandler : IRequestHandler<GetStaffDocumentsQuery, List<StaffDocumentDto>>
{
    private readonly IApplicationDbContext _context;

    public GetStaffDocumentsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<StaffDocumentDto>> Handle(GetStaffDocumentsQuery request, CancellationToken cancellationToken)
    {
        return await _context.StaffDocuments
            .Where(d => d.StaffId == request.StaffId && d.HospitalId == request.HospitalId)
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new StaffDocumentDto(
                d.DocumentId,
                d.FileName,
                d.ContentType,
                d.FileSizeBytes,
                d.Category,
                d.VerificationStatus,
                d.Notes,
                d.BlobUrl,
                d.UploadedAt))
            .ToListAsync(cancellationToken);
    }
}
