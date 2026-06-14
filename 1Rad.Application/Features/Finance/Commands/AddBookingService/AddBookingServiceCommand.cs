using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Application.Features.Finance.Queries.GetServiceCharges; // ServiceChargeDto
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.AddBookingService;

/// <summary>
/// "Add a service on the fly" during appointment booking, when the chosen
/// modality has no service in the centre's catalogue yet. Does the heavy lifting
/// server-side so the booking UI can stay a thin form:
///
///   • Service already exists WITH a price  → returns it unchanged (idempotent —
///     nothing to add; booking just uses it).
///   • Service exists but has NO price (Amount &lt;= 0) → sets the price + referral
///     cut, and back-fills a report template if it's missing.
///   • Service does not exist → creates a report template for the modality, then
///     the service (price + referral cut) linked to it.
///
/// Strictly tenant-scoped: the hospital is taken from the auth context, never the
/// client. Validates name/price/referral. Distinct from UpsertServiceChargeCommand
/// (which throws on a duplicate and never creates a template) so existing
/// catalogue-management behaviour is unchanged.
/// </summary>
public record AddBookingServiceCommand : IRequest<ServiceChargeDto>
{
    public string Modality { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public decimal ReferralCutValue { get; init; } = 0;
}

public class AddBookingServiceCommandHandler : IRequestHandler<AddBookingServiceCommand, ServiceChargeDto>
{
    private readonly IApplicationDbContext _context;

    // Sanity caps — defensive bounds so a fat-fingered / malicious value can't
    // poison the catalogue (a ₹10cr scan, a 10KB "service name", etc.).
    private const decimal MaxAmount = 100_000_000m; // ₹10 crore
    private const int MaxNameLength = 200;
    private const int MaxModalityLength = 50;

    public AddBookingServiceCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ServiceChargeDto> Handle(AddBookingServiceCommand request, CancellationToken cancellationToken)
    {
        // ── Tenant: always from the auth context, never the client. ──
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required to add a service.");

        // ── Validate + normalise inputs. ──
        var modality = (request.Modality ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(modality))
            throw new ArgumentException("Modality is required.", nameof(request.Modality));
        if (modality.Length > MaxModalityLength)
            throw new ArgumentException($"Modality must be {MaxModalityLength} characters or fewer.", nameof(request.Modality));

        // Collapse internal whitespace so "  Chest   X-Ray " == "Chest X-Ray".
        var serviceName = System.Text.RegularExpressions.Regex
            .Replace((request.ServiceName ?? string.Empty).Trim(), @"\s+", " ");
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("Service name is required.", nameof(request.ServiceName));
        if (serviceName.Length > MaxNameLength)
            throw new ArgumentException($"Service name must be {MaxNameLength} characters or fewer.", nameof(request.ServiceName));

        var amount = Math.Round(request.Amount, 2);
        if (amount <= 0)
            throw new ArgumentException("Price must be greater than zero.", nameof(request.Amount));
        if (amount > MaxAmount)
            throw new ArgumentException("Price is unrealistically large.", nameof(request.Amount));

        // Referral cut floored at 0 (a negative would become a negative payout)
        // and capped at the price (you can't pay a referrer more than the fee).
        var referralCut = Math.Round(Math.Max(0m, request.ReferralCutValue), 2);
        if (referralCut > amount)
            throw new ArgumentException("The referral amount cannot exceed the service price.", nameof(request.ReferralCutValue));

        // ── Find an existing catalogue entry (case-insensitive, this centre only). ──
        var existing = await _context.ServiceCharges
            .FirstOrDefaultAsync(s =>
                s.HospitalId == hospitalId &&
                s.Modality.ToUpper() == modality &&
                s.ServiceName.ToLower() == serviceName.ToLower(),
                cancellationToken);

        if (existing != null)
        {
            if (existing.Amount <= 0)
            {
                // Exists but unpriced — set the price + referral, and back-fill a
                // template if it never had one.
                existing.Amount = amount;
                existing.ReferralCutValue = referralCut;
                if (existing.TemplateId == null)
                {
                    var backfillTemplate = BuildTemplate(modality, serviceName, hospitalId);
                    _context.ReportTemplates.Add(backfillTemplate);
                    existing.TemplateId = backfillTemplate.Id;
                }
                await _context.SaveChangesAsync(cancellationToken);
            }
            // else: already priced — return as-is (idempotent; nothing to change).
            return ToDto(existing);
        }

        // ── New service → create the modality's report template + the service. ──
        var template = BuildTemplate(modality, serviceName, hospitalId);
        _context.ReportTemplates.Add(template);

        var entity = new ServiceCharge
        {
            Id = Guid.NewGuid(),
            HospitalId = hospitalId,
            Modality = modality,
            ServiceName = serviceName,
            Amount = amount,
            ReferralCutValue = referralCut,
            TemplateId = template.Id,
        };
        _context.ServiceCharges.Add(entity);

        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    private static ReportTemplate BuildTemplate(string modality, string serviceName, Guid hospitalId) => new()
    {
        Id = Guid.NewGuid(),
        HospitalId = hospitalId,
        Name = serviceName,
        Modality = modality,
        Content =
            "<p><strong>CLINICAL HISTORY:</strong></p><p><br></p>" +
            "<p><strong>TECHNIQUE:</strong></p><p>Routine protocol for " + System.Net.WebUtility.HtmlEncode(serviceName) + ".</p><p><br></p>" +
            "<p><strong>FINDINGS:</strong></p><p><br></p>" +
            "<p><strong>IMPRESSION:</strong></p><p><br></p>",
    };

    private static ServiceChargeDto ToDto(ServiceCharge s) => new()
    {
        Id = s.Id,
        Modality = s.Modality,
        ServiceName = s.ServiceName,
        Amount = s.Amount,
        ReferralCutValue = s.ReferralCutValue,
    };
}
