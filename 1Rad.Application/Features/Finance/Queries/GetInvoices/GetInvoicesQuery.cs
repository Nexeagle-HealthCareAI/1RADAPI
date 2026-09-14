using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Queries.GetInvoices;

// ── Pagination DTO ─────────────────────────────────────────────────────────
// Cursor is base-64(createdAt_ticks + "|" + invoiceGuid). null = first page.
// Returned nextCursor is null when there is no further page.
public class PagedInvoiceResult
{
    public List<InvoiceDto> Items { get; set; } = new();
    public string? NextCursor { get; set; }     // null = last page
    public int TotalCount { get; set; }         // full filtered count (for display)
    public bool IsPaged { get; set; }           // false on the sync-engine path
}

public record GetInvoicesQuery : IRequest<PagedInvoiceResult>
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
    // ── Keyset pagination (UI path only; ignored by the sync-engine path) ────
    // PageSize = 0 means "return everything" (sync engine, export, single-visit).
    // Default UI page is 25 rows.
    public int PageSize { get; init; } = 0;
    // Encoded cursor from the previous page's NextCursor. null = first page.
    public string? Cursor { get; init; }
}

public class InvoiceDto
{
    public Guid InvoiceId { get; set; }
    public string DisplayId { get; set; } = string.Empty;
    public int? TokenNumber { get; set; }
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
    public List<InvoiceExtraChargeDto> ExtraCharges { get; set; } = new();
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

public class InvoiceExtraChargeDto
{
    public string Reason { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class GetInvoicesQueryHandler : IRequestHandler<GetInvoicesQuery, PagedInvoiceResult>
{
    private readonly IApplicationDbContext _context;
    private readonly IInvoiceEnrichmentService _enrichmentService;

    public GetInvoicesQueryHandler(IApplicationDbContext context, IInvoiceEnrichmentService enrichmentService)
    {
        _context = context;
        _enrichmentService = enrichmentService;
    }

    public async Task<PagedInvoiceResult> Handle(GetInvoicesQuery request, CancellationToken cancellationToken)
    {
        try
        {
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new PagedInvoiceResult();
            }

            var query = _context.Invoices
                .AsNoTracking()
                .ApplyInvoiceFilters(request, _context.UserContext.HospitalId)
                .Include(i => i.Patient)
                    .ThenInclude(p => p.Referrer)
                .Include(i => i.Appointment)
                .AsQueryable();

            bool usePaging = request.PageSize > 0 && !request.IncludeDeleted && !request.UpdatedAfter.HasValue && !request.AppointmentId.HasValue;
            DateTime? cursorDate = null;
            Guid? cursorId = null;
            if (usePaging && !string.IsNullOrEmpty(request.Cursor))
            {
                try
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(request.Cursor));
                    var parts = decoded.Split('|');
                    if (parts.Length == 2)
                    {
                        cursorDate = new DateTime(long.Parse(parts[0]), DateTimeKind.Utc);
                        cursorId   = Guid.Parse(parts[1]);
                    }
                }
                catch { /* malformed cursor */ }
            }

            if (usePaging && cursorDate.HasValue && cursorId.HasValue)
            {
                query = query.Where(i =>
                    i.CreatedAt < cursorDate.Value ||
                    (i.CreatedAt == cursorDate.Value && i.Id < cursorId.Value));
            }

            int totalCount = 0;
            if (usePaging)
            {
                totalCount = await query.CountAsync(cancellationToken);
            }

            int takeCount = usePaging ? request.PageSize + 1 : (request.IncludeDeleted ? 100_000 : 200);

            var result = await query
                .OrderByDescending(i => i.CreatedAt)
                .ThenByDescending(i => i.Id)
                .Select(i => new InvoiceDto
                {
                    InvoiceId = i.Id,
                    DisplayId = i.InvoiceId,
                    TokenNumber = i.Appointment != null ? i.Appointment.DailyTokenNumber : null,
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
                        Modality = it.AppointmentServiceId.HasValue
                            ? _context.AppointmentServices
                                .Where(s => s.Id == it.AppointmentServiceId.Value)
                                .Select(s => s.Modality)
                                .FirstOrDefault()
                            : (i.Appointment != null ? i.Appointment.Modality : null)
                    }).ToList(),
                    ExtraCharges = i.ExtraCharges.Select(ec => new InvoiceExtraChargeDto
                    {
                        Reason = ec.Reason,
                        Amount = ec.Amount
                    }).ToList(),
                    UpdatedAt = i.UpdatedAt,
                    DeletedAt = i.DeletedAt
                })
                .Take(takeCount)
                .ToListAsync(cancellationToken);

            // Delegate enrichment to domain service (SRP)
            await _enrichmentService.EnrichInvoicesAsync(result, cancellationToken);

            string? nextCursor = null;
            if (usePaging && result.Count > request.PageSize)
            {
                result.RemoveAt(result.Count - 1);
                var last = result.Last();
                var raw  = $"{last.CreatedAt.Ticks}|{last.InvoiceId}";
                nextCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
            }

            return new PagedInvoiceResult
            {
                Items      = result,
                NextCursor = nextCursor,
                TotalCount = usePaging ? totalCount : result.Count,
                IsPaged    = usePaging,
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve invoices: {ex.Message}", ex);
        }
    }
}
