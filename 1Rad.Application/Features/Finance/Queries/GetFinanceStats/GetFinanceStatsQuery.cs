using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Queries.GetFinanceStats;

public record GetFinanceStatsQuery : IRequest<FinanceStatsDto>;

public class FinanceStatsDto
{
    public decimal TotalRevenue { get; set; }
    public decimal PendingRevenue { get; set; }
    public decimal TotalExpenses { get; set; }
    public decimal NetProfit { get; set; }
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
        try
        {
            // Validate hospital context
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new FinanceStatsDto(); // Return empty stats for invalid context
            }

            var hospitalId = _context.UserContext.HospitalId;

            var invoiceData = await _context.Invoices
                .AsNoTracking()
                .Where(i => i.HospitalId == hospitalId)
                .Select(i => new { i.Status, i.GrossAmount, i.TotalAmount, i.PaidAmount })
                .ToListAsync(cancellationToken);

            var expenseData = await _context.Expenses
                .AsNoTracking()
                .Where(e => e.HospitalId == hospitalId)
                .Select(e => new { e.Amount })
                .ToListAsync(cancellationToken);
            
            if (!invoiceData.Any() && !expenseData.Any()) return new FinanceStatsDto();

            var paidInvoices = invoiceData.Where(i => i.Status == "PAID").ToList();
            var pendingInvoices = invoiceData.Where(i => i.Status != "PAID" && i.Status != "CANCELLED").ToList();

            var totalRev = invoiceData.Sum(i => i.GrossAmount);
            var totalPaid = invoiceData.Sum(i => i.PaidAmount);
            var pendingRev = pendingInvoices.Sum(i => i.TotalAmount - i.PaidAmount);
            var totalExp = expenseData.Sum(e => e.Amount);
            
            return new FinanceStatsDto
            {
                TotalRevenue = totalRev,
                PendingRevenue = pendingRev,
                TotalExpenses = totalExp,
                NetProfit = totalPaid - totalExp,
                PendingCount = pendingInvoices.Count,
                RealizationRate = invoiceData.Any() ? (int)((decimal)paidInvoices.Count / invoiceData.Count * 100) : 0,
                AverageTicket = paidInvoices.Any() ? paidInvoices.Average(i => i.TotalAmount) : 0
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve finance statistics: {ex.Message}", ex);
        }
    }
}
