using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommissionStatus;

public record UpdateReferralCommissionStatusCommand(
    Guid CommissionId,
    string Status,
    string? PaidBy = null,
    string? PayeeName = null,
    string? PayeeContact = null,
    string? PayeeEmail = null,
    string? PayeeAddress = null,
    string? UpdatedBy = null
) : IRequest<bool>;

public class UpdateReferralCommissionStatusCommandHandler : IRequestHandler<UpdateReferralCommissionStatusCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateReferralCommissionStatusCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateReferralCommissionStatusCommand request, CancellationToken cancellationToken)
    {
        var commission = await _context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.Id == request.CommissionId, cancellationToken);

        if (commission == null)
            throw new Exception($"FISCAL ERROR: Commission record [{request.CommissionId}] not found in strategic ledger.");

        var requestedStatus = (request.Status ?? string.Empty).Trim().ToUpperInvariant();
        if (requestedStatus is not "UNPAID" and not "PAID" and not "CANCELLED")
            throw new ArgumentException("Commission status must be UNPAID, PAID, or CANCELLED.", nameof(request.Status));

        var currentStatus = (commission.Status ?? string.Empty).Trim().ToUpperInvariant();
        if (currentStatus == "PAID" && requestedStatus != "PAID")
            throw new InvalidOperationException("A paid commission cannot be reversed directly. Submit an approval request to unpay or adjust it.");
        if (requestedStatus == "CANCELLED" && (commission.AppointmentId.HasValue || commission.AppointmentServiceId.HasValue))
            throw new InvalidOperationException("Appointment-generated commissions can only be cancelled through the appointment cancellation workflow.");
        if (requestedStatus == "PAID" && currentStatus != "UNPAID")
            throw new InvalidOperationException("Only an unpaid commission can be marked paid.");
        if (requestedStatus == "PAID" && commission.CommissionAmount <= 0)
            throw new InvalidOperationException("Only a positive commission amount can be paid.");

        commission.Status = requestedStatus;
        if (requestedStatus == "PAID")
        {
            commission.PaymentDate = DateTime.UtcNow;
            // Persist mandatory disbursement details when marking as PAID.
            if (!string.IsNullOrWhiteSpace(request.PaidBy))    commission.PaidBy       = request.PaidBy;
            if (!string.IsNullOrWhiteSpace(request.PayeeName)) commission.PayeeName    = request.PayeeName;
            if (!string.IsNullOrWhiteSpace(request.PayeeContact)) commission.PayeeContact = request.PayeeContact;
            if (!string.IsNullOrWhiteSpace(request.PayeeEmail))   commission.PayeeEmail   = request.PayeeEmail;
            if (!string.IsNullOrWhiteSpace(request.PayeeAddress)) commission.PayeeAddress = request.PayeeAddress;
        }
        if (!string.IsNullOrWhiteSpace(request.UpdatedBy)) commission.UpdatedBy = request.UpdatedBy;
        commission.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
