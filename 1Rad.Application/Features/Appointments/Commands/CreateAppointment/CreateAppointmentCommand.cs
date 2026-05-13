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
            PatientName = patient.FullName ?? "Unknown",
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
                PatientName = patient.FullName ?? "Unknown",
                HospitalId = appointment.HospitalId,
                InvoiceId = $"INV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}",
                GrossAmount = request.Amount,
                DiscountAmount = 0,
                TotalAmount = request.Amount,
                PaidAmount = 0,
                Status = "PENDING",
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

        // --- REFERRAL SYNCHRONIZATION ---
        if (!string.IsNullOrEmpty(request.ReferredBy))
        {
            var referrer = await _context.Referrers
                .FirstOrDefaultAsync(r => r.Name == request.ReferredBy && r.HospitalId == appointment.HospitalId, cancellationToken);

            if (referrer != null)
            {
                // Ensure patient record is linked to this referrer for longitudinal tracking
                patient.ReferrerId = referrer.ReferrerId;

                // Record commission even if amount is zero to maintain mission audit trail
                var commission = new ReferralCommission
                {
                    ReferrerId = referrer.ReferrerId,
                    ReferrerName = referrer.Name ?? request.ReferredBy ?? "Self-Referral",
                    Modality = request.Modality,
                    CommissionAmount = request.ReferralCutValue ?? 0,
                    Status = "UNPAID",
                    TransactionDate = DateTime.UtcNow,
                    HospitalId = appointment.HospitalId,
                    AppointmentId = appointment.AppointmentId
                };

                _context.ReferralCommissions.Add(commission);
            }
        }


        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            var innerMessage = ex.InnerException?.Message ?? "No inner exception details";
            throw new Exception($"MISSION PERSISTENCE FAILURE: {ex.Message}. Database says: {innerMessage}", ex);
        }
        catch (Exception ex)
        {
            throw new Exception($"UNEXPECTED SYSTEM ERROR: {ex.Message}", ex);
        }

        return appointment.AppointmentId;
    }
}
