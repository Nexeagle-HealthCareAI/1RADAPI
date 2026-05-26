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

        // Refuse months that haven't happened yet. Server clock is authoritative
        // so a client with a wrong date can't disburse for, say, next year.
        if (!int.TryParse(request.Month.AsSpan(0, 4), out var reqYear) ||
            !int.TryParse(request.Month.AsSpan(5, 2), out var reqMonth) ||
            reqMonth < 1 || reqMonth > 12)
            return (Guid.Empty, "Invalid month — expected YYYY-MM.");

        var serverToday = DateTime.UtcNow;
        var requested   = new DateTime(reqYear, reqMonth, 1);
        var thisMonth   = new DateTime(serverToday.Year, serverToday.Month, 1);
        if (requested > thisMonth)
            return (Guid.Empty, $"Cannot disburse salary for {request.Month} — month has not started yet.");

        if (!ValidModes.Contains(request.PaymentMode))
            return (Guid.Empty, "Invalid payment mode.");

        if (!ValidStatuses.Contains(request.Status))
            return (Guid.Empty, "Invalid status — expected Draft or Paid.");

        if (!DateOnly.TryParse(request.PaidOnDate, out var paidOn))
            return (Guid.Empty, "Invalid paid-on date.");

        // Encashment days must be a non-negative whole number — the linked
        // leave row stores Days as int, and fractional encashment would orphan
        // on cleanup (HQ would round, cleanup would mismatch).
        if (request.EncashmentDays < 0 || request.EncashmentDays != Math.Floor(request.EncashmentDays))
            return (Guid.Empty, "Encashment days must be a non-negative whole number.");

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
            EncashmentDays   = request.EncashmentDays,
            EncashmentBonus  = request.EncashmentBonus,
            ExtraPay         = request.ExtraPay,
            ExtraPayReason   = request.ExtraPayReason?.Trim(),
            PaymentMode      = request.PaymentMode.ToLowerInvariant(),
            Reference        = request.Reference?.Trim(),
            PaidOnDate       = paidOn,
            Notes            = request.Notes?.Trim(),
            CreatedByUserId  = request.CreatedByUserId,
        };
        _context.SalaryDisbursements.Add(entry);

        // Auto-create a linked Expense row so Finance sees the salary in the ledger.
        // Status mirrors the disbursement: Draft → Pending, Paid → Paid.
        // Amount must match what actually leaves the bank: NetPay (LWP-applied)
        // plus EncashmentBonus and ExtraPay, both of which are additional cash out.
        var monthLabel = FormatMonth(request.Month);
        var payoutAmount = request.NetPay + request.EncashmentBonus + request.ExtraPay;
        var expense = new Expense
        {
            HospitalId           = request.HospitalId,
            Description          = $"Salary · {staff.FullName} · {monthLabel}",
            Category             = "Salary",
            Amount               = payoutAmount,
            TaxAmount            = 0m,
            PaymentMode          = request.PaymentMode,
            ReferenceNumber      = request.Reference?.Trim(),
            VendorName           = staff.FullName,
            Status               = status == "Paid" ? "Paid" : "Pending",
            TransactionDate      = paidOn.ToDateTime(TimeOnly.MinValue),
            LinkedDisbursementId = entry.DisbursementId,
        };
        _context.Expenses.Add(expense);

        // Auto-create Leave Request for encashment if EncashmentDays > 0
        if (request.EncashmentDays > 0)
        {
            var encashmentLeave = new StaffLeaveRequest
            {
                StaffId = request.StaffId,
                HospitalId = request.HospitalId,
                LeaveType = string.IsNullOrWhiteSpace(request.EncashmentType) ? "Earned Leave" : request.EncashmentType,
                FromDate = paidOn,
                ToDate = paidOn,
                Days = (int)request.EncashmentDays,
                Reason = "Encashed during salary payout",
                Status = "approved",
                AppliedOn = DateTime.UtcNow,
                ReviewedByUserId = request.CreatedByUserId,
                ReviewedAt = DateTime.UtcNow,
                SourceDisbursementId = entry.DisbursementId
            };
            _context.StaffLeaveRequests.Add(encashmentLeave);
        }

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
