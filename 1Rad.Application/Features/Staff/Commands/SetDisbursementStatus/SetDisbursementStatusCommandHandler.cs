using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.SetDisbursementStatus;

public class SetDisbursementStatusCommandHandler : IRequestHandler<SetDisbursementStatusCommand, (bool Success, string? Error)>
{
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase) { "Draft", "Paid" };
    private static readonly HashSet<string> ValidModes    = new(StringComparer.OrdinalIgnoreCase) { "bank", "cash", "upi", "cheque" };

    private readonly IApplicationDbContext _context;

    public SetDisbursementStatusCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(bool Success, string? Error)> Handle(SetDisbursementStatusCommand request, CancellationToken cancellationToken)
    {
        if (!ValidStatuses.Contains(request.Status))
            return (false, "Invalid status — expected Draft or Paid.");

        var disbursement = await _context.SalaryDisbursements
            .FirstOrDefaultAsync(d => d.DisbursementId == request.DisbursementId && d.HospitalId == request.HospitalId, cancellationToken);
        if (disbursement == null) return (false, "Disbursement not found.");

        var nextStatus = string.Equals(request.Status, "Paid", StringComparison.OrdinalIgnoreCase) ? "Paid" : "Draft";

        // Optional updates when transitioning Draft -> Paid: payment mode, reference, paidOn.
        if (!string.IsNullOrWhiteSpace(request.PaymentMode))
        {
            if (!ValidModes.Contains(request.PaymentMode))
                return (false, "Invalid payment mode.");
            disbursement.PaymentMode = request.PaymentMode.ToLowerInvariant();
        }
        if (request.Reference != null) disbursement.Reference = request.Reference.Trim();
        if (!string.IsNullOrWhiteSpace(request.PaidOnDate))
        {
            if (!DateOnly.TryParse(request.PaidOnDate, out var paidOn))
                return (false, "Invalid paid-on date.");
            disbursement.PaidOnDate = paidOn;
        }
        disbursement.Status = nextStatus;

        // Sync the linked expense row.
        var expense = await _context.Expenses
            .FirstOrDefaultAsync(e => e.LinkedDisbursementId == request.DisbursementId, cancellationToken);
        if (expense != null)
        {
            expense.Status          = nextStatus == "Paid" ? "Paid" : "Pending";
            expense.PaymentMode     = disbursement.PaymentMode;
            expense.ReferenceNumber = disbursement.Reference;
            expense.TransactionDate = disbursement.PaidOnDate.ToDateTime(TimeOnly.MinValue);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (true, null);
    }
}
