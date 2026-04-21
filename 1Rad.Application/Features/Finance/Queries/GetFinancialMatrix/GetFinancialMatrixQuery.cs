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
}

public class MatrixItemDto
{
    public string Label { get; set; } = string.Empty;
    public decimal Invoiced { get; set; }
    public decimal Collected { get; set; }
    public decimal Pending { get; set; }
    public int RealizationRate { get; set; }
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
        // Optimization: Use Selective Projection to minimize memory footprint and AsNoTracking for read-only performance.
        var invoiceData = await _context.Invoices
            .AsNoTracking()
            .Where(i => i.HospitalId == _context.UserContext.HospitalId)
            .Select(i => new { i.TotalAmount, i.PaidAmount, i.CreatedAt })
            .ToListAsync(cancellationToken);
        
        if (!invoiceData.Any()) return new FinancialMatrixDto();

        var daily = invoiceData
            .GroupBy(i => i.CreatedAt.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new MatrixItemDto
            {
                Label = g.Key.ToString("dd-MMM-yyyy"),
                Invoiced = g.Sum(i => i.TotalAmount),
                Collected = g.Sum(i => i.PaidAmount),
                Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                RealizationRate = g.Sum(i => i.TotalAmount) > 0 
                    ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
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
                Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                RealizationRate = g.Sum(i => i.TotalAmount) > 0 
                    ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
                    : 0
            }).ToList();

        return new FinancialMatrixDto
        {
            Daily = daily,
            Monthly = monthly,
            Yearly = yearly
        };
    }
}
