using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix;

public record GetFinancialMatrixQuery : IRequest<FinancialMatrixDto>
{
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
}

public class FinancialMatrixDto
{
    public List<MatrixItemDto> Daily { get; set; } = new();
    public List<MatrixItemDto> Weekly { get; set; } = new();
    public List<MatrixItemDto> Monthly { get; set; } = new();
    public List<MatrixItemDto> Yearly { get; set; } = new();
    public List<ModalityRevenueDto> ModalityBreakdown { get; set; } = new();
    public ClinicPerformanceDto Performance { get; set; } = new();
    public List<ModalityProfitabilityDto> ModalityProfitability { get; set; } = new();
    public ReferralContributionDto ReferralContribution { get; set; } = new();
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
    public decimal RangeRevenue { get; set; }
    public int ContributionPercentage { get; set; }
}

public class ClinicPerformanceDto
{
    public decimal GrossRevenue { get; set; }
    public decimal CashCollected { get; set; }
    public decimal ConcessionLeakage { get; set; }
    public double LeakagePercentage { get; set; }
    public decimal OutstandingAR { get; set; }
    public double ExpenseRatio { get; set; }
    public decimal AverageRevenuePerScan { get; set; }
    public int TotalScansCount { get; set; }
}

public class ModalityProfitabilityDto
{
    public string Modality { get; set; } = string.Empty;
    public int ScanCount { get; set; }
    public decimal GrossRevenue { get; set; }
    public decimal ReferralCut { get; set; }
    public decimal NetRevenue { get; set; }
    public double MarginPercentage { get; set; }
}

public class ReferralContributionDto
{
    public decimal ReferredRevenue { get; set; }
    public decimal DirectRevenue { get; set; }
    public double ReferralRatio { get; set; }
    public int ReferredScansCount { get; set; }
    public int DirectScansCount { get; set; }
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
            var invoiceQuery = _context.Invoices.AsNoTracking().Where(i => i.HospitalId == hospitalId);
            var expenseQuery = _context.Expenses.AsNoTracking().Where(e => e.HospitalId == hospitalId);

            if (request.StartDate.HasValue)
            {
                invoiceQuery = invoiceQuery.Where(i => i.CreatedAt >= request.StartDate.Value);
                expenseQuery = expenseQuery.Where(e => e.TransactionDate >= request.StartDate.Value);
            }
            if (request.EndDate.HasValue)
            {
                var end = request.EndDate.Value.Date.AddDays(1).AddTicks(-1);
                invoiceQuery = invoiceQuery.Where(i => i.CreatedAt <= end);
                expenseQuery = expenseQuery.Where(e => e.TransactionDate <= end);
            }

            var invoiceData = await invoiceQuery
                .GroupJoin(_context.Appointments.AsNoTracking(), 
                           i => i.AppointmentId, 
                           a => a.AppointmentId, 
                           (i, appointments) => new { i, appointments })
                .SelectMany(x => x.appointments.DefaultIfEmpty(),
                            (x, a) => new 
                            { 
                                x.i.GrossAmount,
                                x.i.DiscountAmount,
                                x.i.TotalAmount, 
                                x.i.PaidAmount, 
                                x.i.CreatedAt, 
                                x.i.ReferralCutValue,
                                Modality = a != null ? a.Modality : "GENERAL",
                                HasReferrer = a != null && a.ReferrerId != null
                            })
                .ToListAsync(cancellationToken);
            
            var expenseData = await expenseQuery
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
            
            var weekly = invoiceData
                .GroupBy(i => System.Globalization.ISOWeek.GetWeekOfYear(i.CreatedAt))
                .OrderByDescending(g => g.Key)
                .Select(g => new MatrixItemDto
                {
                    Label = $"Week {g.Key}",
                    Invoiced = g.Sum(i => i.TotalAmount),
                    Collected = g.Sum(i => i.PaidAmount),
                    Expenses = expenseData.Where(e => System.Globalization.ISOWeek.GetWeekOfYear(e.TransactionDate) == g.Key).Sum(e => e.Amount),
                    Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                    RealizationRate = g.Sum(i => i.TotalAmount) > 0 
                        ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
                        : 0
                }).Take(8).ToList();

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

