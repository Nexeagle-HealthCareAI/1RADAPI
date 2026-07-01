using MediatR;
using _1Rad.Application.Common;
using _1Rad.Application.Common.Exceptions;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Commands.AddAddendum;

/// <summary>
/// Appends a formal addendum to a finalised report (21 CFR Part 11). The signed
/// content is never altered — the addendum is its own immutable, identity-bound
/// record, and the report's Status advances to "Addended". Requires a password
/// re-auth, the same meaning-of-signature gate as finalisation.
/// </summary>
public record AddAddendumCommand : IRequest<DiagnosticReport>
{
    public string? AppointmentId { get; init; }
    public Guid? ImagingStudyId { get; init; }
    public Guid? AppointmentServiceId { get; init; }

    public string Text { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? Credentials { get; init; }
}

public class AddAddendumCommandHandler : IRequestHandler<AddAddendumCommand, DiagnosticReport>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _passwordHasher;

    public AddAddendumCommandHandler(IApplicationDbContext context, IPasswordHasher passwordHasher)
    {
        _context = context;
        _passwordHasher = passwordHasher;
    }

    public async Task<DiagnosticReport> Handle(AddAddendumCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;
        var doctorId = _context.UserContext.UserId;

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Addendum text is required.", nameof(request.Text));

        // Re-authenticate (an addendum is a signature event too).
        var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == doctorId, cancellationToken);
        if (user == null)
            throw new UnauthorizedAccessException("Signing user not found.");
        if (string.IsNullOrEmpty(user.PasswordHash) || !_passwordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
            throw new UnauthorizedAccessException("Password verification failed — the addendum was not added.");

        // Resolve owner (appointment XOR study), tenant-checked.
        Appointment? appointment = null;
        ImagingStudy? study = null;

        if (request.ImagingStudyId is Guid studyId && studyId != Guid.Empty)
        {
            study = await _context.ImagingStudies.FirstOrDefaultAsync(s => s.Id == studyId, cancellationToken);
            if (study == null) throw new KeyNotFoundException($"Imaging study with ID '{studyId}' not found.");
            if (study.HospitalId != hospitalId)
                throw new UnauthorizedAccessException("You do not have permission to amend a report for this study.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.AppointmentId))
                throw new ArgumentException("Either an Appointment ID or an Imaging Study ID is required.", nameof(request.AppointmentId));

            _ = Guid.TryParse(request.AppointmentId, out var guidId);
            appointment = await _context.Appointments
                .FirstOrDefaultAsync(a => a.AppointmentId == guidId || a.DisplayId == request.AppointmentId, cancellationToken);
            if (appointment == null) throw new KeyNotFoundException($"Appointment with ID '{request.AppointmentId}' not found.");
            if (appointment.HospitalId != hospitalId)
                throw new UnauthorizedAccessException("You do not have permission to amend a report for this appointment.");
        }

        DiagnosticReport? report;
        if (study != null)
        {
            report = await _context.DiagnosticReports
                .FirstOrDefaultAsync(r => r.ImagingStudyId == study.Id, cancellationToken);
        }
        else if (request.AppointmentServiceId.HasValue)
        {
            report = await _context.DiagnosticReports
                .FirstOrDefaultAsync(r => r.AppointmentId == appointment!.AppointmentId
                                       && r.AppointmentServiceId == request.AppointmentServiceId, cancellationToken);
        }
        else
        {
            report = await _context.DiagnosticReports
                .Where(r => r.AppointmentId == appointment!.AppointmentId)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (report == null)
            throw new KeyNotFoundException("Report not found.");

        // Addenda only attach to a SIGNED-FINAL (or already-addended) report.
        if (report.Status != ReportStatuses.Final && report.Status != ReportStatuses.Addended)
            throw new ArgumentException("Only a finalised report can take an addendum.");

        var nowUtc = DateTime.UtcNow;
        var text = request.Text.Trim();
        var textHash = ReportHashing.HashText(text);

        var nextOrder = await _context.ReportAddenda
            .Where(a => a.ReportId == report.Id)
            .CountAsync(cancellationToken) + 1;

        var credentials = string.IsNullOrWhiteSpace(request.Credentials)
            ? BuildCredentials(user)
            : request.Credentials!.Trim();

        _context.ReportAddenda.Add(new ReportAddendum
        {
            Id = Guid.NewGuid(),
            ReportId = report.Id,
            HospitalId = report.HospitalId,
            AuthorUserId = doctorId,
            AuthorName = user.FullName,
            AuthorCredentials = credentials,
            Text = text,
            ContentHash = textHash,
            SignedAt = nowUtc,
            SortOrder = nextOrder,
            CreatedAt = nowUtc,
        });

        report.Status = ReportStatuses.Addended;
        report.UpdatedAt = nowUtc;

        // Tamper-evident audit event, chained to the prior event.
        var prevHash = await _context.ReportAuditEvents
            .Where(e => e.ReportId == report.Id)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => e.ContentHash)
            .FirstOrDefaultAsync(cancellationToken);

        _context.ReportAuditEvents.Add(new ReportAuditEvent
        {
            Id = Guid.NewGuid(),
            ReportId = report.Id,
            HospitalId = report.HospitalId,
            EventType = ReportAuditEventTypes.AddendumAdded,
            ActorUserId = doctorId,
            ActorName = user.FullName,
            Timestamp = nowUtc,
            ContentHash = textHash,
            PreviousHash = prevHash,
            Details = $"Addendum #{nextOrder}",
        });

        await _context.SaveChangesAsync(cancellationToken);

        // Return the report with its addenda so the client can render them.
        report.Addenda = await _context.ReportAddenda
            .Where(a => a.ReportId == report.Id)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(cancellationToken);

        return report;
    }

    private static string BuildCredentials(User user)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(user.Degree)) parts.Add(user.Degree!.Trim());
        if (!string.IsNullOrWhiteSpace(user.LicenseNo)) parts.Add($"Lic. {user.LicenseNo!.Trim()}");
        return string.Join(", ", parts);
    }
}
