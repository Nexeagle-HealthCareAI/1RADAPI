using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.DeleteSalaryDisbursement;

public class DeleteSalaryDisbursementCommandHandler : IRequestHandler<DeleteSalaryDisbursementCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public DeleteSalaryDisbursementCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(bool Success, string? Error)> Handle(DeleteSalaryDisbursementCommand request, CancellationToken cancellationToken)
    {
        var disbursement = await _context.SalaryDisbursements
            .FirstOrDefaultAsync(d => d.DisbursementId == request.DisbursementId && d.StaffId == request.StaffId && d.HospitalId == request.HospitalId, cancellationToken);

        if (disbursement == null)
            return (false, "Disbursement not found.");

        if (!string.Equals(disbursement.Status, "Draft", StringComparison.OrdinalIgnoreCase))
            return (false, "Only Draft disbursements can be deleted.");

        // Also delete the linked Expense if it exists
        var expense = await _context.Expenses
            .FirstOrDefaultAsync(e => e.LinkedDisbursementId == request.DisbursementId, cancellationToken);

        if (expense != null)
        {
            _context.Expenses.Remove(expense);
        }

        // Remove the auto-created encashment leave row. Primary match is the
        // SourceDisbursementId FK — exact 1:1. Fallback to the old fragile
        // match (reason text + date + days) only for legacy rows created
        // before the FK column existed.
        if (disbursement.EncashmentDays > 0)
        {
            var encashmentLeave = await _context.StaffLeaveRequests
                .FirstOrDefaultAsync(l => l.SourceDisbursementId == request.DisbursementId, cancellationToken);

            if (encashmentLeave == null)
            {
                encashmentLeave = await _context.StaffLeaveRequests
                    .FirstOrDefaultAsync(l => l.StaffId == request.StaffId &&
                                              l.SourceDisbursementId == null &&
                                              l.Reason == "Encashed during salary payout" &&
                                              l.FromDate == disbursement.PaidOnDate &&
                                              l.Days == (int)Math.Round(disbursement.EncashmentDays), cancellationToken);
            }

            if (encashmentLeave != null)
            {
                _context.StaffLeaveRequests.Remove(encashmentLeave);
            }
        }

        _context.SalaryDisbursements.Remove(disbursement);
        await _context.SaveChangesAsync(cancellationToken);

        return (true, null);
    }
}
