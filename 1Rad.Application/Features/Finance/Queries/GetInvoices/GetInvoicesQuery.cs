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
}

public class InvoiceDto
{
    public Guid InvoiceId { get; set; }
    public string DisplayId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string? PatientIdentifier { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal DiscountAmount { get; set; }
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
}

public class InvoiceItemDto
{
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int Quantity { get; set; }
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

            // Execute Projection: Using standard property initialization to ensure safe SQL translation in EF Core.
            return await query
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new InvoiceDto
                {
                    InvoiceId = i.Id,
                    DisplayId = i.InvoiceId,
                    PatientName = i.Patient.FullName,
                    PatientIdentifier = i.Patient.PatientIdentifier,
                    GrossAmount = i.GrossAmount,
                    DiscountAmount = i.DiscountAmount,
                    TotalAmount = i.TotalAmount,
                    PaidAmount = i.PaidAmount,
                    BalanceAmount = i.TotalAmount - i.PaidAmount,
                    Status = i.Status,
                    CreatedAt = i.CreatedAt,
                    ReferrerName = i.Appointment != null ? i.Appointment.ReferredBy : (i.Patient.Referrer != null ? i.Patient.Referrer.Name : null),
                    ReferrerId = i.Patient.ReferrerId ?? (i.Appointment != null ? _context.Referrers.Where(r => r.Name == i.Appointment.ReferredBy && r.HospitalId == i.HospitalId).Select(r => (Guid?)r.ReferrerId).FirstOrDefault() : null),
                    Modality = i.Appointment != null ? i.Appointment.Modality : null,
                    CommissionAmount = (_context.ReferralCommissions.Where(c => ((i.AppointmentId != null && c.AppointmentId == i.AppointmentId) || (i.InvoiceId != null && c.ReferenceNumber == i.InvoiceId)) && c.HospitalId == i.HospitalId).Sum(c => (decimal?)c.CommissionAmount) ?? 0),
                    CommissionId = _context.ReferralCommissions
                        .Where(c => ((i.AppointmentId != null && c.AppointmentId == i.AppointmentId) || (i.InvoiceId != null && c.ReferenceNumber == i.InvoiceId)) && c.HospitalId == i.HospitalId)
                        .Select(c => (Guid?)c.Id)
                        .FirstOrDefault(),
                    AppointmentStatus = i.Appointment != null ? i.Appointment.Status : null,
                    AppointmentId = i.AppointmentId,

                    Items = i.Items.Select(it => new InvoiceItemDto
                    {
                        Description = it.Description,
                        Amount = it.Amount,
                        Quantity = it.Quantity
                    }).ToList()
                })
                .Take(200) 
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve invoices: {ex.Message}", ex);
        }
    }
}
