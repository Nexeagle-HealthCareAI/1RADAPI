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

        if (request.StartDate.HasValue)
        {
            query = query.Where(i => i.CreatedAt >= request.StartDate.Value);
        }

        if (request.EndDate.HasValue)
        {
            query = query.Where(i => i.CreatedAt <= request.EndDate.Value);
        }

        if (request.AppointmentId.HasValue)
        {
            query = query.Where(i => i.AppointmentId == request.AppointmentId.Value);
        }

        return query;
    }
}
