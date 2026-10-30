using _1Rad.Application.Common;
using _1Rad.Domain.Constants;
using _1Rad.Infrastructure.Interop;
using FluentAssertions;

namespace _1Rad.UnitTests.Interop;

/// <summary>DICOMweb / worklist / FHIR helpers (gap analysis P3-6).</summary>
public class DicomUidTests
{
    [Fact]
    public void Service_id_round_trips_through_a_2_25_uid()
    {
        var id = Guid.NewGuid();
        var uid = DicomUids.FromGuid(id);
        uid.Should().StartWith("2.25.");
        uid.Length.Should().BeLessThanOrEqualTo(64);
        DicomUids.TryToGuid(uid).Should().Be(id);
    }

    [Fact]
    public void Edge_values_round_trip()
    {
        DicomUids.FromGuid(Guid.Empty).Should().Be("2.25.0");
        var max = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
        DicomUids.TryToGuid(DicomUids.FromGuid(max)).Should().Be(max);
    }

    [Theory]
    [InlineData("1.2.840.113619.2.55.3")]
    [InlineData("2.25.0123")]                                           // leading zero
    [InlineData("2.25.999999999999999999999999999999999999999")]          // > 128 bits
    [InlineData("2.25.12a")]
    [InlineData("")]
    public void Other_uids_are_not_mistaken_for_services(string uid) =>
        DicomUids.TryToGuid(uid).Should().BeNull();
}

public class IntegrationKeyTests
{
    [Fact]
    public void Generated_key_matches_only_its_own_hash()
    {
        var k = IntegrationKeys.Generate();
        IntegrationKeys.LooksLikeKey(k.FullKey).Should().BeTrue();
        IntegrationKeys.TryGetKeyId(k.FullKey).Should().Be(k.KeyId);
        IntegrationKeys.Matches(k.FullKey, k.SecretHash).Should().BeTrue();
        IntegrationKeys.Matches(k.FullKey + "x", k.SecretHash).Should().BeFalse();
        IntegrationKeys.Matches(IntegrationKeys.Generate().FullKey, k.SecretHash).Should().BeFalse();
        k.SecretHash.Should().HaveLength(64);
    }

    [Theory]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.x")] // a JWT
    [InlineData("1rk_short_x")]
    [InlineData("")]
    public void Other_tokens_are_not_keys(string token) =>
        IntegrationKeys.LooksLikeKey(token).Should().BeFalse();

    [Fact]
    public void Scopes_are_normalised_to_known_values()
    {
        IntegrationScopes.Normalise(new[] { "FHIR.read", "nonsense", "dicomweb.read", "fhir.read" })
            .Should().Equal("dicomweb.read", "fhir.read");
    }
}

public class QidoQueryTests
{
    [Fact]
    public void Reads_tags_keywords_ranges_and_paging()
    {
        var q = QidoQuery.Parse(new Dictionary<string, string?>
        {
            ["00100010"] = "SMITH*",
            ["StudyDate"] = "20260101-20260131",
            ["StudyInstanceUID"] = "1.2.3,1.2.4",
            ["limit"] = "5000",
            ["offset"] = "20",
            ["includefield"] = "all",
        });
        q.PatientName.Should().Be("SMITH*");
        q.StudyDateFrom.Should().Be(new DateTime(2026, 1, 1));
        q.StudyDateToExclusive.Should().Be(new DateTime(2026, 2, 1));
        q.StudyInstanceUids.Should().Equal("1.2.3", "1.2.4");
        q.Limit.Should().Be(QidoQuery.MaxLimit);
        q.Offset.Should().Be(20);
    }

    [Fact]
    public void Unreadable_date_marks_the_query_invalid() =>
        QidoQuery.Parse(new Dictionary<string, string?> { ["StudyDate"] = "yesterday" }).InvalidDate.Should().BeTrue();

    [Theory]
    [InlineData("SMITH^J*", false, "SMITH J%")]
    [InlineData("50%", false, "50[%]")]
    [InlineData("R?M", false, "R_M")]
    [InlineData("ram", true, "%ram%")]
    public void Wildcards_become_like_patterns(string value, bool contains, string expected) =>
        QidoQuery.ToLikePattern(value, contains).Should().Be(expected);

    [Fact]
    public void Star_alone_matches_everything() => QidoQuery.ToLikePattern("*", false).Should().BeNull();
}

public class DicomWebAcceptTests
{
    private const string Htj2k = "1.2.840.10008.1.2.4.202";

    [Theory]
    [InlineData(null, DicomWebAccept.ExplicitVrLittleEndian)]
    [InlineData("*/*", DicomWebAccept.ExplicitVrLittleEndian)]
    [InlineData("multipart/related; type=\"application/dicom\"", DicomWebAccept.ExplicitVrLittleEndian)]
    [InlineData("multipart/related; type=\"application/dicom\"; transfer-syntax=*", Htj2k)]
    [InlineData("multipart/related; type=\"application/dicom\"; transfer-syntax=1.2.840.10008.1.2.4.90", "1.2.840.10008.1.2.4.90")]
    public void Instance_syntax_follows_ps3_18(string? accept, string expected) =>
        DicomWebAccept.InstanceSyntax(accept, Htj2k).Should().Be(expected);

    [Fact]
    public void Instance_request_for_other_media_is_not_acceptable() =>
        DicomWebAccept.InstanceSyntax("multipart/related; type=\"application/octet-stream\"", Htj2k).Should().BeNull();

    [Fact]
    public void Ohif_frame_request_gets_the_stored_frame()
    {
        var c = DicomWebAccept.FrameSyntax("multipart/related; type=application/octet-stream; transfer-syntax=*", Htj2k, "image/jphc");
        c.Should().NotBeNull();
        c!.Value.Syntax.Should().Be(Htj2k);
        c.Value.ContentType.Should().Be("application/octet-stream");
    }

