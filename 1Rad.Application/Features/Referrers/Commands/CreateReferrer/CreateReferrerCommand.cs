using System;
using System.Linq;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.CreateReferrer;

public record CreateReferrerCommand(
    string Name,
    string Contact,
    string Address,
    string? Email = null,
    string? Specialty = null,
    string? Degree = null,
    bool IsDoctor = true,
    string? SupportedByDoctor = null
) : IRequest<Guid>;

public class CreateReferrerCommandHandler : IRequestHandler<CreateReferrerCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public CreateReferrerCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(CreateReferrerCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationException("Name", "Referrer name is required.");
        }

        // Contact is optional and unvalidated. Light-sanitize any digits the
        // caller supplied (strip 91/0 prefixes) so stored numbers stay tidy,
        // but accept blank or non-standard values without rejecting.
        var contact = (request.Contact ?? string.Empty).Trim();
        var digits = new string(contact.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("91") && digits.Length == 12)
        {
            digits = digits.Substring(2);
        }
        else if (digits.StartsWith("0") && digits.Length == 11)
        {
            digits = digits.Substring(1);
        }
        var storedContact = digits.Length > 0 ? digits : contact;

        // Dedup SAFETY NET — if a referrer with the same normalised name already
        // exists for this hospital (casing/spacing/honorific/punctuation
        // variant of "Dr Sharma"), reuse it instead of spawning a duplicate.
        // The client-side fuzzy "did you mean" handles looser spelling drift.
        var hospitalId = _context.UserContext.HospitalId;
        var normalized = NameNormalizer.Normalize(request.Name);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            var existing = await _context.Referrers
                .Where(r => r.HospitalId == hospitalId && r.DeletedAt == null)
                .ToListAsync(cancellationToken);
            var match = existing.FirstOrDefault(r => NameNormalizer.Normalize(r.Name) == normalized);
            if (match != null)
            {
                // Backfill a missing contact/address from the new submission.
                if (string.IsNullOrWhiteSpace(match.Contact) && !string.IsNullOrWhiteSpace(storedContact))
                    match.Contact = storedContact;
                if (string.IsNullOrWhiteSpace(match.Address) && !string.IsNullOrWhiteSpace(request.Address))
                    match.Address = request.Address;
                // Fill in any doctor-profile fields this submission supplied.
                if (!string.IsNullOrWhiteSpace(request.Email))     match.Email     = request.Email.Trim();
                if (!string.IsNullOrWhiteSpace(request.Specialty)) match.Specialty = request.Specialty.Trim();
                if (!string.IsNullOrWhiteSpace(request.Degree))    match.Degree    = request.Degree.Trim();
                match.IsDoctor = request.IsDoctor;
                match.SupportedByDoctor = string.IsNullOrWhiteSpace(request.SupportedByDoctor) ? match.SupportedByDoctor : request.SupportedByDoctor.Trim();
                await _context.SaveChangesAsync(cancellationToken);
                return match.ReferrerId;
            }
        }

        var referrer = new Referrer
        {
            Name = request.Name,
            Contact = storedContact,
            Address = request.Address,
            HospitalId = hospitalId,
            Email     = string.IsNullOrWhiteSpace(request.Email)     ? null : request.Email.Trim(),
            Specialty = string.IsNullOrWhiteSpace(request.Specialty) ? null : request.Specialty.Trim(),
            Degree    = string.IsNullOrWhiteSpace(request.Degree)    ? null : request.Degree.Trim(),
            IsDoctor  = request.IsDoctor,
            SupportedByDoctor = string.IsNullOrWhiteSpace(request.SupportedByDoctor) ? null : request.SupportedByDoctor.Trim(),
        };

        _context.Referrers.Add(referrer);
        await _context.SaveChangesAsync(cancellationToken);

        return referrer.ReferrerId;
    }
}
