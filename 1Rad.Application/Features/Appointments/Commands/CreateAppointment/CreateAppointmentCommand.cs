using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.CreateAppointment;

public record CreateAppointmentCommand(
    Guid PatientId,
    string Service,
    string Modality,
    DateTime DateTime,
    string Type,
    string Doctor,
    string ReferredBy,
    string ReferredContact,
    string Notes,
    decimal Amount = 0,
    string? ReferralCutType = null,
    decimal? ReferralCutValue = null
) : IRequest<Guid>;

public class CreateAppointmentCommandHandler : IRequestHandler<CreateAppointmentCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public CreateAppointmentCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(CreateAppointmentCommand request, CancellationToken cancellationToken)
    {
        var patient = await _context.Patients
            .FirstOrDefaultAsync(p => p.PatientId == request.PatientId, cancellationToken);
            
        if (patient == null) throw new Exception("Patient not found.");

        var count = await _context.Appointments.CountAsync(cancellationToken);
        
        var appointment = new Appointment
        {
            DisplayId = $"APP-{101 + count}",
            PatientId = request.PatientId,
            PatientName = patient.FullName,
            Mobile = patient.Mobile,
            Service = request.Service,
            Modality = request.Modality,
            DateTime = request.DateTime,
            Type = request.Type,
            Doctor = request.Doctor,
            Status = "scheduled",
            ReferredBy = request.ReferredBy,
            ReferredContact = request.ReferredContact,
            Notes = request.Notes,
            HospitalId = _context.UserContext.HospitalId != Guid.Empty 
                ? _context.UserContext.HospitalId 
                : patient.HospitalId
        };

        _context.Appointments.Add(appointment);

        // Create Invoice if amount is provided
        if (request.Amount > 0)
        {
            var invoice = new Invoice
            {
                AppointmentId = appointment.AppointmentId,
                PatientId = request.PatientId,
                PatientName = patient.FullName ?? "UNKNOWN",
                HospitalId = appointment.HospitalId,
                InvoiceId = $"INV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}",
                GrossAmount = request.Amount,
                DiscountAmount = 0,
                TotalAmount = request.Amount,
                PaidAmount = 0,
                Status = "PENDING",
                ReferralCutType = request.ReferralCutType ?? "PERCENTAGE",
                ReferralCutValue = request.ReferralCutValue ?? 0,
                CreatedAt = DateTime.UtcNow
            };

            invoice.Items.Add(new InvoiceItem
            {
                Description = request.Service,
                Amount = request.Amount,
                Quantity = 1
            });

            _context.Invoices.Add(invoice);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return appointment.AppointmentId;
    }
}