    [Fact]
    public void Plain_octet_stream_frames_are_decoded() =>
        DicomWebAccept.FrameSyntax("multipart/related; type=\"application/octet-stream\"", Htj2k, "image/jphc")!
            .Value.Syntax.Should().Be(DicomWebAccept.ExplicitVrLittleEndian);

    [Fact]
    public void A_compressed_type_we_dont_hold_is_not_acceptable() =>
        DicomWebAccept.FrameSyntax("multipart/related; type=\"image/jls\"", Htj2k, "image/jphc").Should().BeNull();

    [Fact]
    public void Choices_are_ordered_by_quality()
    {
        var list = DicomWebAccept.Parse(
            "multipart/related; type=\"image/jls\"; q=0.5, multipart/related; type=\"application/octet-stream\"; transfer-syntax=*; q=0.9");
        list[0].MediaType.Should().Be("application/octet-stream");
        list[0].TransferSyntax.Should().Be("*");
    }
}

public class WorklistMapperTests
{
    [Fact]
    public void Item_carries_ist_schedule_service_uid_and_codes()
    {
        var serviceId = Guid.NewGuid();
        var entry = new WorklistEntry(Guid.NewGuid(), serviceId, "APP-101",
            new DateTime(2026, 10, 30, 4, 30, 0, DateTimeKind.Utc), "EMERGENCY", "CONFIRMED", "Dr Sharma",
            "CT Brain Plain", "CT", "CT", Guid.NewGuid(), "Ram Kumar", "MRN-7", "Male", "45 Y");

        var json = WorklistMapper.ToItem(entry, 0).ToJsonString();

        json.Should().Contain(DicomUids.FromGuid(serviceId));
        json.Should().Contain("\"20261030\"").And.Contain("\"100000\"");   // 04:30 UTC = 10:00 IST
        json.Should().Contain("\"APP-101\"").And.Contain("\"APP-101-1\"");
        json.Should().Contain("\"STAT\"").And.Contain("\"ARRIVED\"").And.Contain("\"045Y\"");
    }

    [Theory]
    [InlineData("X-RAY", "DX")]
    [InlineData("USG", "US")]
    [InlineData("MRI", "MR")]
    [InlineData("Mammography", "MG")]
    [InlineData("DEXA", "BMD")]
    [InlineData("something", null)]
    public void Modality_labels_map_to_dicom(string label, string? expected) =>
        WorklistMapper.DicomModality(label).Should().Be(expected);

    [Fact]
    public void Long_display_ids_fall_back_to_a_16_char_accession() =>
        WorklistMapper.Accession("APPOINTMENT-1234567890", Guid.NewGuid()).Should().HaveLength(16);
}

public class FhirTests
{
    [Fact]
    public void Date_prefixes_build_a_range()
    {
        FhirSearch.TryParseDates(new[] { "ge2026-10-01", "lt2026-11-01" }, out var from, out var to).Should().BeTrue();
        from.Should().Be(new DateTime(2026, 10, 1));
        to.Should().Be(new DateTime(2026, 11, 1));

        FhirSearch.TryParseDates(new[] { "2026-10" }, out from, out to).Should().BeTrue();
        from.Should().Be(new DateTime(2026, 10, 1));
        to.Should().Be(new DateTime(2026, 11, 1));

        FhirSearch.TryParseDates(new[] { "xx2026-10-01" }, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void References_and_tokens_parse()
    {
        var id = Guid.NewGuid();
        FhirSearch.ReferenceId($"Patient/{id}", "Patient").Should().Be(id);
        FhirSearch.ReferenceId($"https://x/api/v1/fhir/Patient/{id}", "Patient").Should().Be(id);
        FhirSearch.ReferenceId("Patient/abc", "Patient").Should().BeNull();
        FhirSearch.Token("sys|123").Should().Be(("sys", "123"));
        FhirSearch.Token("123").Should().Be(((string?)null, "123"));
    }

    [Fact]
    public void Report_keeps_words_not_markup()
    {
        var id = Guid.NewGuid();
        var row = new FhirReportRow(id, Guid.NewGuid(), "Final", Guid.NewGuid(), "Ram", null, null,
            "<p>No acute&nbsp;abnormality.</p>", "{\"structured\":true}", "", "Dr A", "MD", DateTime.UtcNow, null, null,
            "https://api/x.pdf", 0, "CT Brain");
        var json = FhirMapper.DiagnosticReport(row).ToJsonString();
        json.Should().Contain("\"status\":\"final\"").And.Contain("18748-4").And.Contain("No acute");
        json.Should().NotContain("structured");
    }

    [Theory]
    [InlineData(null, "NOT_STARTED", null, "active")]
    [InlineData("REPORTED", "REPORTED", null, "completed")]
    [InlineData("NOT_STARTED", "CANCELLED", null, "revoked")]
    public void Service_request_status(string? svc, string? appt, string? cancelled, string expected) =>
        FhirMapper.ServiceRequestStatus(svc, appt, cancelled == null ? null : DateTime.UtcNow).Should().Be(expected);

    [Fact]
    public void Bundle_entries_have_full_urls()
    {
        var id = Guid.NewGuid();
        var patient = FhirMapper.Patient(new FhirPatientRow(id, Guid.NewGuid(), "Ram", "MRN1", "9876543210", "F", "30",
            null, null, null, "Patna", DateTime.UtcNow, null));
        FhirMapper.SearchBundle("self", "https://api/fhir", new[] { patient }, 1, null).ToJsonString()
            .Should().Contain($"https://api/fhir/Patient/{id}").And.Contain("\"gender\":\"female\"");
    }
}
