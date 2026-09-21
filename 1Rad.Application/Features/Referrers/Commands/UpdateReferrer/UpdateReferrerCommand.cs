using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.UpdateReferrer;

public record UpdateReferrerCommand(
    Guid ReferrerId,
    string Name,
    string Contact,
    string Address,
    string? Email = null,
    string? Specialty = null,
    string? Degree = null,
    bool IsDoctor = true,
    string? SupportedByDoctor = null
) : IRequest<bool>;

public class UpdateReferrerCommandHandler : IRequestHandler<UpdateReferrerCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateReferrerCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateReferrerCommand request, CancellationToken cancellationToken)
    {
        var contact = request.Contact ?? string.Empty;
        var digits = new string(contact.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("91") && digits.Length == 12)
        {
            digits = digits.Substring(2);
        }
        else if (digits.StartsWith("0") && digits.Length == 11)
        {
            digits = digits.Substring(1);
        }

        // Mobile is optional (an agent may have none). Only validate the format
        // when a number was actually entered.
        if (digits.Length > 0 && (digits.Length != 10 || !Regex.IsMatch(digits, "^[6-9][0-9]{9}$")))
        {
            throw new ValidationException("Contact", "Please enter a valid 10-digit Indian mobile number.");
        }

        var referrer = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId, cancellationToken);

        if (referrer == null) return false;

        // Stored form is trimmed + UPPERCASE, the same as booking and Create (#15).
        var newName = NameNormalizer.Upper(request.Name);
        if (newName.Length == 0)
            throw new ValidationException("Name", "Referrer name is required.");

        var oldName = referrer.Name ?? string.Empty;
        var renamed = !string.Equals(oldName.Trim(), newName, StringComparison.Ordinal);
        if (renamed)
        {
            // Two live partners may not share a name (unique index). Say so plainly instead of
            // letting the database throw a 500 - the way to combine two records is Merge.
            var lower = newName.ToLower();
            var taken = await _context.Referrers.AnyAsync(r =>
                r.ReferrerId != request.ReferrerId && r.HospitalId == referrer.HospitalId
                && r.DeletedAt == null && r.Name.ToLower() == lower, cancellationToken);
            if (taken)
                throw new ConflictException($"Another partner is already named \"{newName}\". To combine the two records, merge them instead of renaming.");

            await PropagateRenameAsync(referrer, oldName, newName, cancellationToken);
        }

        referrer.Name = newName;
        referrer.Contact = digits;
        referrer.Address = request.Address;
        referrer.Email     = string.IsNullOrWhiteSpace(request.Email)     ? null : request.Email.Trim();
        referrer.Specialty = string.IsNullOrWhiteSpace(request.Specialty) ? null : request.Specialty.Trim();
        referrer.Degree    = string.IsNullOrWhiteSpace(request.Degree)    ? null : request.Degree.Trim();
        referrer.IsDoctor  = request.IsDoctor;
        referrer.SupportedByDoctor = string.IsNullOrWhiteSpace(request.SupportedByDoctor) ? null : request.SupportedByDoctor.Trim();

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Carries a rename to the places that hold the partner's name as TEXT. Visits are keyed by
    /// Appointment.ReferrerId, so history stays with the partner regardless; this keeps the
    /// name text on the visits and on the commission rows in step so the exports, the ledger
    /// and the name-keyed finance reports read the new name everywhere. Visits that predate the
    /// id column (no ReferrerId, but the old name) are re-linked by id at the same time, so a
    /// rename never orphans them even when the backfill script has not been run.
    /// </summary>
    private async Task PropagateRenameAsync(_1Rad.Domain.Entities.Referrer referrer, string oldName, string newName, CancellationToken ct)
    {
        var id = referrer.ReferrerId;
        var hospitalId = referrer.HospitalId;
        var oldLower = oldName.Trim().ToLower();

        var visits = await _context.Appointments
            .Where(a => a.HospitalId == hospitalId
                && (a.ReferrerId == id
                    || (a.ReferrerId == null && oldLower.Length > 0 && a.ReferredBy != null && a.ReferredBy.ToLower() == oldLower)))
            .ToListAsync(ct);
        foreach (var v in visits)
        {
            v.ReferrerId = id;
            if (!string.Equals(v.ReferredBy, newName, StringComparison.Ordinal)) v.ReferredBy = newName;
        }

        var commissions = await _context.ReferralCommissions
            .Where(c => c.ReferrerId == id && c.HospitalId == hospitalId && c.ReferrerName != newName)
            .ToListAsync(ct);
        foreach (var c in commissions) c.ReferrerName = newName;
    }
}
