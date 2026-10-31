using System.Text.Json.Nodes;
using _1Rad.Application.Common;

namespace _1Rad.Infrastructure.Interop;

/// <summary>One scheduled service, as read from the appointment tables.</summary>
public sealed record WorklistEntry(
    Guid AppointmentId,
    Guid ServiceId,
    string? DisplayId,
    DateTime ScheduledIst,   // Appointment.DateTime — India clock time
    string? Priority,
    string? AppointmentStatus,
    string? ReferredBy,
    string? ServiceName,
    string? ServiceModality,
    string? AppointmentModality,
    Guid PatientId,
    string? PatientName,
    string? PatientIdentifier,
    string? Gender,
    string? Age);

/// <summary>
/// Modality worklist items (the attributes of a DICOM MWL C-FIND response,
/// PS3.4 K.6) as DICOM JSON. The on-prem bridge turns these into Orthanc
/// worklist files; the modality's C-FIND then sees today's 1Rad bookings, so the
/// technician picks the patient instead of typing name and ID.
///
/// Each scheduled service gets a Study Instance UID derived from its id
/// (<see cref="DicomUids"/>). When the images come back with that UID, study
/// matching links them to exactly this service.
/// </summary>
public static class WorklistMapper
{
    public static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public static JsonObject ToItem(WorklistEntry e, int serviceIndex)
    {
        var localTime = e.ScheduledIst;
        var modality = DicomModality(e.ServiceModality) ?? DicomModality(e.AppointmentModality) ?? "OT";
        var procedureId = ProcedureId(e.DisplayId, e.AppointmentId, serviceIndex);
        var description = DicomJsonBuilder.Clean(e.ServiceName, 64);

        var sps = DicomJsonBuilder.New()
            .Str("00080060", "CS", modality)
            .Empty("00400001", "AE")                               // Scheduled Station AE Title — any
            .Str("00400002", "DA", localTime.ToString("yyyyMMdd"))
            .Str("00400003", "TM", localTime.ToString("HHmmss"))
            .Empty("00400006", "PN")                               // Scheduled Performing Physician
            .Str("00400007", "LO", description)
            .Str("00400009", "SH", procedureId)
            .Str("00400020", "CS", SpsStatus(e.AppointmentStatus));
        if (description == null) sps.Empty("00400007", "LO");

        var item = DicomJsonBuilder.New()
            .Str("00080005", "CS", "ISO_IR 192")
            .Str("00080050", "SH", Accession(e.DisplayId, e.AppointmentId))
            .Pn("00100010", DicomJsonBuilder.Clean(e.PatientName, 64))
            .Str("00100020", "LO", DicomJsonBuilder.Clean(e.PatientIdentifier, 64) ?? e.PatientId.ToString("N"))
            .Empty("00100030", "DA")
            .Str("00100040", "CS", DicomJsonBuilder.Sex(e.Gender))
            .Str("00101010", "AS", DicomJsonBuilder.Age(e.Age))
            .Str("0020000D", "UI", DicomUids.FromGuid(e.ServiceId))
            .Str("00321060", "LO", description)
            .Str("00401001", "SH", procedureId)
            .Str("00401003", "SH", RequestedPriority(e.Priority))
            .Seq("00400100", new[] { sps });

        var referrer = DicomJsonBuilder.Clean(e.ReferredBy, 64);
        if (referrer != null) item.Pn("00080090", referrer); else item.Empty("00080090", "PN");
        if (!item.ContainsKey("00100010")) item.Empty("00100010", "PN");
        if (!item.ContainsKey("00100040")) item.Empty("00100040", "CS");
        return item;
    }

    /// <summary>
    /// Accession number (SH, 16 chars). The appointment's display id ("APP-101")
    /// — which study matching already understands — else the start of the id.
    /// </summary>
    public static string Accession(string? displayId, Guid appointmentId)
    {
        var d = DicomJsonBuilder.Clean(displayId, 16);
        return d != null && d.Length == displayId!.Trim().Length ? d : appointmentId.ToString("N").Substring(0, 16);
    }

    private static string ProcedureId(string? displayId, Guid appointmentId, int serviceIndex)
    {
        var acc = Accession(displayId, appointmentId);
        var suffix = $"-{serviceIndex + 1}";
        return (acc.Length + suffix.Length > 16 ? acc.Substring(0, 16 - suffix.Length) : acc) + suffix;
    }

    private static string SpsStatus(string? appointmentStatus) =>
        (appointmentStatus ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "CONFIRMED" or "ARRIVED" => "ARRIVED",
            "IN_PROGRESS" => "STARTED",
            _ => "SCHEDULED",
        };

    private static string RequestedPriority(string? priority) =>
        (priority ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "EMERGENCY" or "STAT" or "URGENT" => "STAT",
            "HIGH" or "PRIORITY" => "HIGH",
            _ => "ROUTINE",
        };

    /// <summary>
    /// 1Rad's modality labels ("X-RAY", "MRI", "USG", …) as DICOM modality codes.
    /// Null when the label is empty or unknown.
    /// </summary>
    public static string? DicomModality(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var m = new string(label.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return m switch
        {
            "XRAY" or "DX" or "DR" or "RADIOGRAPHY" or "DIGITALXRAY" => "DX",
            "CR" => "CR",
            "CT" or "CTSCAN" or "HRCT" or "CECT" or "NCCT" => "CT",
            "MRI" or "MR" or "MRISCAN" => "MR",
            "USG" or "US" or "ULTRASOUND" or "ULTRASONOGRAPHY" or "SONOGRAPHY" or "DOPPLER" or "ECHO" => "US",
            "MAMMOGRAPHY" or "MAMMO" or "MG" => "MG",
            "PET" or "PETCT" or "PT" => "PT",
            "NM" or "NUCLEARMEDICINE" or "SPECT" => "NM",
            "FLUOROSCOPY" or "FLUORO" or "RF" or "XF" => "RF",
            "DEXA" or "BMD" or "BONEDENSITOMETRY" => "BMD",
            "OPG" or "PX" or "PANORAMIC" or "DENTAL" => "PX",
            "ECG" or "EKG" => "ECG",
            "ANGIO" or "ANGIOGRAPHY" or "XA" or "CATHLAB" => "XA",
            _ => null,
        };
    }
}
