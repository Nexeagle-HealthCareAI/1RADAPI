using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.ImageLinks;

/// <summary>Which report the key images belong to: a visit study, or a PACS study.</summary>
public record ReportOwner(Guid? AppointmentId, Guid? AppointmentServiceId, Guid? ImagingStudyId)
{
    public bool IsStudy => ImagingStudyId is Guid g && g != Guid.Empty;
    public bool IsVisit => AppointmentId is Guid g && g != Guid.Empty;

    public IQueryable<ReportKeyImage> Filter(IQueryable<ReportKeyImage> q) => IsStudy
        ? q.Where(k => k.ImagingStudyId == ImagingStudyId)
        : q.Where(k => k.AppointmentId == AppointmentId && k.AppointmentServiceId == AppointmentServiceId);
}

public record KeyImageDto(Guid Id, Guid? StudyAssetId, string? SeriesInstanceUid, string SopInstanceUid, int? FrameIndex, int? SliceIndex, string? Note, DateTime CreatedAt);

public record GetKeyImagesQuery(ReportOwner Owner) : IRequest<IReadOnlyList<KeyImageDto>>;

public class GetKeyImagesQueryHandler : IRequestHandler<GetKeyImagesQuery, IReadOnlyList<KeyImageDto>>
{
    private readonly IApplicationDbContext _db;
    public GetKeyImagesQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<KeyImageDto>> Handle(GetKeyImagesQuery q, CancellationToken ct)
    {
        if (!q.Owner.IsStudy && !q.Owner.IsVisit) return Array.Empty<KeyImageDto>();
        return await q.Owner.Filter(_db.ReportKeyImages.AsNoTracking())
            .OrderBy(k => k.CreatedAt)
            .Select(k => new KeyImageDto(k.Id, k.StudyAssetId, k.SeriesInstanceUid, k.SopInstanceUid, k.FrameIndex, k.SliceIndex, k.Note, k.CreatedAt))
            .ToListAsync(ct);
    }
}

/// <summary>Mark an image as key (idempotent per instance + frame). At most 24 per report.</summary>
public record AddKeyImageCommand(ReportOwner Owner, Guid? StudyAssetId, string? SeriesInstanceUid, string SopInstanceUid, int? FrameIndex, int? SliceIndex, string? Note) : IRequest<KeyImageDto>;

public class AddKeyImageCommandHandler : IRequestHandler<AddKeyImageCommand, KeyImageDto>
{
    public const int MaxPerReport = 24;
    private readonly IApplicationDbContext _db;
    public AddKeyImageCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<KeyImageDto> Handle(AddKeyImageCommand c, CancellationToken ct)
    {
        var sop = (c.SopInstanceUid ?? string.Empty).Trim();
        if (sop.Length == 0 || sop.Length > 64) throw new ArgumentException("A key image needs the image's DICOM instance UID.");
        await EnsureOwnerAsync(c.Owner, ct);

        var existing = await c.Owner.Filter(_db.ReportKeyImages)
            .FirstOrDefaultAsync(k => k.SopInstanceUid == sop && k.FrameIndex == c.FrameIndex, ct);
        if (existing != null) return Dto(existing);

        if (await c.Owner.Filter(_db.ReportKeyImages).CountAsync(ct) >= MaxPerReport)
            throw new ArgumentException($"A report can have up to {MaxPerReport} key images.");

        // Fill series / asset from the slice index when the viewer didn't send them.
        var slice = await _db.StudySliceIndexes.AsNoTracking()
            .Where(s => s.SopInstanceUID == sop)
            .Select(s => new { s.AssetId, s.SeriesUID })
            .FirstOrDefaultAsync(ct);

        var uc = _db.UserContext;
        var row = new ReportKeyImage
        {
            HospitalId = uc.HospitalId,
            AppointmentId = c.Owner.IsStudy ? null : c.Owner.AppointmentId,
            AppointmentServiceId = c.Owner.IsStudy ? null : c.Owner.AppointmentServiceId,
            ImagingStudyId = c.Owner.IsStudy ? c.Owner.ImagingStudyId : null,
            StudyAssetId = c.StudyAssetId ?? slice?.AssetId,
            SeriesInstanceUid = Trim(c.SeriesInstanceUid, 64) ?? slice?.SeriesUID,
            SopInstanceUid = sop,
            FrameIndex = c.FrameIndex,
            SliceIndex = c.SliceIndex,
            Note = Trim(c.Note, 200),
            CreatedByUserId = uc.UserId == Guid.Empty ? null : uc.UserId,
        };
        _db.ReportKeyImages.Add(row);
        await _db.SaveChangesAsync(ct);
        return Dto(row);
    }

    private async Task EnsureOwnerAsync(ReportOwner o, CancellationToken ct)
    {
        if (o.IsStudy)
        {
            if (!await _db.ImagingStudies.AnyAsync(s => s.Id == o.ImagingStudyId, ct))
                throw new KeyNotFoundException("Study not found.");
            return;
        }
        if (!o.IsVisit) throw new ArgumentException("Say which visit or study the key image belongs to.");
        if (!await _db.Appointments.AnyAsync(a => a.AppointmentId == o.AppointmentId, ct))
            throw new KeyNotFoundException("Visit not found.");
        if (o.AppointmentServiceId is Guid sid &&
            !await _db.AppointmentServices.AnyAsync(s => s.Id == sid && s.AppointmentId == o.AppointmentId, ct))
            throw new ArgumentException("That study is not on this visit.");
    }

    private static string? Trim(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : (s.Trim().Length > max ? s.Trim()[..max] : s.Trim());

    internal static KeyImageDto Dto(ReportKeyImage k) =>
        new(k.Id, k.StudyAssetId, k.SeriesInstanceUid, k.SopInstanceUid, k.FrameIndex, k.SliceIndex, k.Note, k.CreatedAt);
}

public record RemoveKeyImageCommand(Guid Id) : IRequest<bool>;

public class RemoveKeyImageCommandHandler : IRequestHandler<RemoveKeyImageCommand, bool>
{
    private readonly IApplicationDbContext _db;
    public RemoveKeyImageCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<bool> Handle(RemoveKeyImageCommand c, CancellationToken ct)
    {
        var row = await _db.ReportKeyImages.FirstOrDefaultAsync(k => k.Id == c.Id, ct);
        if (row == null) return false;
        _db.ReportKeyImages.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
