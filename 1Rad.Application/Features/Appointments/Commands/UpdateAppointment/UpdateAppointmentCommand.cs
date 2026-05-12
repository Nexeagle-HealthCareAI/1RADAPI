using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.UpdateAppointment;

public record UpdateAppointmentCommand(
    Guid AppointmentId,
    string Service,
    string Modality,
    DateTime DateTime,
    string Doctor,
    string Notes,
    string ReferredBy,
    string? PatientName = null,
    string? Mobile = null,
    string? PatientAge = null,
    decimal? Amount = null,
    string? ReferralCutType = null,
    decimal? ReferralCutValue = null
) : IRequest<bool>;

public class UpdateAppointmentCommandHandler : IRequestHandler<UpdateAppointmentCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateAppointmentCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateAppointmentCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _context.Appointments
            .Include(a => a.Patient)
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId, cancellationToken);

        if (appointment == null) return false;

        // Update Appointment fields
        appointment.Service = request.Service;
        appointment.Modality = request.Modality;
        appointment.DateTime = request.DateTime;
        appointment.Doctor = request.Doctor;
        appointment.Notes = request.Notes;
        appointment.ReferredBy = request.ReferredBy;

        // Update denormalized patient info if provided
        if (!string.IsNullOrEmpty(request.PatientName)) appointment.PatientName = request.PatientName;
        if (!string.IsNullOrEmpty(request.Mobile)) appointment.Mobile = request.Mobile;

        // Update the underlying Patient entity as well
        if (appointment.Patient != null)
        {
            if (!string.IsNullOrEmpty(request.PatientName)) appointment.Patient.FullName = request.PatientName;
            if (!string.IsNullOrEmpty(request.Mobile)) appointment.Patient.Mobile = request.Mobile;
            if (!string.IsNullOrEmpty(request.PatientAge)) appointment.Patient.Age = request.PatientAge;
        }

        // Handle Invoice updates if amount or cuts changed
        var invoice = await _context.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.AppointmentId == request.AppointmentId, cancellationToken);

        if (invoice != null)
        {
            if (request.Amount.HasValue && invoice.Items.Any())
            {
                var item = invoice.Items.First();
                item.Amount = request.Amount.Value;
                invoice.TotalAmount = request.Amount.Value;
                invoice.BalanceAmount = invoice.TotalAmount - invoice.PaidAmount;
            }

            if (!string.IsNullOrEmpty(request.ReferralCutType)) invoice.ReferralCutType = request.ReferralCutType;
            if (request.ReferralCutValue.HasValue) invoice.ReferralCutValue = request.ReferralCutValue.Value;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
