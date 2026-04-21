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
        var invoices = await _context.Invoices.ToListAsync(cancellationToken);
        
        if (!invoices.Any()) return new FinancialMatrixDto();

        var daily = invoices
            .GroupBy(i => i.CreatedAt.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new MatrixItemDto
            {
                Label = g.Key.ToString("dd-MMM-yyyy"),
                Invoiced = g.Sum(i => i.TotalAmount),
                Collected = g.Sum(i => i.PaidAmount),
                Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                RealizationRate = g.Sum(i => i.TotalAmount) > 0 
                    ? (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100) 
                    : 0
            }).Take(30).ToList(); // Last 30 days

        var monthly = invoices
            .GroupBy(i => new { i.CreatedAt.Year, i.CreatedAt.Month })
            .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
            .Select(g => new MatrixItemDto
            {
                Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM yyyy"),
                Invoiced = g.Sum(i => i.TotalAmount),
                Collected = g.Sum(i => i.PaidAmount),
                Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                RealizationRate = g.Sum(i => i.TotalAmount) > 0 
                    ? (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100) 
                    : 0
            }).Take(12).ToList(); // Last 12 months

        var yearly = invoices
            .GroupBy(i => i.CreatedAt.Year)
            .OrderByDescending(g => g.Key)
            .Select(g => new MatrixItemDto
            {
                Label = g.Key.ToString(),
                Invoiced = g.Sum(i => i.TotalAmount),
                Collected = g.Sum(i => i.PaidAmount),
                Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                RealizationRate = g.Sum(i => i.TotalAmount) > 0 
                    ? (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100) 
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
