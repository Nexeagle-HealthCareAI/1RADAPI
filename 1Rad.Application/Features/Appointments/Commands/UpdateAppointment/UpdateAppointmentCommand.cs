using System;
using System.Linq;
using System.Text.RegularExpressions;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
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

        bool dateChanged = appointment.DateTime.Date != request.DateTime.Date;

        // If the date has changed, calculate a new token number for the new date
        if (dateChanged)
        {
            var appointmentDate = request.DateTime.Date;
            var newDailyTokenNumber = await _context.Appointments
                .CountAsync(a => a.HospitalId == appointment.HospitalId && a.DateTime.Date == appointmentDate, cancellationToken) + 1;
            
            appointment.DailyTokenNumber = newDailyTokenNumber;
        }

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
            if (dateChanged)
            {
                invoice.CreatedAt = request.DateTime;
            }
            if (request.Amount.HasValue && invoice.Items.Any())
            {
                var item = invoice.Items.First();
                item.Amount = request.Amount.Value;
                invoice.TotalAmount = request.Amount.Value;
                invoice.GrossAmount = request.Amount.Value; // Sync gross if updating from appointment
            }


            if (request.ReferralCutValue.HasValue) invoice.ReferralCutValue = request.ReferralCutValue.Value;

        }

        // --- REFERRAL COMMISSION SYNC ---
        var commission = await _context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.AppointmentId == request.AppointmentId, cancellationToken);

        decimal finalCut = request.ReferralCutValue ?? (invoice?.ReferralCutValue ?? 0);

        if (commission != null)
        {
            if (dateChanged)
            {
                commission.TransactionDate = request.DateTime;
            }
            if (finalCut <= 0 || string.IsNullOrEmpty(request.ReferredBy))
            {
                // If cut removed or referrer removed, set commission to 0 to preserve audit trail
                commission.CommissionAmount = 0;
            }
            else
            {
                commission.CommissionAmount = finalCut;
                commission.Modality = request.Modality;
                
                var searchName = request.ReferredBy.Trim();
                var referrer = await _context.Referrers
                    .FirstOrDefaultAsync(r => r.Name.ToLower() == searchName.ToLower() && r.HospitalId == appointment.HospitalId, cancellationToken);
                
                if (referrer == null)
                {
                    referrer = new Referrer
                    {
                        Name = searchName,
                        Contact = string.Empty,
                        Address = string.Empty,
                        HospitalId = appointment.HospitalId
                    };
                    _context.Referrers.Add(referrer);
                }

                commission.ReferrerId = referrer.ReferrerId;
                commission.ReferrerName = referrer.Name ?? request.ReferredBy;
            }
        }
        else if (finalCut > 0 && !string.IsNullOrEmpty(request.ReferredBy))
        {
            var searchName = request.ReferredBy.Trim();
            var referrer = await _context.Referrers
                .FirstOrDefaultAsync(r => r.Name.ToLower() == searchName.ToLower() && r.HospitalId == appointment.HospitalId, cancellationToken);

            if (referrer == null)
            {
                referrer = new Referrer
                {
                    Name = searchName,
                    Contact = string.Empty,
                    Address = string.Empty,
                    HospitalId = appointment.HospitalId
                };
                _context.Referrers.Add(referrer);
            }

            var newCommission = new ReferralCommission
            {
                ReferrerId = referrer.ReferrerId,
                ReferrerName = referrer.Name ?? request.ReferredBy,
                Modality = request.Modality,
                CommissionAmount = finalCut,
                Status = "UNPAID",
                TransactionDate = DateTime.UtcNow,
                HospitalId = appointment.HospitalId,
                AppointmentId = appointment.AppointmentId
            };
            _context.ReferralCommissions.Add(newCommission);
        }


        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
