using System.Text.Json.Nodes;
using _1Rad.Domain.Entities;

namespace _1Rad.Infrastructure.Interop;

public sealed record FhirPatientRow(
    Guid Id, Guid HospitalId, string? FullName, string? Mrn, string? Mobile, string? Gender, string? Age,
    string? Address, string? Village, string? Block, string? District, DateTime UpdatedAt, DateTime? DeletedAt);

public sealed record FhirServiceRequestRow(
    Guid Id, Guid HospitalId, Guid AppointmentId, string? DisplayId, string? ServiceStatus, string? AppointmentStatus,
    string? Priority, string? ServiceName, string? Modality, DateTime ScheduledUtc, Guid PatientId, string? PatientName,
    string? ReferredBy, DateTime UpdatedAt, DateTime? CancelledAt);

public sealed record FhirSeriesRow(string SeriesUid, string? Modality, string? Description, int Instances, int? SeriesNumber);

public sealed record FhirImagingStudyRow(
    Guid Id, Guid HospitalId, string? StudyUid, string Status, Guid? PatientId, string? PatientName, string? DicomPatientId,
    string? AccessionNumber, string? Modality, DateTime? StudyDate, string? Description, Guid? AppointmentServiceId,
    DateTime CreatedAt, IReadOnlyList<FhirSeriesRow> Series);

public sealed record FhirReportRow(
    Guid Id, Guid HospitalId, string Status, Guid? PatientId, string? PatientName, Guid? ImagingStudyId,
    Guid? AppointmentServiceId, string? Impression, string? Findings, string? Advice, string? SignerName,
    string? SignerCredentials, DateTime? SignedAt, DateTime? FinalizedAt, DateTime? UpdatedAt, string? PdfUrl,
    int AddendumCount, string? ServiceName);

/// <summary>
/// 1Rad rows as FHIR R4 resources (gap analysis P3-6 phase 4): reads, plus Patient / ServiceRequest create.
/// Codes follow what India's ABDM / NRCeS profiles use (SNOMED CT categories,
/// LOINC report code, DICOM modality codes, v2-0074 "RAD"), so the resources drop
/// into an ABDM health-information exchange with little rework. They are not
/// declared as NRCeS-profile conformant (meta.profile is not set): patient
/// demographics in 1Rad are an age text and a mobile number, not the identity
/// set those profiles require.
/// </summary>
public static class FhirMapper
{
    public const string ContentType = "application/fhir+json";
    private const string DcmSystem = "http://dicom.nema.org/resources/ontology/DCM";

    public static string MrnSystem(Guid hospitalId) => $"https://1rad.nexeagle.com/fhir/sid/{hospitalId:N}/mrn";
    public static string AccessionSystem(Guid hospitalId) => $"https://1rad.nexeagle.com/fhir/sid/{hospitalId:N}/accession";

    // ── Patient ─────────────────────────────────────────────────────────────

    public static JsonObject Patient(FhirPatientRow p)
    {
        var o = new JsonObject
        {
            ["resourceType"] = "Patient",
            ["id"] = p.Id.ToString(),
            ["meta"] = Meta(p.UpdatedAt),
            ["active"] = p.DeletedAt == null,
        };
        if (!string.IsNullOrWhiteSpace(p.Mrn))
            o["identifier"] = new JsonArray(new JsonObject
            {
                ["type"] = CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "MR", "Medical record number"),
                ["system"] = MrnSystem(p.HospitalId),
                ["value"] = p.Mrn.Trim(),
            });
        if (!string.IsNullOrWhiteSpace(p.FullName))
            o["name"] = new JsonArray(new JsonObject { ["use"] = "official", ["text"] = p.FullName.Trim() });
        if (!string.IsNullOrWhiteSpace(p.Mobile))
            o["telecom"] = new JsonArray(new JsonObject { ["system"] = "phone", ["value"] = p.Mobile.Trim(), ["use"] = "mobile" });
        o["gender"] = Gender(p.Gender);

