using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.SubmitReferralBookingRequest;

/// <summary>
/// A referring doctor asks the centre to book a patient, from their portal link (/r/{id}). Lands
/// as a PENDING <see cref="ReferralBookingRequest"/>, not a real Appointment - see that entity's
/// doc comment for why. Anonymous (capability-link) caller: the controller has already checked the
/// token is valid for this ReferrerId before dispatching this.
/// </summary>
public record SubmitReferralBookingRequestCommand(
    Guid ReferrerId,
    string PatientName,
    string? Mobile,
    string? Age,
    string? Gender,
    string? Modality,
    string? ServiceName,
    DateTime? PreferredDate,
    string? Notes
) : IRequest<Guid>;

public class SubmitReferralBookingRequestCommandHandler : IRequestHandler<SubmitReferralBookingRequestCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public SubmitReferralBookingRequestCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<Guid> Handle(SubmitReferralBookingRequestCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PatientName))
            throw new ValidationException("PatientName", "Please enter the patient's name.");

        var referrer = await _context.Referrers.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId && r.DeletedAt == null, ct);
        if (referrer == null)
            throw new NotFoundException("Referrer", request.ReferrerId);

        var patientName = Bounded(request.PatientName, 255, "PatientName", "The patient's name")!;
        var digits = new string((request.Mobile ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length > 20)
            throw new ValidationException("Mobile", "That mobile number is too long.");

        var entry = new ReferralBookingRequest
        {
            HospitalId = referrer.HospitalId,
            ReferrerId = referrer.ReferrerId,
            PatientName = patientName,
            Mobile = digits.Length == 0 ? null : digits,
            Age = Bounded(request.Age, 20, "Age", "Age"),
            Gender = Bounded(request.Gender, 20, "Gender", "Gender"),
            Modality = Bounded(request.Modality, 50, "Modality", "Scan type"),
            ServiceName = Bounded(request.ServiceName, 255, "ServiceName", "Service"),
            // A past preferred date is almost certainly a typo (day/month swap) rather than a real
            // request to book in the past - drop it and let the front desk ask, rather than silently
            // shifting a fresh request to the bottom of an implicitly date-sorted queue.
            PreferredDate = request.PreferredDate.HasValue && request.PreferredDate.Value.Date >= DateTime.UtcNow.Date
                ? request.PreferredDate.Value.Date
                : null,
            Notes = Bounded(request.Notes, 1000, "Notes", "The note"),
            Status = "PENDING",
        };

        _context.ReferralBookingRequests.Add(entry);
        await _context.SaveChangesAsync(ct);
        return entry.Id;
    }

    // The columns are fixed-width and this caller is anonymous: an over-long value is refused with a
    // message the doctor can act on, instead of reaching the database and failing as a masked 500.
    private static string? Bounded(string? value, int max, string field, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > max)
            throw new ValidationException(field, $"{label} is too long (at most {max} characters).");
        return trimmed;
    }
}
