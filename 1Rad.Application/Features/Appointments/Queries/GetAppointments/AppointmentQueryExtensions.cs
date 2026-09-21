using _1Rad.Application.Common;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Appointments.Queries.GetAppointments;

public static class AppointmentQueryExtensions
{
    public static IQueryable<Appointment> ApplyWorklistFilters(
        this IQueryable<Appointment> query,
        GetAppointmentsQuery request,
        Guid hospitalId,
        IQueryable<Guid>? serviceChangedAppointmentIds = null)
    {
        query = query.Where(a => a.HospitalId == hospitalId);

        if (!string.IsNullOrEmpty(request.Status) && request.Status != "ALL")
        {
            query = query.Where(a => a.Status == request.Status);
        }

        if (!request.IncludeDeleted)
        {
            query = query.Where(a => a.DeletedAt == null);
        }

        if (request.UpdatedAfter.HasValue)
        {
            var since = request.UpdatedAfter.Value;
            // A per-service edit (technician notes, a service's scan status)
            // only bumps that AppointmentService row's own UpdatedAt, not the
            // parent visit's — so a client polling for "what changed" by the
            // parent's UpdatedAt alone would never see it. The handler passes
            // in the visit ids whose service lines changed since `since`.
            query = serviceChangedAppointmentIds != null
                ? query.Where(a => a.UpdatedAt > since || serviceChangedAppointmentIds.Contains(a.AppointmentId))
                : query.Where(a => a.UpdatedAt > since);
        }

        if (request.StartDate.HasValue)
        {
            // A bare "YYYY-MM-DD" (see IstDateRange) — not to be confused with
            // UpdatedAfter above, which is already a precise UTC instant.
            query = query.Where(a => a.DateTime >= IstDateRange.ToUtcStart(request.StartDate.Value));
        }

        if (request.EndDate.HasValue)
        {
            // Inclusive of the whole IST day named by EndDate.
            query = query.Where(a => a.DateTime <= IstDateRange.ToUtcEndInclusive(request.EndDate.Value));
        }

        if (request.ActiveSince.HasValue)
        {
            // "Everything still in play, plus anything recent" — the clinical
            // worklists (Doctor/Technician boards) need visits from the last
            // N days regardless of status AND any older visit that hasn't
            // reached a finalized status yet, so an old STAT study nobody
            // completed can't silently fall off the board.
            var recentFrom = IstDateRange.ToUtcStart(request.ActiveSince.Value);
            query = query.Where(a =>
                a.DateTime >= recentFrom
                || a.Status == null
                || (a.Status.ToUpper() != "CANCELLED" && a.Status.ToUpper() != "DELIVERED"));
        }

        if (!string.IsNullOrEmpty(request.Modality) && request.Modality != "ALL")
        {
            query = query.Where(a => a.Modality == request.Modality);
        }

        if (!string.IsNullOrEmpty(request.Doctor) && request.Doctor != "ALL")
        {
            query = query.Where(a => a.Doctor == request.Doctor);
        }

        if (!string.IsNullOrEmpty(request.SearchQuery))
        {
            var search = request.SearchQuery.ToLower().Trim();
            
            if (Guid.TryParse(search, out Guid parsedGuid))
            {
                query = query.Where(a => a.PatientId == parsedGuid || a.AppointmentId == parsedGuid);
            }
            else
            {
                query = query.Where(a => 
                    (a.Patient != null && a.Patient.FullName != null && a.Patient.FullName.ToLower().Contains(search)) || 
                    (a.Mobile != null && a.Mobile.Contains(search)) || 
                    (a.DisplayId != null && a.DisplayId.ToLower().Contains(search)) ||
                    (a.Patient != null && a.Patient.PatientIdentifier != null && a.Patient.PatientIdentifier.ToLower().Contains(search)));
            }
        }

        return query;
    }
}
