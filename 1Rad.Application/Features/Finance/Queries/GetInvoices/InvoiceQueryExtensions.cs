using _1Rad.Application.Common;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Finance.Queries.GetInvoices;

public static class InvoiceQueryExtensions
{
    public static IQueryable<Invoice> ApplyInvoiceFilters(
        this IQueryable<Invoice> query,
        GetInvoicesQuery request,
        Guid hospitalId)
    {
        query = query.Where(i => i.HospitalId == hospitalId);

        if (!request.IncludeDeleted)
        {
            query = query.Where(i => i.DeletedAt == null);
        }

        if (request.UpdatedAfter.HasValue)
        {
            var since = request.UpdatedAfter.Value;
            query = query.Where(i => i.UpdatedAt > since);
        }

        if (!string.IsNullOrEmpty(request.Status) && request.Status != "ALL")
        {
            query = query.Where(i => i.Status == request.Status);
        }

        if (!string.IsNullOrEmpty(request.Search))
        {
            var search = request.Search.ToLower().Trim();
            query = query.Where(i => 
                (i.Patient != null && i.Patient.FullName != null && i.Patient.FullName.ToLower().Contains(search)) || 
                (i.InvoiceId != null && i.InvoiceId.ToLower().Contains(search)));
        }

        // StartDate/EndDate arrive as a bare "YYYY-MM-DD" (date picker) — a
        // DateTimeKind.Unspecified midnight with no timezone info. CreatedAt is
        // a real UTC instant, so comparing them directly silently treats
        // "Sept 16" as midnight UTC (5:30am IST) instead of midnight IST,
        // shifting the day boundary by 5.5 hours. See IstDateRange.
        if (request.StartDate.HasValue)
        {
            query = query.Where(i => i.CreatedAt >= IstDateRange.ToUtcStart(request.StartDate.Value));
        }

        if (request.EndDate.HasValue)
        {
            query = query.Where(i => i.CreatedAt <= IstDateRange.ToUtcEndInclusive(request.EndDate.Value));
        }

        if (request.AppointmentId.HasValue)
        {
            query = query.Where(i => i.AppointmentId == request.AppointmentId.Value);
        }

        return query;
    }
}
