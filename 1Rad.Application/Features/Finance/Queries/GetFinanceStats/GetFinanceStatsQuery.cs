using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Queries.GetFinanceStats;

public record GetFinanceStatsQuery : IRequest<FinanceStatsDto>;

public class FinanceStatsDto
{
    public decimal TotalRevenue { get; set; }
    public decimal PendingRevenue { get; set; }
    public int PendingCount { get; set; }
    public int RealizationRate { get; set; }
    public decimal AverageTicket { get; set; }
}

public class GetFinanceStatsQueryHandler : IRequestHandler<GetFinanceStatsQuery, FinanceStatsDto>
{
    private readonly IApplicationDbContext _context;

    public GetFinanceStatsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<FinanceStatsDto> Handle(GetFinanceStatsQuery request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;

        // Optimization: Use Selective Projection to fetch only necessary numeric/status fields.
        // This prevents loading heavy navigation properties and full objects into memory.
        var invoiceData = await _context.Invoices
            .AsNoTracking()
            .Where(i => i.HospitalId == hospitalId)
            .Select(i => new { i.Status, i.TotalAmount, i.PaidAmount })
            .ToListAsync(cancellationToken);
        
        if (!invoiceData.Any()) return new FinanceStatsDto();

        var paidInvoices = invoiceData.Where(i => i.Status == "PAID").ToList();
        var pendingInvoices = invoiceData.Where(i => i.Status != "PAID" && i.Status != "CANCELLED").ToList();

        var totalRev = invoiceData.Sum(i => i.PaidAmount);
        var pendingRev = pendingInvoices.Sum(i => i.TotalAmount - i.PaidAmount);
        
        return new FinanceStatsDto
        {
            TotalRevenue = totalRev,
            PendingRevenue = pendingRev,
            PendingCount = pendingInvoices.Count,
            RealizationRate = (int)((decimal)paidInvoices.Count / invoiceData.Count * 100),
            AverageTicket = paidInvoices.Any() ? paidInvoices.Average(i => i.TotalAmount) : 0
        };
    }
}
