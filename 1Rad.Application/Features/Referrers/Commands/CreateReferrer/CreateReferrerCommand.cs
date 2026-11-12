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
    string? SupportedByDoctor = null,
    string? Pan = null
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
            // Review #25: a same-name partner on a DIFFERENT mobile is a namesake, not a
            // duplicate. Reuse only when the mobiles agree (or one side has none).
            var upperName = NameNormalizer.Upper(request.Name);
            var newMobile = ReferrerResolver.MobileDigits(storedContact);
            var sameName = existing.Where(r => NameNormalizer.Normalize(r.Name) == normalized).ToList();
            var match = sameName.FirstOrDefault(r => newMobile.Length == 0 || ReferrerResolver.MobileDigits(r.Contact).Length == 0
                                                     || ReferrerResolver.MobileDigits(r.Contact) == newMobile);
            if (match == null && sameName.Any(r => r.Name == upperName))
                throw new ValidationException($"A partner named {upperName} already exists with a different mobile number. Add initials or the clinic name to tell them apart.");
            if (match != null)
            {
                // Backfill only what is missing; a filled profile or the partner's type is
                // changed on the partner's own edit screen, never by an "add" that matched.
                match.Name = NameNormalizer.Upper(match.Name); // canonicalise casing (#15)
                if (string.IsNullOrWhiteSpace(match.Contact) && !string.IsNullOrWhiteSpace(storedContact))
                    match.Contact = storedContact;
                if (string.IsNullOrWhiteSpace(match.Address) && !string.IsNullOrWhiteSpace(request.Address))
                    match.Address = NameNormalizer.Upper(request.Address);
                if (string.IsNullOrWhiteSpace(match.Email) && !string.IsNullOrWhiteSpace(request.Email))         match.Email     = request.Email.Trim();
                if (string.IsNullOrWhiteSpace(match.Specialty) && !string.IsNullOrWhiteSpace(request.Specialty)) match.Specialty = NameNormalizer.Upper(request.Specialty);
                if (string.IsNullOrWhiteSpace(match.Degree) && !string.IsNullOrWhiteSpace(request.Degree))       match.Degree    = NameNormalizer.Upper(request.Degree);
                if (string.IsNullOrWhiteSpace(match.SupportedByDoctor) && !string.IsNullOrWhiteSpace(request.SupportedByDoctor))
                    match.SupportedByDoctor = NameNormalizer.Upper(request.SupportedByDoctor);
                if (string.IsNullOrWhiteSpace(match.Pan) && ReferralTds.IsValidPan(request.Pan?.Trim().ToUpperInvariant()))
                    match.Pan = request.Pan!.Trim().ToUpperInvariant();
                await _context.SaveChangesAsync(cancellationToken);
                return match.ReferrerId;
            }
        }

        var referrer = new Referrer
        {
            Name = NameNormalizer.Upper(request.Name),
            Contact = storedContact,
            Address = NameNormalizer.Upper(request.Address),
            HospitalId = hospitalId,
            Email     = string.IsNullOrWhiteSpace(request.Email)     ? null : request.Email.Trim(),
            Specialty = NameNormalizer.UpperOrNull(request.Specialty),
            Degree    = NameNormalizer.UpperOrNull(request.Degree),
            IsDoctor  = request.IsDoctor,
            SupportedByDoctor = NameNormalizer.UpperOrNull(request.SupportedByDoctor),
            Pan = ReferralTds.IsValidPan(request.Pan?.Trim().ToUpperInvariant()) ? request.Pan!.Trim().ToUpperInvariant() : null,
        };

        _context.Referrers.Add(referrer);
        await _context.SaveChangesAsync(cancellationToken);

        return referrer.ReferrerId;
    }
}