        var address = string.Join(", ", new[] { p.Address, p.Village, p.Block, p.District }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        if (address.Length > 0)
        {
            var a = new JsonObject { ["text"] = address, ["country"] = "IN" };
            if (!string.IsNullOrWhiteSpace(p.District)) a["district"] = p.District.Trim();
            o["address"] = new JsonArray(a);
        }
        // 1Rad records age as free text, not a birth date — kept as an extension-free
        // note on the resource text so nothing is invented.
        if (!string.IsNullOrWhiteSpace(p.Age))
            o["text"] = Narrative($"{Escape(p.FullName)} — age {Escape(p.Age)} (as recorded at registration)");
        return o;
    }

    public static string Gender(string? g)
    {
        if (string.IsNullOrWhiteSpace(g)) return "unknown";
        var s = g.Trim().ToUpperInvariant();
        if (s.StartsWith("M")) return "male";
        if (s.StartsWith("F") || s.StartsWith("W")) return "female";
        return "other";
    }

    // ── ServiceRequest (one per booked service line) ────────────────────────

    public static JsonObject ServiceRequest(FhirServiceRequestRow r)
    {
        var modality = WorklistMapper.DicomModality(r.Modality);
        var code = new JsonObject { ["text"] = string.IsNullOrWhiteSpace(r.ServiceName) ? (r.Modality ?? "Imaging") : r.ServiceName.Trim() };
        if (modality != null)
            code["coding"] = new JsonArray(Coding(DcmSystem, modality, null));

        var o = new JsonObject
        {
            ["resourceType"] = "ServiceRequest",
            ["id"] = r.Id.ToString(),
            ["meta"] = Meta(r.UpdatedAt),
            ["identifier"] = new JsonArray(new JsonObject
            {
                ["type"] = CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "ACSN", "Accession ID"),
                ["system"] = AccessionSystem(r.HospitalId),
                ["value"] = WorklistMapper.Accession(r.DisplayId, r.AppointmentId),
            }),
            ["status"] = ServiceRequestStatus(r.ServiceStatus, r.AppointmentStatus, r.CancelledAt),
            ["intent"] = "order",
            ["category"] = new JsonArray(CodeableConcept("http://snomed.info/sct", "363679005", "Imaging")),
            ["priority"] = Priority(r.Priority),
            ["code"] = code,
            ["subject"] = Reference("Patient", r.PatientId, r.PatientName),
            ["occurrenceDateTime"] = Instant(r.ScheduledUtc),
        };
        if (!string.IsNullOrWhiteSpace(r.ReferredBy))
            o["requester"] = new JsonObject { ["display"] = r.ReferredBy.Trim() };
        return o;
    }

    public static string ServiceRequestStatus(string? serviceStatus, string? appointmentStatus, DateTime? cancelledAt)
    {
        var s = (serviceStatus ?? string.Empty).Trim().ToUpperInvariant();
        var a = (appointmentStatus ?? string.Empty).Trim().ToUpperInvariant();
        if (cancelledAt != null || s == "CANCELLED" || a == "CANCELLED") return "revoked";
        if (s is "REPORTED" or "DELIVERED" || a is "REPORTED" or "DELIVERED" or "COMPLETED") return "completed";
        return "active";
    }

    private static string Priority(string? p) =>
        (p ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "EMERGENCY" or "STAT" => "stat",
            "URGENT" or "HIGH" or "PRIORITY" => "urgent",
            _ => "routine",
        };

    // ── ImagingStudy ────────────────────────────────────────────────────────

    public static JsonObject ImagingStudy(FhirImagingStudyRow s, string dicomWebBaseUrl)
    {
        var o = new JsonObject
        {
            ["resourceType"] = "ImagingStudy",
            ["id"] = s.Id.ToString(),
            ["meta"] = Meta(s.CreatedAt),
            ["status"] = s.Status switch
            {
                ImagingStudyStatus.Ready => "available",
                ImagingStudyStatus.Failed => "unknown",
                _ => "registered",
            },
            ["subject"] = s.PatientId != null
                ? Reference("Patient", s.PatientId.Value, s.PatientName)
                : new JsonObject
                {
                    // Not yet linked to a 1Rad patient — identify by what the images say.
                    ["display"] = s.PatientName ?? "Unidentified patient",
                    ["identifier"] = new JsonObject { ["value"] = s.DicomPatientId ?? "UNKNOWN" },
                },
        };

        var identifiers = new JsonArray();
        if (!string.IsNullOrWhiteSpace(s.StudyUid))
            identifiers.Add(new JsonObject { ["system"] = "urn:dicom:uid", ["value"] = $"urn:oid:{s.StudyUid}" });
        if (!string.IsNullOrWhiteSpace(s.AccessionNumber))
            identifiers.Add(new JsonObject
            {
                ["type"] = CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "ACSN", "Accession ID"),
                ["system"] = AccessionSystem(s.HospitalId),
                ["value"] = s.AccessionNumber.Trim(),
            });
        if (identifiers.Count > 0) o["identifier"] = identifiers;

        if (s.StudyDate != null) o["started"] = s.StudyDate.Value.ToString("yyyy-MM-ddTHH:mm:ss") + "+05:30";
        if (s.AppointmentServiceId != null)
            o["basedOn"] = new JsonArray(Reference("ServiceRequest", s.AppointmentServiceId.Value, null));

        var modalities = s.Series.Select(x => x.Modality).Append(s.Modality)
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!.Trim().ToUpperInvariant()).Distinct().ToList();
        if (modalities.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var m in modalities) arr.Add(Coding(DcmSystem, m, null));
            o["modality"] = arr;
        }
        if (!string.IsNullOrWhiteSpace(s.Description)) o["description"] = s.Description.Trim();

        if (s.Series.Count > 0)
        {
            o["numberOfSeries"] = s.Series.Count;
            o["numberOfInstances"] = s.Series.Sum(x => x.Instances);
            var series = new JsonArray();
            foreach (var x in s.Series)
            {
                var item = new JsonObject
                {
                    ["uid"] = x.SeriesUid,
                    ["modality"] = Coding(DcmSystem, string.IsNullOrWhiteSpace(x.Modality) ? "OT" : x.Modality.Trim().ToUpperInvariant(), null),
                    ["numberOfInstances"] = x.Instances,
                };
                if (x.SeriesNumber != null) item["number"] = x.SeriesNumber.Value;
                if (!string.IsNullOrWhiteSpace(x.Description)) item["description"] = x.Description.Trim();
                series.Add(item);
            }
            o["series"] = series;
        }

        // Where the images can be fetched (DICOMweb). Contained so the resource is self-describing.
        if (s.Status == ImagingStudyStatus.Ready && !string.IsNullOrWhiteSpace(s.StudyUid))
        {
            o["contained"] = new JsonArray(new JsonObject
            {
                ["resourceType"] = "Endpoint",
                ["id"] = "dicomweb",
                ["status"] = "active",
                ["connectionType"] = Coding("http://terminology.hl7.org/CodeSystem/endpoint-connection-type", "dicom-wado-rs", "DICOM WADO-RS"),
                ["payloadType"] = new JsonArray(new JsonObject { ["text"] = "DICOM" }),
                ["address"] = dicomWebBaseUrl,
            });
            o["endpoint"] = new JsonArray(new JsonObject { ["reference"] = "#dicomweb" });
        }
        return o;
    }

    // ── DiagnosticReport (signed reports only) ──────────────────────────────

    public static JsonObject DiagnosticReport(FhirReportRow r)
    {
        var issued = r.SignedAt ?? r.FinalizedAt ?? r.UpdatedAt;
        var o = new JsonObject
        {
            ["resourceType"] = "DiagnosticReport",
            ["id"] = r.Id.ToString(),
            ["meta"] = Meta(r.UpdatedAt ?? issued ?? DateTime.UtcNow),
            ["status"] = r.Status switch
            {
                ReportStatuses.Preliminary => "preliminary",
                ReportStatuses.Addended => "appended",
                _ => r.AddendumCount > 0 ? "appended" : "final",
            },
            ["category"] = new JsonArray(CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0074", "RAD", "Radiology")),
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray(Coding("http://loinc.org", "18748-4", "Diagnostic imaging study")),
                ["text"] = string.IsNullOrWhiteSpace(r.ServiceName) ? "Diagnostic imaging report" : r.ServiceName.Trim(),
            },
        };
        if (r.PatientId != null) o["subject"] = Reference("Patient", r.PatientId.Value, r.PatientName);
        else if (!string.IsNullOrWhiteSpace(r.PatientName)) o["subject"] = new JsonObject { ["display"] = r.PatientName };
        if (issued != null)
        {
            o["effectiveDateTime"] = Instant(issued.Value);
            o["issued"] = Instant(issued.Value);
        }
        if (!string.IsNullOrWhiteSpace(r.SignerName))
        {
            var display = string.IsNullOrWhiteSpace(r.SignerCredentials) ? r.SignerName.Trim() : $"{r.SignerName.Trim()}, {r.SignerCredentials.Trim()}";
            o["resultsInterpreter"] = new JsonArray(new JsonObject { ["display"] = display });
            o["performer"] = new JsonArray(new JsonObject { ["display"] = display });
        }
        if (r.AppointmentServiceId != null)
            o["basedOn"] = new JsonArray(Reference("ServiceRequest", r.AppointmentServiceId.Value, null));
        if (r.ImagingStudyId != null)
            o["imagingStudy"] = new JsonArray(Reference("ImagingStudy", r.ImagingStudyId.Value, null));

        var impression = PlainText(r.Impression);
        if (impression != null) o["conclusion"] = impression;

        var findings = PlainText(r.Findings);
        var advice = PlainText(r.Advice);
        var html = "";
        if (findings != null) html += $"<p><b>Findings</b></p><p>{Escape(findings)}</p>";
        if (impression != null) html += $"<p><b>Impression</b></p><p>{Escape(impression)}</p>";
        if (advice != null) html += $"<p><b>Advice</b></p><p>{Escape(advice)}</p>";
        if (html.Length > 0) o["text"] = new JsonObject { ["status"] = "generated", ["div"] = $"<div xmlns=\"http://www.w3.org/1999/xhtml\">{html}</div>" };

        if (!string.IsNullOrWhiteSpace(r.PdfUrl))
            o["presentedForm"] = new JsonArray(new JsonObject
            {
                ["contentType"] = "application/pdf",
                ["url"] = r.PdfUrl,
                ["title"] = "Signed report (PDF)",
            });
        return o;
    }

    // ── Reading what an HIS sends (create) ──────────────────────────────────

    public sealed record PatientInput(string? Name, string? Mrn, string? Mobile, string? Gender, string? Age, string? Address, string? District);

    public static PatientInput ReadPatient(JsonObject p, DateTime todayUtc)
    {
        string? name = null;
        if (p["name"] is JsonArray names && names.Count > 0 && names[0] is JsonObject n)
        {
            name = (string?)n["text"];
            if (string.IsNullOrWhiteSpace(name))
            {
                var given = n["given"] is JsonArray g ? string.Join(" ", g.Select(x => (string?)x).Where(x => !string.IsNullOrWhiteSpace(x))) : null;
                name = string.Join(" ", new[] { given, (string?)n["family"] }.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
        }

        string? mobile = null;
        if (p["telecom"] is JsonArray tel)
            mobile = tel.OfType<JsonObject>().Where(t => (string?)t["system"] == "phone")
                .Select(t => (string?)t["value"]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (mobile != null)
        {
            var digits = new string(mobile.Where(char.IsDigit).ToArray());
            mobile = digits.Length > 10 ? digits.Substring(digits.Length - 10) : digits;
        }

        string? mrn = null;
        if (p["identifier"] is JsonArray ids)
            mrn = ids.OfType<JsonObject>()
                .Where(i => i["type"]?["coding"] is JsonArray c && c.OfType<JsonObject>().Any(x => (string?)x["code"] == "MR"))
                .Select(i => (string?)i["value"]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var gender = ((string?)p["gender"])?.ToLowerInvariant() switch
        {
            "male" => "Male",
            "female" => "Female",
            "other" => "Other",
            _ => null,
        };

        string? age = null;
        if (DateTime.TryParse((string?)p["birthDate"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dob))
        {
            var today = (todayUtc + TimeSpan.FromHours(5.5)).Date;
            var years = today.Year - dob.Year - (today < dob.Date.AddYears(today.Year - dob.Year) ? 1 : 0);
            age = years >= 1 ? years.ToString() : $"{Math.Max(0, (today - dob.Date).Days / 30)} M";
        }

        string? address = null, district = null;
        if (p["address"] is JsonArray addrs && addrs.Count > 0 && addrs[0] is JsonObject a)
        {
            address = (string?)a["text"];
            if (string.IsNullOrWhiteSpace(address) && a["line"] is JsonArray lines)
                address = string.Join(", ", lines.Select(l => (string?)l).Where(l => !string.IsNullOrWhiteSpace(l)));
            district = (string?)a["district"] ?? (string?)a["city"];
        }

        return new PatientInput(name?.Trim(), mrn?.Trim(), string.IsNullOrEmpty(mobile) ? null : mobile, gender, age, address?.Trim(), district?.Trim());
    }

    public sealed record ServiceRequestInput(
        string? SubjectReference, string? ServiceName, string? DicomModality, DateTime? OccurrenceUtc,
        string Priority, string? Requester, string? Note);

    public static ServiceRequestInput ReadServiceRequest(JsonObject r)
    {
        var code = r["code"] as JsonObject;
        string? modality = null;
        if (code?["coding"] is JsonArray codings)
            modality = codings.OfType<JsonObject>()
                .Where(c => (string?)c["system"] == DcmSystem)
                .Select(c => (string?)c["code"]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var serviceName = (string?)code?["text"];
        if (string.IsNullOrWhiteSpace(serviceName) && code?["coding"] is JsonArray cs)
            serviceName = cs.OfType<JsonObject>().Select(c => (string?)c["display"]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        DateTime? when = null;
        var occ = (string?)r["occurrenceDateTime"];
        if (occ != null && DateTimeOffset.TryParse(occ, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
            when = dto.UtcDateTime;

        var priority = ((string?)r["priority"])?.ToLowerInvariant() switch
        {
            "stat" => "STAT",
            "urgent" or "asap" => "URGENT",
            _ => "ROUTINE",
        };

        var note = r["note"] is JsonArray notes
            ? string.Join(" ", notes.OfType<JsonObject>().Select(x => (string?)x["text"]).Where(x => !string.IsNullOrWhiteSpace(x)))
            : null;

        return new ServiceRequestInput(
            (string?)r["subject"]?["reference"], serviceName?.Trim(), modality?.Trim().ToUpperInvariant(), when, priority,
            ((string?)r["requester"]?["display"])?.Trim(), string.IsNullOrWhiteSpace(note) ? null : note.Trim());
    }

    /// <summary>A DICOM modality code as the label 1Rad uses on bookings.</summary>
    public static string? RadModality(string? dicomCode) => (dicomCode ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "CT" => "CT",
        "MR" => "MRI",
        "US" => "USG",
        "DX" or "CR" or "DR" => "X-RAY",
        "MG" => "MAMMOGRAPHY",
        "PT" => "PET",
        "NM" => "NM",
        "RF" => "FLUOROSCOPY",
        "BMD" => "DEXA",
        "PX" => "OPG",
        "XA" => "ANGIOGRAPHY",
        "ECG" => "ECG",
        _ => null,
    };

    // ── Bundles, CapabilityStatement, errors ────────────────────────────────

    public static JsonObject SearchBundle(string selfUrl, string baseUrl, IEnumerable<JsonObject> resources, int total, string? nextUrl)
    {
        var entries = new JsonArray();
        foreach (var r in resources)
            entries.Add(new JsonObject
            {
                ["fullUrl"] = $"{baseUrl}/{r["resourceType"]!.GetValue<string>()}/{r["id"]!.GetValue<string>()}",
                ["resource"] = r,
                ["search"] = new JsonObject { ["mode"] = "match" },
            });
        var links = new JsonArray(new JsonObject { ["relation"] = "self", ["url"] = selfUrl });
        if (nextUrl != null) links.Add(new JsonObject { ["relation"] = "next", ["url"] = nextUrl });
        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "searchset",
            ["total"] = total,
            ["link"] = links,
            ["entry"] = entries,
        };
    }

    public static JsonObject OperationOutcome(string code, string diagnostics, string severity = "error") => new()
    {
        ["resourceType"] = "OperationOutcome",
        ["issue"] = new JsonArray(new JsonObject
        {
            ["severity"] = severity,
            ["code"] = code,
            ["diagnostics"] = diagnostics,
        }),
    };

    public static JsonObject CapabilityStatement(string baseUrl, DateTime now)
    {
        JsonObject Resource(string type, params (string Name, string Type, string Doc)[] search)
        {
            var canCreate = type is "Patient" or "ServiceRequest";
            var sp = new JsonArray();
            foreach (var (name, t, doc) in search)
                sp.Add(new JsonObject { ["name"] = name, ["type"] = t, ["documentation"] = doc });
            return new JsonObject
            {
                ["type"] = type,
                ["interaction"] = canCreate
                    ? new JsonArray(new JsonObject { ["code"] = "read" }, new JsonObject { ["code"] = "search-type" }, new JsonObject { ["code"] = "create" })
                    : new JsonArray(new JsonObject { ["code"] = "read" }, new JsonObject { ["code"] = "search-type" }),
                ["searchParam"] = sp,
            };
        }

        return new JsonObject
        {
            ["resourceType"] = "CapabilityStatement",
            ["status"] = "active",
            ["date"] = now.ToString("yyyy-MM-dd"),
            ["publisher"] = "NexEagle — 1Rad",
            ["kind"] = "instance",
            ["software"] = new JsonObject { ["name"] = "1Rad FHIR facade" },
            ["implementation"] = new JsonObject { ["description"] = "1Rad radiology centre (read-only)", ["url"] = baseUrl },
            ["fhirVersion"] = "4.0.1",
            ["format"] = new JsonArray("application/fhir+json", "json"),
            ["rest"] = new JsonArray(new JsonObject
            {
                ["mode"] = "server",
                ["documentation"] = "Read; plus create for Patient and ServiceRequest (scope fhir.write). Authenticate with a centre integration key (scope fhir.read) as 'Authorization: Bearer 1rk_…' or a 1Rad staff access token.",
                ["security"] = new JsonObject { ["description"] = "Bearer token; data is limited to the key's centre." },
                ["resource"] = new JsonArray(
                    Resource("Patient",
                        ("identifier", "token", "MRN (value or system|value)"),
                        ("name", "string", "Name contains"),
                        ("phone", "token", "Mobile number"),
                        ("_lastUpdated", "date", "ge/le/gt/lt prefixes")),
                    Resource("ServiceRequest",
                        ("patient", "reference", "Patient/{id}"),
                        ("identifier", "token", "Accession number"),
                        ("occurrence", "date", "Scheduled date, ge/le/gt/lt/eq prefixes"),
                        ("status", "token", "active | completed | revoked")),
                    Resource("ImagingStudy",
                        ("patient", "reference", "Patient/{id}"),
                        ("identifier", "token", "urn:oid:{StudyInstanceUID} or accession number"),
                        ("started", "date", "Study date, ge/le/gt/lt/eq prefixes")),
                    Resource("DiagnosticReport",
                        ("patient", "reference", "Patient/{id}"),
                        ("based-on", "reference", "ServiceRequest/{id}"),
                        ("issued", "date", "Signed date, ge/le/gt/lt/eq prefixes"),
                        ("status", "token", "preliminary | final | appended"))),
            }),
        };
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static JsonObject Meta(DateTime updatedUtc) => new() { ["lastUpdated"] = Instant(updatedUtc) };

    public static string Instant(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static JsonObject Coding(string system, string code, string? display)
    {
        var c = new JsonObject { ["system"] = system, ["code"] = code };
        if (display != null) c["display"] = display;
        return c;
    }

    private static JsonObject CodeableConcept(string system, string code, string display) => new()
    {
        ["coding"] = new JsonArray(Coding(system, code, display)),
        ["text"] = display,
    };

    private static JsonObject Reference(string type, Guid id, string? display)
    {
        var r = new JsonObject { ["reference"] = $"{type}/{id}" };
        if (!string.IsNullOrWhiteSpace(display)) r["display"] = display.Trim();
        return r;
    }

    private static JsonObject Narrative(string text) => new()
    {
        ["status"] = "generated",
        ["div"] = $"<div xmlns=\"http://www.w3.org/1999/xhtml\">{text}</div>",
    };

    /// <summary>Report sections may hold light HTML or structured JSON from the editor; keep the words.</summary>
    public static string? PlainText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (t.StartsWith("{") || t.StartsWith("[")) return null; // structured field JSON — not prose
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*(br|/p|/div|/li)\s*/?\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>", string.Empty);
        t = System.Net.WebUtility.HtmlDecode(t);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[ \t]+", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\n\s*\n+", "\n").Trim();
        return t.Length == 0 ? null : t;
    }

    private static string Escape(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
