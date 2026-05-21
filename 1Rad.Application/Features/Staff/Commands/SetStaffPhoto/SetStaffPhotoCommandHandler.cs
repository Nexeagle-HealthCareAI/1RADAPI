using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.SetStaffPhoto;

public class SetStaffPhotoCommandHandler : IRequestHandler<SetStaffPhotoCommand, (string? PhotoUrl, string? Error)>
{
    private const string BlobContainer = "staff-documents";

    // Allowed image MIME types.
    private static readonly HashSet<string> AllowedMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif"
    };

    private readonly IApplicationDbContext _context;
    private readonly IBlobService _blobService;

    public SetStaffPhotoCommandHandler(IApplicationDbContext context, IBlobService blobService)
    {
        _context = context;
        _blobService = blobService;
    }

    public async Task<(string? PhotoUrl, string? Error)> Handle(SetStaffPhotoCommand request, CancellationToken cancellationToken)
    {
        var staff = await _context.StaffMembers
            .FirstOrDefaultAsync(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId, cancellationToken);
        if (staff == null) return (null, "Staff member not found.");

        // Delete previous photo (if any) before replacing or clearing.
        if (!string.IsNullOrWhiteSpace(staff.PhotoUrl))
        {
            try { await _blobService.DeleteFileAsync(staff.PhotoUrl, BlobContainer); }
            catch { /* swallow — blob may already be gone */ }
        }

        // No new file → clear the photo.
        if (request.FileStream == null)
        {
            staff.PhotoUrl  = null;
            staff.PhotoPath = null;
            staff.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return (null, null);
        }

        if (string.IsNullOrWhiteSpace(request.FileName))
            return (null, "File name is required.");

        if (!string.IsNullOrWhiteSpace(request.ContentType) && !AllowedMimes.Contains(request.ContentType))
            return (null, "Only JPEG, PNG, WebP, or GIF images are allowed for staff photos.");

        // Folder layout: {hospitalId}/{staffId}/photo_{tick}.{ext}
        var ext      = Path.GetExtension(request.FileName);
        var blobPath = $"{request.HospitalId}/{request.StaffId}/photo_{DateTime.UtcNow.Ticks}{ext}";

        var blobUrl = await _blobService.UploadFileAtPathAsync(
            request.FileStream,
            blobPath,
            request.ContentType ?? "image/jpeg",
            BlobContainer);

        staff.PhotoUrl  = blobUrl;
        staff.PhotoPath = blobPath;
        staff.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return (blobUrl, null);
    }
}
