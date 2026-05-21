using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.AddSalaryDisbursement;

public class AddSalaryDisbursementCommandHandler : IRequestHandler<AddSalaryDisbursementCommand, (Guid DisbursementId, string? Error)>
{
    private static readonly HashSet<string> ValidModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bank", "cash", "upi", "cheque"
    };

    private readonly IApplicationDbContext _context;

    public AddSalaryDisbursementCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(Guid DisbursementId, string? Error)> Handle(AddSalaryDisbursementCommand request, CancellationToken cancellationToken)
    {
        var staff = await _context.StaffMembers
            .FirstOrDefaultAsync(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId, cancellationToken);
        if (staff == null) return (Guid.Empty, "Staff not found.");

        if (string.IsNullOrWhiteSpace(request.Month) || request.Month.Length != 7 || request.Month[4] != '-')
            return (Guid.Empty, "Invalid month — expected YYYY-MM.");

        if (!ValidModes.Contains(request.PaymentMode))
            return (Guid.Empty, "Invalid payment mode.");

        if (!DateOnly.TryParse(request.PaidOnDate, out var paidOn))
            return (Guid.Empty, "Invalid paid-on date.");

        // Idempotency — one disbursal per (staff, month).
        var existing = await _context.SalaryDisbursements
            .AnyAsync(d => d.StaffId == request.StaffId && d.Month == request.Month, cancellationToken);
        if (existing) return (Guid.Empty, $"Salary for {request.Month} is already disbursed.");

        var entry = new SalaryDisbursement
        {
            StaffId          = request.StaffId,
            HospitalId       = request.HospitalId,
            RevisionId       = request.RevisionId,
            Month            = request.Month,
            GrossPay         = request.GrossPay,
            NetPay           = request.NetPay,
            StructureGross   = request.StructureGross,
            StructureNet     = request.StructureNet,
            LwpDays          = request.LwpDays,
            LwpDeduction     = request.LwpDeduction,
            PerDayRate       = request.PerDayRate,
            PaidLeaveInMonth = request.PaidLeaveInMonth,
            LwpLeaveInMonth  = request.LwpLeaveInMonth,
            AttendanceJson   = request.AttendanceJson,
            PaymentMode      = request.PaymentMode.ToLowerInvariant(),
            Reference        = request.Reference?.Trim(),
            PaidOnDate       = paidOn,
            Notes            = request.Notes?.Trim(),
            CreatedByUserId  = request.CreatedByUserId,
        };
        _context.SalaryDisbursements.Add(entry);
        await _context.SaveChangesAsync(cancellationToken);
        return (entry.DisbursementId, null);
    }
}
