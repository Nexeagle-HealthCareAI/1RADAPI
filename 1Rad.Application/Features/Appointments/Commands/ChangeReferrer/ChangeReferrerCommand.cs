using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;
using _1Rad.Application.Features.Approvals;

namespace _1Rad.Application.Features.Appointments.Commands.ChangeReferrer;

/// <summary>
/// Scenario 05 — correct an appointment's "Referred By" so the commission
/// credits the right person. If no payment has been collected the change is
/// applied straight away; once payment exists it must go through admin approval
/// (the UI then submits a CHANGE_REFERRER request), so this command applies
/// NOTHING in that case and just reports RequiresApproval.
/// </summary>
public class ChangeReferrerResult
{
    public bool Applied { get; set; }
    public bool RequiresApproval { get; set; }
    public string Message { get; set; } = string.Empty;
}

public record ChangeReferrerCommand : IRequest<ChangeReferrerResult>
{
    public Guid AppointmentId { get; init; }
    public string NewReferrerName { get; init; } = string.Empty;
    public string? NewReferrerContact { get; init; }
    public bool? NewReferrerIsDoctor { get; init; }
    // When the new referrer is an "Other person" (agent), the doctor they collect
    // for — mirrors booking's supporting-doctor field.
    public string? NewReferrerSupportedByDoctor { get; init; }
    // Full referrer profile (parity with booking).
    public string? NewReferrerEmail { get; init; }
    public string? NewReferrerSpecialty { get; init; }
    public string? NewReferrerDegree { get; init; }
    public string? NewReferrerAddress { get; init; }
    // The supporting doctor's own profile (when the referrer is an Other person).
    public string? NewReferrerSupportedSpecialty { get; init; }
    public string? NewReferrerSupportedDegree { get; init; }
}

public class ChangeReferrerCommandHandler : IRequestHandler<ChangeReferrerCommand, ChangeReferrerResult>
{
    private readonly IApplicationDbContext _context;

    public ChangeReferrerCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<ChangeReferrerResult> Handle(ChangeReferrerCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;

        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId && a.HospitalId == hospitalId, ct);
        if (appointment == null)
            return new ChangeReferrerResult { Message = "Appointment not found." };

        if (string.IsNullOrWhiteSpace(request.NewReferrerName))
            return new ChangeReferrerResult { Message = "A referrer name is required." };

        // Payment already collected → the change has to be approved by an admin.
        var hasPayments = await _context.Invoices
            .AnyAsync(i => i.AppointmentId == request.AppointmentId && (i.PaidAmount > 0 || i.Status == "PAID" || i.Status == "PARTIAL"), ct)
            || await _context.Payments
            .AnyAsync(p => p.Invoice.AppointmentId == request.AppointmentId, ct);

        if (hasPayments)
        {
            return new ChangeReferrerResult
            {
                RequiresApproval = true,
                Message = "Payment has already been collected — changing the referrer needs admin approval."
            };
        }

        await ReferrerReassign.ApplyAsync(
            _context, request.AppointmentId, hospitalId,
            request.NewReferrerName, request.NewReferrerContact, request.NewReferrerIsDoctor, ct,
            supportedByDoctor: request.NewReferrerSupportedByDoctor,
            email: request.NewReferrerEmail, specialty: request.NewReferrerSpecialty,
            degree: request.NewReferrerDegree, address: request.NewReferrerAddress,
            supportedSpecialty: request.NewReferrerSupportedSpecialty,
            supportedDegree: request.NewReferrerSupportedDegree);

        await _context.SaveChangesAsync(ct);

        return new ChangeReferrerResult { Applied = true, Message = "Referrer updated." };
    }
}
