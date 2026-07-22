using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Queries.GetInvoices;

public class InvoiceEnrichmentService : IInvoiceEnrichmentService
{
    private readonly IApplicationDbContext _context;

    public InvoiceEnrichmentService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task EnrichInvoicesAsync(List<InvoiceDto> result, CancellationToken cancellationToken)
    {
        if (result == null || result.Count == 0) return;

        var commHospitalId = _context.UserContext.HospitalId;
        var commApptIds = result.Where(r => r.AppointmentId.HasValue).Select(r => r.AppointmentId!.Value).Distinct().ToList();
        var commDisplayIds = result.Select(r => r.DisplayId).Where(d => !string.IsNullOrEmpty(d)).Distinct().ToList();

        if (commApptIds.Count > 0 || commDisplayIds.Count > 0)
        {
            var comms = await _context.ReferralCommissions
                .AsNoTracking()
                .Where(c => c.HospitalId == commHospitalId
                    && ((c.AppointmentId != null && commApptIds.Contains(c.AppointmentId.Value))
                        || (c.ReferenceNumber != null && commDisplayIds.Contains(c.ReferenceNumber))))
                .Select(c => new { c.Id, c.AppointmentId, c.ReferenceNumber, c.ReferrerId, c.ReferrerName, c.CommissionAmount })
                .ToListAsync(cancellationToken);

            if (comms.Count > 0)
            {
                foreach (var inv in result)
                {
                    var matched = comms.Where(c =>
                        (inv.AppointmentId.HasValue && c.AppointmentId == inv.AppointmentId)
                        || (!string.IsNullOrEmpty(inv.DisplayId) && c.ReferenceNumber == inv.DisplayId))
                        .ToList();
                    if (matched.Count == 0) continue;

                    var first = matched[0];
                    if (first.ReferrerName != null) inv.ReferrerName = first.ReferrerName;
                    inv.ReferrerId = first.ReferrerId;
                    inv.CommissionAmount = matched.Sum(c => c.CommissionAmount);
                    inv.CommissionId = first.Id;
                }
            }
        }

        var unresolvedNames = result
            .Where(r => !r.ReferrerId.HasValue && !string.IsNullOrEmpty(r.ReferrerName))
            .Select(r => r.ReferrerName!)
            .Distinct()
            .ToList();

        if (unresolvedNames.Count > 0)
        {
            var refIdByName = (await _context.Referrers
                .AsNoTracking()
                .Where(r => r.HospitalId == commHospitalId && r.Name != null && unresolvedNames.Contains(r.Name))
                .Select(r => new { r.Name, r.ReferrerId })
                .ToListAsync(cancellationToken))
                .GroupBy(r => r.Name!)
                .ToDictionary(g => g.Key, g => g.First().ReferrerId);

            foreach (var inv in result)
            {
                if (!inv.ReferrerId.HasValue && !string.IsNullOrEmpty(inv.ReferrerName)
                    && refIdByName.TryGetValue(inv.ReferrerName, out var rid))
                {
                    inv.ReferrerId = rid;
                }
            }
        }

        var appointmentInvoiceIds = result
            .Where(inv => inv.AppointmentId.HasValue
                && (inv.Items.Count == 0 || inv.Items.Any(item => item.AppointmentServiceId.HasValue)))
            .Select(inv => inv.AppointmentId!.Value)
            .Distinct()
            .ToList();

        if (appointmentInvoiceIds.Count > 0)
        {
            var svcByAppt = (await _context.AppointmentServices
                .AsNoTracking()
                .Where(s => appointmentInvoiceIds.Contains(s.AppointmentId) && s.DeletedAt == null)
                .Select(s => new { s.AppointmentId, s.Id, s.ServiceName, s.Modality, s.Amount })
                .ToListAsync(cancellationToken))
                .GroupBy(s => s.AppointmentId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var inv in result)
            {
                if (!inv.AppointmentId.HasValue
                    || !svcByAppt.TryGetValue(inv.AppointmentId.Value, out var svcs))
                    continue;

                var itemServiceIds = inv.Items
                    .Where(item => item.AppointmentServiceId.HasValue)
                    .Select(item => item.AppointmentServiceId!.Value)
                    .ToHashSet();

                foreach (var svc in svcs.Where(service => !itemServiceIds.Contains(service.Id)))
                {
                    inv.Items.Add(new InvoiceItemDto
                    {
                        Description          = svc.ServiceName,
                        Amount               = svc.Amount,
                        Quantity             = 1,
                        AppointmentServiceId = svc.Id,
                        Modality             = svc.Modality,
                    });
                }
            }
        }
    }
}
