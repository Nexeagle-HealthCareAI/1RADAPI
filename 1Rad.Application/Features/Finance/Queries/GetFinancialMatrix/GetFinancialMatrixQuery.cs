using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix;

public record GetFinancialMatrixQuery : IRequest<FinancialMatrixDto>;

public class FinancialMatrixDto
{
    public List<MatrixItemDto> Daily { get; set; } = new();
    public List<MatrixItemDto> Monthly { get; set; } = new();
    public List<MatrixItemDto> Yearly { get; set; } = new();
    public List<ModalityRevenueDto> ModalityBreakdown { get; set; } = new();
}

public class MatrixItemDto
{
    public string Label { get; set; } = string.Empty;
    public decimal Invoiced { get; set; }
    public decimal Collected { get; set; }
    public decimal Pending { get; set; }
    public decimal Expenses { get; set; }
    public decimal NetProfit => Invoiced - Expenses;
    public int RealizationRate { get; set; }
}

public class ModalityRevenueDto
{
    public string Modality { get; set; } = string.Empty;
    public decimal DailyRevenue { get; set; }
    public decimal MonthlyRevenue { get; set; }
    public decimal YearlyRevenue { get; set; }
    public int ContributionPercentage { get; set; }
}

public class GetFinancialMatrixQueryHandler : IRequestHandler<GetFinancialMatrixQuery, FinancialMatrixDto>
{
    private readonly IApplicationDbContext _context;

    public GetFinancialMatrixQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<FinancialMatrixDto> Handle(GetFinancialMatrixQuery request, CancellationToken cancellationToken)
    {
        try
        {
            // Validate hospital context
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new FinancialMatrixDto(); // Return empty matrix for invalid context
            }

            var hospitalId = _context.UserContext.HospitalId;

            // Perform a domain join to link Invoices with Appointments for Modality context
            var invoiceData = await _context.Invoices
                .AsNoTracking()
                .Where(i => i.HospitalId == hospitalId)
                .Join(_context.Appointments.AsNoTracking(), 
                      i => i.AppointmentId, 
                      a => a.AppointmentId, 
                      (i, a) => new { i.TotalAmount, i.PaidAmount, i.CreatedAt, a.Modality })
                .ToListAsync(cancellationToken);
            
            var expenseData = await _context.Expenses
                .AsNoTracking()
                .Where(e => e.HospitalId == hospitalId)
                .Select(e => new { e.Amount, e.TransactionDate })
                .ToListAsync(cancellationToken);
            
            if (!invoiceData.Any() && !expenseData.Any()) return new FinancialMatrixDto();

            var totalLifeTimeInvoiced = invoiceData.Sum(i => i.TotalAmount);

            // 1. Temporal Aggregation (Standard)
            var daily = invoiceData
                .GroupBy(i => i.CreatedAt.Date)
                .Select(g => new
                {
                    Date = g.Key,
                    Invoiced = g.Sum(i => i.TotalAmount),
                    Collected = g.Sum(i => i.PaidAmount)
                })
                .Concat(expenseData.GroupBy(e => e.TransactionDate.Date).Select(g => new { Date = g.Key, Invoiced = 0m, Collected = 0m })) // Ensure date coverage
                .GroupBy(x => x.Date)
                .OrderByDescending(g => g.Key)
                .Select(g => new MatrixItemDto
                {
                    Label = g.Key.ToString("dd-MMM-yyyy"),
                    Invoiced = g.Sum(x => x.Invoiced),
                    Collected = g.Sum(x => x.Collected),
                    Expenses = expenseData.Where(e => e.TransactionDate.Date == g.Key).Sum(e => e.Amount),
                    Pending = g.Sum(x => x.Invoiced - x.Collected),
                    RealizationRate = g.Sum(x => x.Invoiced) > 0 
                        ? Math.Min(100, (int)(g.Sum(x => x.Collected) / g.Sum(x => x.Invoiced) * 100))
                        : 0
                }).Take(30).ToList();

            var monthly = invoiceData
                .GroupBy(i => new { i.CreatedAt.Year, i.CreatedAt.Month })
                .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
                .Select(g => new MatrixItemDto
                {
                    Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM yyyy"),
                    Invoiced = g.Sum(i => i.TotalAmount),
                    Collected = g.Sum(i => i.PaidAmount),
                    Expenses = expenseData.Where(e => e.TransactionDate.Year == g.Key.Year && e.TransactionDate.Month == g.Key.Month).Sum(e => e.Amount),
                    Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                    RealizationRate = g.Sum(i => i.TotalAmount) > 0 
                        ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
                        : 0
                }).Take(12).ToList();

            var yearly = invoiceData
                .GroupBy(i => i.CreatedAt.Year)
                .OrderByDescending(g => g.Key)
                .Select(g => new MatrixItemDto
                {
                    Label = g.Key.ToString(),
                    Invoiced = g.Sum(i => i.TotalAmount),
                    Collected = g.Sum(i => i.PaidAmount),
                    Expenses = expenseData.Where(e => e.TransactionDate.Year == g.Key).Sum(e => e.Amount),
                    Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                    RealizationRate = g.Sum(i => i.TotalAmount) > 0 
                        ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
                        : 0
                }).ToList();

            // 2. Modality Cross-Pivot Aggregation
            var today = DateTime.UtcNow.Date;
            var monthStart = new DateTime(today.Year, today.Month, 1);
            var yearStart = new DateTime(today.Year, 1, 1);

            var modalityBreakdown = invoiceData
                .GroupBy(i => i.Modality)
                .Select(g => new ModalityRevenueDto
                {
                    Modality = (g.Key ?? "GENERIC").ToUpper(),
                    DailyRevenue = g.Where(i => i.CreatedAt.Date == today).Sum(i => i.TotalAmount),
                    MonthlyRevenue = g.Where(i => i.CreatedAt >= monthStart).Sum(i => i.TotalAmount),
                    YearlyRevenue = g.Where(i => i.CreatedAt >= yearStart).Sum(i => i.TotalAmount),
                    ContributionPercentage = totalLifeTimeInvoiced > 0 
                        ? (int)(g.Sum(i => i.TotalAmount) / totalLifeTimeInvoiced * 100)
                        : 0
                })
                .OrderByDescending(x => x.YearlyRevenue)
                .ToList();

            return new FinancialMatrixDto
            {
                Daily = daily,
                Monthly = monthly,
                Yearly = yearly,
                ModalityBreakdown = modalityBreakdown
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve financial matrix: {ex.Message}", ex);
        }
    }
}
