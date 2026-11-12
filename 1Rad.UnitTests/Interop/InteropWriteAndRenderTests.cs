using System.Text.Json.Nodes;
using _1Rad.Infrastructure.Interop;
using FluentAssertions;

namespace _1Rad.UnitTests.Interop;

/// <summary>WADO-RS rendering, the STOW JSON form and FHIR create (gap analysis P3-6, second pass).</summary>
public class DicomRendererTests
{
    private static DicomRenderer.PixelFrame Ct(params short[] hu)
    {
        var bytes = new byte[hu.Length * 2];
        Buffer.BlockCopy(hu, 0, bytes, 0, bytes.Length);
        return new DicomRenderer.PixelFrame(bytes, 1, hu.Length, 1, 16, true, 1, 0, "MONOCHROME2", 0, 40, 400);
    }

    [Fact]
    public void Ct_window_maps_hounsfield_to_gray()
    {
        var d = DicomRenderer.ToDisplayPixels(Ct(-1000, 40, 1000))!.Value;
        d.Rgb.Should().BeFalse();
        d.Pixels[0].Should().Be(0);
        d.Pixels[1].Should().BeInRange(127, 129);
        d.Pixels[2].Should().Be(255);
    }

    [Fact]
    public void Monochrome1_is_inverted() =>
        DicomRenderer.ToDisplayPixels(Ct(-1000) with { Photometric = "MONOCHROME1" })!.Value.Pixels[0].Should().Be(255);

    [Fact]
    public void Planar_rgb_is_interleaved()
    {
        var f = new DicomRenderer.PixelFrame(new byte[] { 1, 2, 3, 4, 5, 6 }, 1, 2, 3, 8, false, 1, 0, "RGB", 1, null, null);
        DicomRenderer.ToDisplayPixels(f)!.Value.Pixels.Should().Equal(1, 3, 5, 2, 4, 6);
    }

    [Fact]
    public void Palette_colour_is_not_renderable() =>
        DicomRenderer.ToDisplayPixels(Ct(0) with { Photometric = "PALETTE COLOR" }).Should().BeNull();

    [Fact]
    public void Viewport_scales_down_only()
    {
        DicomRenderer.Fit(512, 256, 128, 128).Should().Be((128, 64));
        DicomRenderer.Fit(100, 100, 500, null).Should().Be((100, 100));
    }

    [Fact]
    public void Query_and_accept_are_read()
    {
        var o = DicomRenderer.ParseQuery("50", "256,256", "40,400,LINEAR", "image/png");
        o.Quality.Should().Be(50);
        o.ViewportWidth.Should().Be(256);
        o.WindowCenter.Should().Be(40);
        o.WindowWidth.Should().Be(400);
        DicomRenderer.MediaType("image/jpeg;q=0.5, image/png").Should().Be("image/png");
        DicomRenderer.MediaType("application/dicom").Should().BeNull();
        DicomRenderer.MediaType(null).Should().Be("image/jpeg");
    }
}

public class StowJsonFormTests
{
    [Fact]
    public void Bulk_parts_are_found_by_content_location()
    {
        var parts = new List<DicomJsonStowAssembler.BulkPart>
        {
            new("http://h/bulk/1", "application/octet-stream", new byte[1]),
            new("bulk/2", "image/jpeg; transfer-syntax=1.2.840.10008.1.2.4.50", new byte[1]),
        };
        DicomJsonStowAssembler.Find(parts, "http://h/bulk/1").Should().BeSameAs(parts[0]);
        DicomJsonStowAssembler.Find(parts, "https://x/y/bulk/2").Should().BeSameAs(parts[1]);
        DicomJsonStowAssembler.Find(parts, "nothing").Should().BeNull();
        DicomJsonStowAssembler.TransferSyntaxParam(parts[1].ContentType!).Should().Be("1.2.840.10008.1.2.4.50");
    }
}

public class FhirCreateTests
{
    [Fact]
    public void Patient_from_an_his_is_read()
    {
        var json = """
        {"resourceType":"Patient","name":[{"given":["Ram","K"],"family":"Kumar"}],
         "telecom":[{"system":"email","value":"a@b"},{"system":"phone","value":"+91 98765-43210"}],
         "gender":"male","birthDate":"1980-11-15",
         "identifier":[{"type":{"coding":[{"code":"MR"}]},"value":"H123"}],
         "address":[{"line":["12 MG Road"],"district":"Patna"}]}
        """;
        var p = FhirMapper.ReadPatient((JsonObject)JsonNode.Parse(json)!, new DateTime(2026, 10, 30));
        p.Name.Should().Be("Ram K Kumar");
        p.Mobile.Should().Be("9876543210");
        p.Gender.Should().Be("Male");
        p.Age.Should().Be("45");
        p.Mrn.Should().Be("H123");
        p.District.Should().Be("Patna");
    }

    [Fact]
    public void Infant_age_is_in_months() =>
        FhirMapper.ReadPatient((JsonObject)JsonNode.Parse("""{"name":[{"text":"Baby"}],"birthDate":"2026-07-01"}""")!,
            new DateTime(2026, 10, 30)).Age.Should().Be("4 M");

    [Fact]
    public void Order_from_an_his_is_read()
    {
        var json = """
        {"resourceType":"ServiceRequest","subject":{"reference":"Patient/x"},
         "code":{"coding":[{"system":"http://dicom.nema.org/resources/ontology/DCM","code":"ct"}],"text":"CT Brain"},
         "occurrenceDateTime":"2026-10-31T10:00:00+05:30","priority":"stat",
         "requester":{"display":"Dr A"},"note":[{"text":"r/o bleed"}]}
        """;
        var o = FhirMapper.ReadServiceRequest((JsonObject)JsonNode.Parse(json)!);
        o.ServiceName.Should().Be("CT Brain");
        o.DicomModality.Should().Be("CT");
        o.OccurrenceUtc.Should().Be(new DateTime(2026, 10, 31, 4, 30, 0));
        o.Priority.Should().Be("STAT");
        o.Requester.Should().Be("Dr A");
        FhirMapper.RadModality("MR").Should().Be("MRI");
    }
}
