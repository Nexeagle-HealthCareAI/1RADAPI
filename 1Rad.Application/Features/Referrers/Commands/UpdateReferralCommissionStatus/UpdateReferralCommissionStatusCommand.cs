using _1Rad.Application.Common;
using _1Rad.Domain.Exceptions;
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
            throw new NotFoundException($"Commission record [{request.CommissionId}] was not found.");

        var requestedStatus = (request.Status ?? string.Empty).Trim().ToUpperInvariant();
        if (requestedStatus is not "UNPAID" and not "PAID" and not "CANCELLED")
            throw new ValidationException("Commission status must be UNPAID, PAID, or CANCELLED.");

        var currentStatus = (commission.Status ?? string.Empty).Trim().ToUpperInvariant();
        if (currentStatus == "PAID" && requestedStatus != "PAID")
            throw new BusinessRuleViolationException("A paid commission cannot be reversed directly. Submit an approval request to unpay or adjust it.");
        if (requestedStatus == "CANCELLED" && (commission.AppointmentId.HasValue || commission.AppointmentServiceId.HasValue))
            throw new BusinessRuleViolationException("Appointment-generated commissions can only be cancelled through the appointment cancellation workflow.");
        if (requestedStatus == "PAID" && currentStatus != "UNPAID")
            throw new BusinessRuleViolationException("Only an unpaid commission can be marked paid.");
        if (requestedStatus == "PAID" && commission.CommissionAmount <= 0)
            throw new BusinessRuleViolationException("Only a positive commission amount can be paid.");

        // A referrer is paid once the patient has paid — the Referral Hub already
        // hides the action until then, but that was the ONLY place the rule lived,
        // so any other caller (or a stale screen) could pay out on a visit the
        // patient had not settled. Only commissions earned on an appointment have
        // a patient bill to check; manual/legacy ones do not.
        if (requestedStatus == "PAID" && commission.AppointmentId.HasValue)
        {
            var payability = await PatientPaymentStatus.ForCommissionsAsync(
                _context, commission.HospitalId,
                new[] { (commission.Id, commission.AppointmentId, commission.ReferenceNumber) },
                cancellationToken);
            payability.TryGetValue(commission.Id, out var patientStatus);
            if (!PatientPaymentStatus.IsPayable(patientStatus))
                throw new BusinessRuleViolationException("The patient has not paid for this visit yet, so the referral commission cannot be paid out.");
        }

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
        // Recorded from the signed-in account. request.UpdatedBy is client-supplied, so it is ignored.
        commission.UpdatedBy = await CommissionActor.ResolveAsync(_context, cancellationToken);
        commission.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
