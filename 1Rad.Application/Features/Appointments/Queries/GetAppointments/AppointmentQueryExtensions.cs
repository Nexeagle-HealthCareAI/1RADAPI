using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Appointments.Queries.GetAppointments;

public static class AppointmentQueryExtensions
{
    public static IQueryable<Appointment> ApplyWorklistFilters(
        this IQueryable<Appointment> query,
        GetAppointmentsQuery request,
        Guid hospitalId)
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
            query = query.Where(a => a.UpdatedAt > since);
        }

        if (request.StartDate.HasValue)
        {
            query = query.Where(a => a.DateTime >= request.StartDate.Value);
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
