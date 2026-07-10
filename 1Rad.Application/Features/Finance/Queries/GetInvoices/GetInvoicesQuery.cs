using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Queries.GetInvoices;

public record GetInvoicesQuery : IRequest<List<InvoiceDto>>
{
    public string? Status { get; init; }
    public string? Search { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    // Sync engine knobs (Phase B3 Slice 1). UpdatedAfter restricts the
    // result to rows that changed since the client's last pull;
    // IncludeDeleted opts the sync engine into seeing tombstones so it
    // can apply DELETE semantics to its cache.
    public DateTime? UpdatedAfter { get; init; }
    public bool IncludeDeleted { get; init; }
    // Restrict to a single visit's invoice — used by the appointment-edit flow to
    // pull just that bill straight after a service add/remove.
    public Guid? AppointmentId { get; init; }
}

public class InvoiceDto
{
    public Guid InvoiceId { get; set; }
    public string DisplayId { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public string? PatientIdentifier { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal CentreDiscount { get; set; }
    public decimal ReferrerDiscount { get; set; }
    public decimal InstitutionalDeduction { get; set; }
    public decimal AdditionalCharges { get; set; }
    public string? AdditionalChargesReason { get; set; }
    public bool IsFree { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? ReferrerName { get; set; }
    public Guid? ReferrerId { get; set; }
    public string? Modality { get; set; }
    public decimal CommissionAmount { get; set; }
    public Guid? CommissionId { get; set; }
    public string? AppointmentStatus { get; set; }
    public Guid? AppointmentId { get; set; }
    public List<InvoiceItemDto> Items { get; set; } = new();
    // Sync fields. Populated for every row the sync engine pulls.
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class InvoiceItemDto
{
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int Quantity { get; set; }
    // Multi-service rollout. Filled from the linked AppointmentService
    // when the line is service-attached; null on freeform registry
    // lines or legacy rows pre-dating the AppointmentServiceId FK.
    public Guid? AppointmentServiceId { get; set; }
    public string? Modality { get; set; }
    // Per-line free test — this service is free; excluded from the payable total.
    public bool IsFree { get; set; }
}

public class GetInvoicesQueryHandler : IRequestHandler<GetInvoicesQuery, List<InvoiceDto>>
{
    private readonly IApplicationDbContext _context;

    public GetInvoicesQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<InvoiceDto>> Handle(GetInvoicesQuery request, CancellationToken cancellationToken)
    {
        try
        {
            // Defensive Check: If no hospital context is established, return an empty list rather than risking a cross-tenant query or a 500 error.
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new List<InvoiceDto>();
            }

            var query = _context.Invoices
                .AsNoTracking()
                .Where(i => i.HospitalId == _context.UserContext.HospitalId)
                .Include(i => i.Patient)
                    .ThenInclude(p => p.Referrer)
                .Include(i => i.Appointment)
                .AsQueryable();

            // Tombstone filter — the regular billing UI hides deleted rows;
            // the sync engine flips IncludeDeleted on so it can apply
            // DELETE semantics to its local cache.
            if (!request.IncludeDeleted)
            {
                query = query.Where(i => i.DeletedAt == null);
            }

            // Delta-fetch (B3 Slice 1). Runs against IX_Invoices_Hospital_
            // UpdatedAt so each sync poll is a small index range scan even
            // on a centre with years of invoices.
            if (request.UpdatedAfter.HasValue)
            {
                var since = request.UpdatedAfter.Value;
                query = query.Where(i => i.UpdatedAt > since);
            }

            // Status Filtering
            if (!string.IsNullOrEmpty(request.Status) && request.Status != "ALL")
            {
                query = query.Where(i => i.Status == request.Status);
            }

            // Search Filtering (Patient Name or Display ID)
            if (!string.IsNullOrEmpty(request.Search))
            {
                query = query.Where(i => i.Patient.FullName.Contains(request.Search) || i.InvoiceId.Contains(request.Search));
            }

            // Temporal Filtering
            if (request.StartDate.HasValue)
            {
                query = query.Where(i => i.CreatedAt >= request.StartDate.Value);
            }

            if (request.EndDate.HasValue)
            {
                query = query.Where(i => i.CreatedAt <= request.EndDate.Value);
            }

            // Single-visit refresh: the appointment-edit flow pulls just this
            // visit's invoice straight after a service add/remove so the Revenue
            // Hub reflects the new line items immediately (instead of waiting for
            // the next watermark sync).
            if (request.AppointmentId.HasValue)
            {
                query = query.Where(i => i.AppointmentId == request.AppointmentId.Value);
            }

            // Execute Projection: Using standard property initialization to ensure safe SQL translation in EF Core.
            var result = await query
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new InvoiceDto
                {
                    InvoiceId = i.Id,
                    DisplayId = i.InvoiceId,
                    PatientId = i.PatientId,
                    PatientName = i.Patient.FullName,
                    PatientIdentifier = i.Patient.PatientIdentifier,
                    GrossAmount = i.GrossAmount,
                    DiscountAmount = i.DiscountAmount,
                    CentreDiscount = i.CentreDiscount,
                    ReferrerDiscount = i.ReferrerDiscount,
                    InstitutionalDeduction = i.InstitutionalDeduction,
                    AdditionalCharges = i.AdditionalCharges,
                    AdditionalChargesReason = i.AdditionalChargesReason,
                    IsFree = i.IsFree,
                    TotalAmount = i.TotalAmount,
                    PaidAmount = i.PaidAmount,
                    BalanceAmount = i.TotalAmount - i.PaidAmount,
                    Status = i.Status,
                    CreatedAt = i.CreatedAt,
                    // Referrer reflects the actual commission (source of truth — it
                    // is written from the selected referrerId) so a MANUAL invoice's
                    // referrer matches the Referral Hub instead of falling back to the
                    // appointment's / patient's default referrer. Only when there is no
                    // commission row do we use those fallbacks.
                    // Referrer/commission fields are resolved in ONE batched pass
                    // after materialisation (see below) instead of 4 correlated
                    // subqueries per invoice. Here we seed only the FALLBACKS — a
                    // matching commission overrides them in memory, preserving the
                    // old `commission ?? fallback` precedence exactly.
                    ReferrerName = (i.Appointment != null ? i.Appointment.ReferredBy : (i.Patient.Referrer != null ? i.Patient.Referrer.Name : null)),
                    ReferrerId = i.Patient.ReferrerId,
                    Modality = i.Appointment != null ? i.Appointment.Modality : null,
                    CommissionAmount = 0,
                    CommissionId = null,
                    AppointmentStatus = i.Appointment != null ? i.Appointment.Status : null,
                    AppointmentId = i.AppointmentId,

                    Items = i.Items.Select(it => new InvoiceItemDto
                    {
                        Description = it.Description,
                        Amount = it.Amount,
                        Quantity = it.Quantity,
                        AppointmentServiceId = it.AppointmentServiceId,
                        IsFree = it.IsFree,
                        // Pull modality straight from the attached
                        // AppointmentService when present. Falls back
                        // to the visit's scalar Modality for legacy
                        // single-service rows that don't have an FK.
                        Modality = it.AppointmentServiceId.HasValue
                            ? _context.AppointmentServices
                                .Where(s => s.Id == it.AppointmentServiceId.Value)
                                .Select(s => s.Modality)
                                .FirstOrDefault()
                            : (i.Appointment != null ? i.Appointment.Modality : null)
                    }).ToList(),
                    UpdatedAt = i.UpdatedAt,
                    DeletedAt = i.DeletedAt
                })
                // Cap at 200 for the legacy paginated UI path. The sync
                // engine path uses ?updatedAfter= which is already bounded
                // by the time window, so the cap doesn't apply there in
                // practice — a centre with >200 invoices changed in 30s
                // is implausible. If it ever happens the next pull picks
                // up the rest.
                .Take(request.IncludeDeleted ? 100000 : 200)
                .ToListAsync(cancellationToken);

            // ── Batched referrer + commission resolution ──────────────────
            // One query for every commission touching the result set, then resolve
            // ReferrerName / ReferrerId / CommissionAmount / CommissionId per invoice
            // in memory — replaces 4 correlated subqueries PER ROW. A matching
            // commission OVERRIDES the seeded fallbacks (old `commission ?? fallback`).
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
                        // commission name overrides the fallback only when present
                        if (first.ReferrerName != null) inv.ReferrerName = first.ReferrerName;
                        inv.ReferrerId = first.ReferrerId;
                        inv.CommissionAmount = matched.Sum(c => c.CommissionAmount);
                        inv.CommissionId = first.Id;
                    }
                }
            }

            // Referrer-by-name fallback (was a per-row Referrers subquery): only for
            // invoices still without a referrer id but carrying a referrer name.
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

            // Backfill display items for invoices that have NONE but are linked
            // to an appointment — derive them from the live AppointmentService
            // lines so the Revenue Hub shows the services even when the invoice
            // was created without line items (or an OCC-blocked edit never
            // rebuilt them). Read-only — nothing is persisted.
            var itemlessApptIds = result
                .Where(inv => inv.Items.Count == 0 && inv.AppointmentId.HasValue)
                .Select(inv => inv.AppointmentId!.Value)
                .Distinct()
                .ToList();

            if (itemlessApptIds.Count > 0)
            {
                var svcByAppt = (await _context.AppointmentServices
                    .AsNoTracking()
                    .Where(s => itemlessApptIds.Contains(s.AppointmentId) && s.DeletedAt == null)
                    .Select(s => new { s.AppointmentId, s.Id, s.ServiceName, s.Modality, s.Amount })
                    .ToListAsync(cancellationToken))
                    .GroupBy(s => s.AppointmentId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var inv in result)
                {
                    if (inv.Items.Count == 0 && inv.AppointmentId.HasValue
                        && svcByAppt.TryGetValue(inv.AppointmentId.Value, out var svcs))
                    {
                        inv.Items = svcs.Select(s => new InvoiceItemDto
                        {
                            Description          = s.ServiceName,
                            Amount               = s.Amount,
                            Quantity             = 1,
                            AppointmentServiceId = s.Id,
                            Modality             = s.Modality,
                        }).ToList();
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve invoices: {ex.Message}", ex);
        }
    }
}
