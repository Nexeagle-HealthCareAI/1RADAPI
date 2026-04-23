using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Queries.GetExpenses;

public record GetExpensesQuery : IRequest<List<ExpenseDto>>
{
    public string? Category { get; init; }
    public string? Search { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
}

public class ExpenseDto
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal TaxAmount { get; set; }
    public string? PaymentMode { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? VendorName { get; set; }
    public string? CostCenter { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class GetExpensesQueryHandler : IRequestHandler<GetExpensesQuery, List<ExpenseDto>>
{
    private readonly IApplicationDbContext _context;

    public GetExpensesQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ExpenseDto>> Handle(GetExpensesQuery request, CancellationToken cancellationToken)
    {
        try
        {
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new List<ExpenseDto>();
            }

            var query = _context.Expenses
                .AsNoTracking()
                .Where(e => e.HospitalId == _context.UserContext.HospitalId)
                .AsQueryable();

            // Category Filtering
            if (!string.IsNullOrEmpty(request.Category) && request.Category != "ALL")
            {
                query = query.Where(e => e.Category == request.Category);
            }

            // Search Filtering
            if (!string.IsNullOrEmpty(request.Search))
            {
                query = query.Where(e => e.Description.Contains(request.Search) || 
                                         e.VendorName.Contains(request.Search) || 
                                         e.ReferenceNumber.Contains(request.Search));
            }

            // Temporal Filtering
            if (request.StartDate.HasValue)
            {
                query = query.Where(e => e.TransactionDate >= request.StartDate.Value);
            }

            if (request.EndDate.HasValue)
            {
                query = query.Where(e => e.TransactionDate <= request.EndDate.Value);
            }

            return await query
                .OrderByDescending(e => e.TransactionDate)
                .Select(e => new ExpenseDto
                {
                    Id = e.Id,
                    Description = e.Description,
                    Category = e.Category,
                    Amount = e.Amount,
                    TaxAmount = e.TaxAmount,
                    PaymentMode = e.PaymentMode,
                    ReferenceNumber = e.ReferenceNumber,
                    VendorName = e.VendorName,
                    CostCenter = e.CostCenter,
                    Status = e.Status,
                    TransactionDate = e.TransactionDate,
                    CreatedAt = e.CreatedAt
                })
                .Take(200)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve expenses: {ex.Message}", ex);
        }
    }
}
