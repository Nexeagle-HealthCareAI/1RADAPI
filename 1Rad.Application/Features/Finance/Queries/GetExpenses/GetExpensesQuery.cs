using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Queries.GetExpenses;

// ── Pagination DTO ─────────────────────────────────────────────────────────
public class PagedExpenseResult
{
    public List<ExpenseDto> Items { get; set; } = new();
    public string? NextCursor { get; set; }
    public int TotalCount { get; set; }
    public bool IsPaged { get; set; }
}

public record GetExpensesQuery : IRequest<PagedExpenseResult>
{
    public string? Category { get; init; }
    public string? Search { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public DateTime? UpdatedAfter { get; init; }
    public bool IncludeDeleted { get; init; }
    // ── Keyset pagination ──────────────────────────────────────────
    // PageSize = 0 means “return everything” (sync engine path).
    public int PageSize { get; init; } = 0;
    public string? Cursor { get; init; }
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
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class GetExpensesQueryHandler : IRequestHandler<GetExpensesQuery, PagedExpenseResult>
{
    private readonly IApplicationDbContext _context;

    public GetExpensesQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<PagedExpenseResult> Handle(GetExpensesQuery request, CancellationToken cancellationToken)
    {
        try
        {
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new PagedExpenseResult();
            }

            var query = _context.Expenses
                .AsNoTracking()
                .Where(e => e.HospitalId == _context.UserContext.HospitalId)
                .AsQueryable();

            if (!request.IncludeDeleted)
            {
                query = query.Where(e => e.DeletedAt == null);
            }
            if (request.UpdatedAfter.HasValue)
            {
                var since = request.UpdatedAfter.Value;
                query = query.Where(e => e.UpdatedAt > since);
            }

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

            // ── Keyset cursor decode ──────────────────────────────────────
            bool usePaging = request.PageSize > 0 && !request.IncludeDeleted && !request.UpdatedAfter.HasValue;
            DateTime? cursorDate = null;
            Guid? cursorId = null;
            if (usePaging && !string.IsNullOrEmpty(request.Cursor))
            {
                try
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(request.Cursor));
                    var parts = decoded.Split('|');
                    if (parts.Length == 2)
                    {
                        cursorDate = new DateTime(long.Parse(parts[0]), DateTimeKind.Utc);
                        cursorId   = Guid.Parse(parts[1]);
                    }
                }
                catch { /* malformed cursor — treat as first page */ }
            }

            if (usePaging && cursorDate.HasValue && cursorId.HasValue)
            {
                query = query.Where(e =>
                    e.TransactionDate < cursorDate.Value ||
                    (e.TransactionDate == cursorDate.Value && e.Id < cursorId.Value));
            }

            int totalCount = 0;
            if (usePaging)
            {
                totalCount = await query.CountAsync(cancellationToken);
            }

            int takeCount = usePaging ? request.PageSize + 1
                          : (request.IncludeDeleted ? 100_000 : 500);

            var result = await query
                .OrderByDescending(e => e.TransactionDate)
                .ThenByDescending(e => e.Id)
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
                    CreatedAt = e.CreatedAt,
                    UpdatedAt = e.UpdatedAt,
                    DeletedAt = e.DeletedAt
                })
                .Take(takeCount)
                .ToListAsync(cancellationToken);

            string? nextCursor = null;
            if (usePaging && result.Count > request.PageSize)
            {
                result.RemoveAt(result.Count - 1);
                var last = result.Last();
                var raw  = $"{last.TransactionDate.Ticks}|{last.Id}";
                nextCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
            }

            return new PagedExpenseResult
            {
                Items      = result,
                NextCursor = nextCursor,
                TotalCount = usePaging ? totalCount : result.Count,
                IsPaged    = usePaging,
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve expenses: {ex.Message}", ex);
        }
    }
}
