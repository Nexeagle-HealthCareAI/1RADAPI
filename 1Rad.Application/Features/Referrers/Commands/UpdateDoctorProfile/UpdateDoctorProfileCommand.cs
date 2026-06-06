using System.Linq;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.UpdateDoctorProfile;

// Public, token-gated self-service profile update from the doctor portal
// (/r/{id}). The caller is the referring doctor via their capability link — the
// PublicReferralController validates the token before this runs. The doctor can
// keep their own identity + reach details current (name / contact / email) and
// their profile (location / specialty / degree). Scoped to the exact ReferrerId
// with query filters bypassed (an anonymous request carries no hospital/user
// context, so the global HospitalId filter would match nothing).
public record UpdateDoctorProfileCommand(
    Guid ReferrerId,
    string? Name,
    string? Location,
    string? Specialty,
    string? Degree,
    string? Email,
    string? Contact
) : IRequest<bool>;

public class UpdateDoctorProfileCommandHandler : IRequestHandler<UpdateDoctorProfileCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateDoctorProfileCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<bool> Handle(UpdateDoctorProfileCommand request, CancellationToken ct)
    {
        var referrer = await _context.Referrers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId && r.DeletedAt == null, ct);
        if (referrer == null) return false;

        // Trim to null so a cleared field stores null rather than whitespace.
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        // Name is identity — only overwrite when a non-empty value is supplied
        // (never let the doctor blank out their own name).
        var name = Clean(request.Name);
        if (name != null) referrer.Name = name;

        referrer.Address   = Clean(request.Location);   // "location" on the portal
        referrer.Specialty = Clean(request.Specialty);
        referrer.Degree    = Clean(request.Degree);
        referrer.Email     = Clean(request.Email);

        // Keep only the digits of the mobile (matches the admin-side format).
        var digits = new string((request.Contact ?? string.Empty).Where(char.IsDigit).ToArray());
        referrer.Contact = digits.Length == 0 ? null : digits;

        referrer.UpdatedAt = DateTime.UtcNow; // also auto-stamped on save; set for clarity

        await _context.SaveChangesAsync(ct);
        return true;
    }
}
