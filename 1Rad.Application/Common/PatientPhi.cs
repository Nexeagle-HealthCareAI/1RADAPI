using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// Collects everything that identifies the patient of a visit or a PACS study,
/// for <see cref="PhiRedactor"/>. One place, so every AI feature masks the same
/// things (name and its parts, PTID, visit number, phones, address, referrer
/// contact; for a study: DICOM name, patient id and accession number).
/// </summary>
public static class PatientPhi
{
    public static async Task<List<string?>> CollectAsync(
        IApplicationDbContext db, Guid? appointmentId, Guid? imagingStudyId, CancellationToken ct)
    {
        var phi = new List<string?>();
        if (imagingStudyId is Guid sid && sid != Guid.Empty)
        {
            var s = await db.ImagingStudies
                .Where(x => x.Id == sid)
                .Select(x => new { x.PatientName, x.DicomPatientId, x.AccessionNumber, x.AppointmentId })
                .FirstOrDefaultAsync(ct);
            if (s != null)
            {
                // DICOM names are "FAMILY^GIVEN" — split on ^ as well.
                phi.AddRange(PhiRedactor.WithNameParts(s.PatientName?.Replace('^', ' '), s.PatientName, s.DicomPatientId, s.AccessionNumber));
                if (appointmentId is not Guid && s.AppointmentId is Guid linked) appointmentId = linked;
            }
        }
        if (appointmentId is Guid aid && aid != Guid.Empty)
        {
            var a = await db.Appointments
                .Where(x => x.AppointmentId == aid)
                .Select(x => new
                {
                    x.PatientName, x.DisplayId, x.Mobile, x.ReferredContact,
                    FullName = x.Patient.FullName, Ptid = x.Patient.PatientIdentifier,
                    PatientMobile = x.Patient.Mobile, x.Patient.Address, x.Patient.Village,
                })
                .FirstOrDefaultAsync(ct);
            if (a != null)
            {
                phi.AddRange(PhiRedactor.WithNameParts(a.PatientName, a.DisplayId, a.Mobile, a.ReferredContact, a.Ptid, a.PatientMobile, a.Address));
                if (!string.Equals(a.FullName, a.PatientName, StringComparison.OrdinalIgnoreCase))
                    phi.AddRange(PhiRedactor.WithNameParts(a.FullName));
                // A village name is only masked when it is clearly a name, not a short common word.
                if (!string.IsNullOrWhiteSpace(a.Village) && a.Village!.Trim().Length >= 5) phi.Add(a.Village);
            }
        }
        return phi;
    }
}