            var modalityBreakdown = invoiceData
                .GroupBy(i => i.Modality)
                .Select(g => new ModalityRevenueDto
                {
                    Modality = (g.Key ?? "GENERAL").ToUpper(),
                    RangeRevenue = g.Sum(i => i.TotalAmount),
                    ContributionPercentage = totalLifeTimeInvoiced > 0 
                        ? (int)(g.Sum(i => i.TotalAmount) / totalLifeTimeInvoiced * 100)
                        : 0
                })
                .OrderByDescending(x => x.RangeRevenue)
                .ToList();

            // ADVANCED CLINICAL PERFORMANCE METRICS
            var totalGross = invoiceData.Sum(i => i.GrossAmount);
            var totalPaid = invoiceData.Sum(i => i.PaidAmount);
            var totalDiscount = invoiceData.Sum(i => i.DiscountAmount);
            var totalExpenses = expenseData.Sum(e => e.Amount);
            
            var performance = new ClinicPerformanceDto
            {
                GrossRevenue = totalGross,
                CashCollected = totalPaid,
                ConcessionLeakage = totalDiscount,
                LeakagePercentage = totalGross > 0 ? (double)Math.Round((totalDiscount / totalGross) * 100, 1) : 0,
                OutstandingAR = invoiceData.Sum(i => i.TotalAmount - i.PaidAmount),
                ExpenseRatio = totalPaid > 0 ? (double)Math.Round((totalExpenses / totalPaid) * 100, 1) : 0,
                AverageRevenuePerScan = invoiceData.Any() ? Math.Round(invoiceData.Sum(i => i.TotalAmount) / invoiceData.Count, 2) : 0,
                TotalScansCount = invoiceData.Count
            };

            // ADVANCED MODALITY PROFITABILITY MATRIX (Modality revenue minus partner cuts)
            var modalityProfitability = invoiceData
                .GroupBy(i => i.Modality)
                .Select(g => 
                {
                    var gross = g.Sum(x => x.GrossAmount);
                    var cut = g.Sum(x => x.ReferralCutValue);
                    var net = g.Sum(x => x.TotalAmount) - cut;
                    return new ModalityProfitabilityDto
                    {
                        Modality = (g.Key ?? "GENERAL").ToUpper(),
                        ScanCount = g.Count(),
                        GrossRevenue = gross,
                        ReferralCut = cut,
                        NetRevenue = net,
                        MarginPercentage = gross > 0 ? (double)Math.Round((net / gross) * 100, 1) : 0
                    };
                })
                .OrderByDescending(m => m.GrossRevenue)
                .ToList();

            // ADVANCED REFERRAL CONTRIBUTION ANALYSIS
            var referredInvoices = invoiceData.Where(i => i.HasReferrer).ToList();
            var directInvoices = invoiceData.Where(i => !i.HasReferrer).ToList();
            var totalNetBilled = invoiceData.Sum(i => i.TotalAmount);
            
            var referralContribution = new ReferralContributionDto
            {
                ReferredRevenue = referredInvoices.Sum(i => i.TotalAmount),
                DirectRevenue = directInvoices.Sum(i => i.TotalAmount),
                ReferralRatio = totalNetBilled > 0 ? (double)Math.Round((referredInvoices.Sum(i => i.TotalAmount) / totalNetBilled) * 100, 1) : 0,
                ReferredScansCount = referredInvoices.Count,
                DirectScansCount = directInvoices.Count
            };

            return new FinancialMatrixDto
            {
                Daily = daily,
                Weekly = weekly,
                Monthly = monthly,
                Yearly = yearly,
                ModalityBreakdown = modalityBreakdown,
                Performance = performance,
                ModalityProfitability = modalityProfitability,
                ReferralContribution = referralContribution
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve financial matrix: {ex.Message}", ex);
        }
    }
}
