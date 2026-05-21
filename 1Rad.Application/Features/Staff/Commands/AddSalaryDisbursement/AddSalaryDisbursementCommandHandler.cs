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
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Draft", "Paid"
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

        if (!ValidStatuses.Contains(request.Status))
            return (Guid.Empty, "Invalid status — expected Draft or Paid.");

        if (!DateOnly.TryParse(request.PaidOnDate, out var paidOn))
            return (Guid.Empty, "Invalid paid-on date.");

        // Idempotency — one disbursal per (staff, month).
        var existing = await _context.SalaryDisbursements
            .AnyAsync(d => d.StaffId == request.StaffId && d.Month == request.Month, cancellationToken);
        if (existing) return (Guid.Empty, $"Salary for {request.Month} is already disbursed.");

        // Normalise status to canonical form.
        var status = string.Equals(request.Status, "Paid", StringComparison.OrdinalIgnoreCase) ? "Paid" : "Draft";

        var entry = new SalaryDisbursement
        {
            StaffId          = request.StaffId,
            HospitalId       = request.HospitalId,
            RevisionId       = request.RevisionId,
            Month            = request.Month,
            Status           = status,
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

        // Auto-create a linked Expense row so Finance sees the salary in the ledger.
        // Status mirrors the disbursement: Draft → Pending, Paid → Paid.
        var monthLabel = FormatMonth(request.Month);
        var expense = new Expense
        {
            HospitalId           = request.HospitalId,
            Description          = $"Salary · {staff.FullName} · {monthLabel}",
            Category             = "Salary",
            Amount               = request.NetPay,
            TaxAmount            = 0m,
            PaymentMode          = request.PaymentMode,
            ReferenceNumber      = request.Reference?.Trim(),
            VendorName           = staff.FullName,
            Status               = status == "Paid" ? "Paid" : "Pending",
            TransactionDate      = paidOn.ToDateTime(TimeOnly.MinValue),
            LinkedDisbursementId = entry.DisbursementId,
        };
        _context.Expenses.Add(expense);

        await _context.SaveChangesAsync(cancellationToken);
        return (entry.DisbursementId, null);
    }

    private static string FormatMonth(string yyyyMM)
    {
        if (yyyyMM.Length != 7) return yyyyMM;
        var parts = yyyyMM.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var month)) return yyyyMM;
        var months = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        if (month < 1 || month > 12) return yyyyMM;
        return $"{months[month - 1]} {year}";
    }
}
