using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.UpsertAttendance;

public class UpsertAttendanceCommandHandler
    : IRequestHandler<UpsertAttendanceCommand, (Guid AttendanceId, string? Error)>
{
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "present", "absent", "halfday", "late", "leave" };

    private readonly IApplicationDbContext _context;

    public UpsertAttendanceCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<(Guid AttendanceId, string? Error)> Handle(
        UpsertAttendanceCommand request, CancellationToken cancellationToken)
    {
        if (!ValidStatuses.Contains(request.Status))
            return (Guid.Empty, "Invalid status. Allowed: present, absent, halfday, late, leave.");

        if (!DateOnly.TryParse(request.Date, out var date))
            return (Guid.Empty, "Invalid date — expected YYYY-MM-DD.");

        var staffExists = await _context.StaffMembers
            .AnyAsync(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId, cancellationToken);
        if (!staffExists)
            return (Guid.Empty, "Staff not found.");

        var existing = await _context.StaffAttendances
            .FirstOrDefaultAsync(a => a.StaffId == request.StaffId && a.AttendanceDate == date, cancellationToken);

        if (existing != null)
        {
            existing.Status          = request.Status.ToLowerInvariant();
            existing.Note            = request.Note?.Trim();
            existing.MarkedByUserId  = request.MarkedByUserId;
            existing.MarkedAt        = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return (existing.AttendanceId, null);
        }

        var entry = new StaffAttendance
        {
            StaffId         = request.StaffId,
            HospitalId      = request.HospitalId,
            AttendanceDate  = date,
            Status          = request.Status.ToLowerInvariant(),
            Note            = request.Note?.Trim(),
            MarkedByUserId  = request.MarkedByUserId,
        };
        _context.StaffAttendances.Add(entry);
        await _context.SaveChangesAsync(cancellationToken);
        return (entry.AttendanceId, null);
    }
}
