using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.CreateReferrersBulk;

/// <summary>One row of a bulk partner upload (inline grid or Excel).</summary>
public record ReferrerBulkItem(
    string Name,
    string? Contact = null,
    string? Address = null,
    string? Email = null,
    string? Specialty = null,
    string? Degree = null,
    bool IsDoctor = true,
    string? SupportedByDoctor = null
);

public record CreateReferrersBulkCommand(List<ReferrerBulkItem> Referrers) : IRequest<BulkReferrerResult>;

public record BulkReferrerResult(int Created, int Merged, int Skipped);

/// <summary>
/// Bulk-create referrers from the inline multi-add grid or an Excel upload.
/// Loads the hospital's referrers ONCE and de-dupes every row against that set
/// AND against rows already processed in the same batch (so a duplicate inside
/// the upload collapses too) — honorific/casing-insensitive, matching the
/// single-create path. Names are stored UPPERCASE. One SaveChanges for the lot.
/// </summary>
public class CreateReferrersBulkCommandHandler : IRequestHandler<CreateReferrersBulkCommand, BulkReferrerResult>
{
    private readonly IApplicationDbContext _context;

    public CreateReferrersBulkCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<BulkReferrerResult> Handle(CreateReferrersBulkCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required.");

        var items = request.Referrers ?? new List<ReferrerBulkItem>();
        int created = 0, merged = 0, skipped = 0;

        var existing = await _context.Referrers
            .Where(r => r.HospitalId == hospitalId && r.DeletedAt == null)
            .ToListAsync(ct);

        var byKey = new Dictionary<string, Referrer>();
        foreach (var r in existing)
        {
            var k = NameNormalizer.Normalize(r.Name);
            if (k.Length > 0 && !byKey.ContainsKey(k)) byKey[k] = r;
        }

        foreach (var item in items)
        {
            var key = NameNormalizer.Normalize(item?.Name);
            if (key.Length == 0) { skipped++; continue; } // no usable name

            var contact = SanitizeContact(item!.Contact);

            if (byKey.TryGetValue(key, out var match))
            {
                // Merge — backfill blanks, refresh any supplied profile fields.
                match.Name = NameNormalizer.Upper(match.Name);
                if (string.IsNullOrWhiteSpace(match.Contact) && !string.IsNullOrWhiteSpace(contact)) match.Contact = contact;
                if (string.IsNullOrWhiteSpace(match.Address) && !string.IsNullOrWhiteSpace(item.Address)) match.Address = NameNormalizer.Upper(item.Address);
                if (!string.IsNullOrWhiteSpace(item.Email)) match.Email = item.Email.Trim();
                if (!string.IsNullOrWhiteSpace(item.Specialty)) match.Specialty = NameNormalizer.Upper(item.Specialty);
                if (!string.IsNullOrWhiteSpace(item.Degree)) match.Degree = NameNormalizer.Upper(item.Degree);
                match.IsDoctor = item.IsDoctor;
                if (!string.IsNullOrWhiteSpace(item.SupportedByDoctor)) match.SupportedByDoctor = NameNormalizer.Upper(item.SupportedByDoctor);
                merged++;
                continue;
            }

            var referrer = new Referrer
            {
                Name = NameNormalizer.Upper(item.Name),
                Contact = contact ?? string.Empty,
                Address = NameNormalizer.Upper(item.Address),
                HospitalId = hospitalId,
                Email = string.IsNullOrWhiteSpace(item.Email) ? null : item.Email.Trim(),
                Specialty = NameNormalizer.UpperOrNull(item.Specialty),
                Degree = NameNormalizer.UpperOrNull(item.Degree),
                IsDoctor = item.IsDoctor,
                SupportedByDoctor = NameNormalizer.UpperOrNull(item.SupportedByDoctor),
            };
            _context.Referrers.Add(referrer);
            byKey[key] = referrer;
            created++;
        }

        if (created > 0 || merged > 0)
            await _context.SaveChangesAsync(ct);

        return new BulkReferrerResult(created, merged, skipped);
    }

    private static string? SanitizeContact(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("91") && digits.Length == 12) digits = digits.Substring(2);
        else if (digits.StartsWith("0") && digits.Length == 11) digits = digits.Substring(1);
        return digits.Length > 0 ? digits : raw.Trim();
    }
}
