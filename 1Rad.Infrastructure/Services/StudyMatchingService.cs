using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Infrastructure.Services
{
    /// <summary>
    /// HMAC-free, deterministic reconciliation of a PACS-only study to a
    /// patient/appointment. Confidence order:
    ///   1. AccessionNumber → Appointment (the bridge sets accession to the
    ///      1Rad appointment id / DisplayId — strongest signal: it links the
    ///      visit AND, through it, the patient).
    ///   2. DicomPatientId → Patient.PatientIdentifier (MRN), exact + unique.
    ///   3. Normalised PatientName → Patient.NameNormalized, exact + UNIQUE only
    ///      (never auto-link an ambiguous namesake — those go to the inbox).
    /// Scopes every lookup to the study's own hospital and ignores the tenant
    /// query filter, since matching can run outside a user request (extraction).
    /// </summary>
    public class StudyMatchingService : IStudyMatchingService
    {
        private readonly IApplicationDbContext _context;

        public StudyMatchingService(IApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<bool> TryMatchAsync(ImagingStudy study, CancellationToken cancellationToken)
        {
            if (study == null) return false;
            // Never override an explicit human decision.
            if (study.MatchStatus == ImagingStudyMatchStatus.ManuallyAssigned) return false;

            var hid = study.HospitalId;
            var linked = false;

            // 0. Study Instance UID handed out by the modality worklist ("2.25.<service id>")
            //    → that exact scheduled service, its appointment and patient.
            if (study.AppointmentId == null)
            {
                var serviceId = DicomUids.TryToGuid(study.StudyInstanceUID);
                if (serviceId != null)
                {
                    var svc = await _context.AppointmentServices.IgnoreQueryFilters()
                        .Where(s => s.HospitalId == hid && s.Id == serviceId.Value && s.DeletedAt == null)
                        .Select(s => new { s.Id, s.AppointmentId, s.Appointment.PatientId })
                        .FirstOrDefaultAsync(cancellationToken);
                    if (svc != null)
                    {
                        study.AppointmentId = svc.AppointmentId;
                        study.AppointmentServiceId ??= svc.Id;
                        if (study.PatientId == null && svc.PatientId != Guid.Empty)
                            study.PatientId = svc.PatientId;
                        linked = true;
                    }
                }
            }

            // 1. Accession → appointment (and its patient).
            if (study.AppointmentId == null && !string.IsNullOrWhiteSpace(study.AccessionNumber))
            {
                var acc = study.AccessionNumber.Trim();
                Appointment? appt = null;
                if (Guid.TryParse(acc, out var accGuid))
                {
                    appt = await _context.Appointments.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(a => a.HospitalId == hid && a.AppointmentId == accGuid, cancellationToken);
                }
                appt ??= await _context.Appointments.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.HospitalId == hid && a.DisplayId == acc, cancellationToken);

                if (appt != null)
                {
                    study.AppointmentId = appt.AppointmentId;
                    if (study.PatientId == null && appt.PatientId != Guid.Empty)
                        study.PatientId = appt.PatientId;
                    linked = true;
                }
            }

            // 2. MRN → patient (exact + unique).
            if (study.PatientId == null && !string.IsNullOrWhiteSpace(study.DicomPatientId))
            {
                var mrn = study.DicomPatientId.Trim();
                var matches = await _context.Patients.IgnoreQueryFilters()
                    .Where(p => p.HospitalId == hid && p.DeletedAt == null && p.PatientIdentifier == mrn)
                    .Select(p => p.PatientId)
                    .Take(2)
                    .ToListAsync(cancellationToken);
                if (matches.Count == 1)
                {
                    study.PatientId = matches[0];
                    linked = true;
                }
            }

            // 3. Normalised name → patient (exact + UNIQUE only).
            if (study.PatientId == null && !string.IsNullOrWhiteSpace(study.PatientName))
            {
                var key = NameNormalizer.Normalize(study.PatientName);
                if (!string.IsNullOrEmpty(key))
                {
                    var matches = await _context.Patients.IgnoreQueryFilters()
                        .Where(p => p.HospitalId == hid && p.DeletedAt == null && p.NameNormalized == key)
                        .Select(p => p.PatientId)
                        .Take(2)
                        .ToListAsync(cancellationToken);
                    if (matches.Count == 1)
                    {
                        study.PatientId = matches[0];
                        linked = true;
                    }
                }
            }

            if (linked)
                study.MatchStatus = ImagingStudyMatchStatus.AutoMatched;

            return linked;
        }
    }
}
