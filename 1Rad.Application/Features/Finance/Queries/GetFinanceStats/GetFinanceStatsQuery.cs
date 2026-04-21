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
        var allInvoices = await _context.Invoices.ToListAsync(cancellationToken);
        
        if (!allInvoices.Any()) return new FinanceStatsDto();

        var paidInvoices = allInvoices.Where(i => i.Status == "PAID").ToList();
        var pendingInvoices = allInvoices.Where(i => i.Status != "PAID" && i.Status != "CANCELLED").ToList();

        var totalRev = allInvoices.Sum(i => i.PaidAmount);
        var pendingRev = pendingInvoices.Sum(i => i.BalanceAmount);
        
        return new FinanceStatsDto
        {
            TotalRevenue = totalRev,
            PendingRevenue = pendingRev,
            PendingCount = pendingInvoices.Count,
            RealizationRate = (int)((decimal)paidInvoices.Count / allInvoices.Count * 100),
            AverageTicket = paidInvoices.Any() ? paidInvoices.Average(i => i.TotalAmount) : 0
        };
    }
}
