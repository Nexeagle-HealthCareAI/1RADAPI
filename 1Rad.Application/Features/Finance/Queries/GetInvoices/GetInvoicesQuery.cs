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
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<InvoiceItemDto> Items { get; set; } = new();
}

public record InvoiceItemDto(string Description, decimal Amount, int Quantity);

public class GetInvoicesQueryHandler : IRequestHandler<GetInvoicesQuery, List<InvoiceDto>>
{
    private readonly IApplicationDbContext _context;

    public GetInvoicesQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<InvoiceDto>> Handle(GetInvoicesQuery request, CancellationToken cancellationToken)
    {
        var query = _context.Invoices
            .Where(i => i.HospitalId == _context.UserContext.HospitalId)
            .Include(i => i.Items)
            .AsQueryable();

        if (!string.IsNullOrEmpty(request.Status) && request.Status != "ALL")
        {
            query = query.Where(i => i.Status == request.Status);
        }

        if (!string.IsNullOrEmpty(request.Search))
        {
            query = query.Where(i => i.PatientName.Contains(request.Search) || i.DisplayId.Contains(request.Search));
        }

        if (request.StartDate.HasValue)
        {
            query = query.Where(i => i.CreatedAt >= request.StartDate.Value);
        }

        if (request.EndDate.HasValue)
        {
            query = query.Where(i => i.CreatedAt <= request.EndDate.Value);
        }

        return await query
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvoiceDto
            {
                InvoiceId = i.InvoiceId,
                DisplayId = i.DisplayId,
                PatientName = i.PatientName,
                TotalAmount = i.TotalAmount,
                PaidAmount = i.PaidAmount,
                BalanceAmount = i.BalanceAmount,
                Status = i.Status,
                CreatedAt = i.CreatedAt,
                Items = i.Items.Select(it => new InvoiceItemDto(it.Description, it.Amount, it.Quantity)).ToList()
            })
            .ToListAsync(cancellationToken);
    }
}
