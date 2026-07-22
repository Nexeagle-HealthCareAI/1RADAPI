using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Finance.Queries.GetExpenses;

public static class ExpenseQueryExtensions
{
    public static IQueryable<Expense> ApplyExpenseFilters(
        this IQueryable<Expense> query,
        GetExpensesQuery request,
        Guid hospitalId)
    {
        query = query.Where(e => e.HospitalId == hospitalId);

        if (!request.IncludeDeleted)
        {
            query = query.Where(e => e.DeletedAt == null);
        }

        if (request.UpdatedAfter.HasValue)
        {
            var since = request.UpdatedAfter.Value;
            query = query.Where(e => e.UpdatedAt > since);
        }

        if (!string.IsNullOrEmpty(request.Category) && request.Category != "ALL")
        {
            query = query.Where(e => e.Category == request.Category);
        }

        if (!string.IsNullOrEmpty(request.Search))
        {
            var search = request.Search.ToLower().Trim();
            // Checking for null on nullable string properties to avoid warnings
            query = query.Where(e => 
                (e.Description != null && e.Description.ToLower().Contains(search)) || 
                (e.VendorName != null && e.VendorName.ToLower().Contains(search)) || 
                (e.ReferenceNumber != null && e.ReferenceNumber.ToLower().Contains(search)));
        }

        if (request.StartDate.HasValue)
        {
            query = query.Where(e => e.TransactionDate >= request.StartDate.Value);
        }

        if (request.EndDate.HasValue)
        {
            query = query.Where(e => e.TransactionDate <= request.EndDate.Value);
        }

        return query;
    }
}
