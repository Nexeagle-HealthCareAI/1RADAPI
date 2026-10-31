using System.Linq;
using System.Text.Json;
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
) : IRequest<UpdateDoctorProfileResult>;

/// <summary>ContactChangePending: the new mobile/email waits for the centre to approve it.</summary>
public record UpdateDoctorProfileResult(bool Found, bool ContactChangePending);

public class UpdateDoctorProfileCommandHandler : IRequestHandler<UpdateDoctorProfileCommand, UpdateDoctorProfileResult>
{
    public const string ApprovalType = "REFERRER_CONTACT";
    private readonly IApplicationDbContext _context;

    public UpdateDoctorProfileCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<UpdateDoctorProfileResult> Handle(UpdateDoctorProfileCommand request, CancellationToken ct)
    {
        var referrer = await _context.Referrers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId && r.DeletedAt == null, ct);
        if (referrer == null) return new UpdateDoctorProfileResult(false, false);

        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        // The NAME is not editable from the public link (it is the payee's identity on
        // every appointment, payout and report). The field is accepted and ignored.
        //
        // Review R8: a blank field never clears what the centre holds — the portal only
        // adds or corrects. Profile details apply at once.
        referrer.Address   = Clean(request.Location)  ?? referrer.Address;
        referrer.Specialty = Clean(request.Specialty) ?? referrer.Specialty;
        referrer.Degree    = Clean(request.Degree)    ?? referrer.Degree;

        // Review R8: mobile and email are where every future link is sent. Anyone
        // holding a forwarded link could redirect them and take over the portal, so a
        // change becomes a request the centre approves (Approvals → Referrer contact).
        var digits = new string((request.Contact ?? string.Empty).Where(char.IsDigit).ToArray());
        var newContact = digits.Length == 0 ? null : digits;
        var newEmail = Clean(request.Email);
        var contactChanged = newContact != null && newContact != referrer.Contact;
        var emailChanged = newEmail != null && !string.Equals(newEmail, referrer.Email, StringComparison.OrdinalIgnoreCase);

        var pending = false;
        if (contactChanged || emailChanged)
        {
            var payload = JsonSerializer.Serialize(new
            {
                referrerId = referrer.ReferrerId,
                contact = contactChanged ? newContact : null,
                email = emailChanged ? newEmail : null,
                previousContact = referrer.Contact,
                previousEmail = referrer.Email,
            });
            var existing = await _context.ApprovalRequests
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.HospitalId == referrer.HospitalId && a.Type == ApprovalType
                                          && a.Status == "PENDING" && a.DeletedAt == null
                                          && a.Payload.Contains(referrer.ReferrerId.ToString()), ct);
            var title = $"{referrer.Name}: new contact details from the referral portal";
            var reason = string.Join(" · ", new[]
            {
                contactChanged ? $"Mobile {Mask(referrer.Contact)} → {newContact}" : null,
                emailChanged ? $"Email {referrer.Email ?? "none"} → {newEmail}" : null,
            }.Where(x => x != null));
            if (existing != null)
            {
                existing.Payload = payload;
                existing.Title = title;
                existing.Reason = reason;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _context.ApprovalRequests.Add(new Domain.Entities.ApprovalRequest
                {
                    HospitalId = referrer.HospitalId,
                    Type = ApprovalType,
                    Title = title,
                    Reason = reason,
                    Payload = payload,
                    Status = "PENDING",
                    RequestedBy = Guid.Empty, // the referrer, via their link — not a staff user
                });
            }
            pending = true;
        }

        referrer.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        return new UpdateDoctorProfileResult(true, pending);
    }

    private static string Mask(string? contact) =>
        string.IsNullOrEmpty(contact) ? "none" : contact.Length <= 4 ? contact : new string('•', contact.Length - 4) + contact[^4..];
}
