using _1Rad.Application.Common;
using _1Rad.Application.Features.Reporting.Commands.AiAssist;
using _1Rad.Application.Features.Reporting.Commands.GenerateVoiceReport;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using FluentAssertions;

namespace _1Rad.UnitTests;

/// <summary>
/// Report AI privacy: identifiers are masked in everything sent (name and its
/// parts, phones, e-mails; whole words only), the voice draft no longer sends
/// the patient's name, and the centre's AI rules (off / monthly allowance) hold.
/// </summary>
public class ReportAiPrivacyTests : BaseHandlerTest
{
    private readonly FakeClaude _claude = new();

    [Fact]
    public void Redactor_masks_name_parts_contacts_and_restores_them()
    {
        var phi = PhiRedactor.WithNameParts("Pooja Kumari", "PT-1043");
        var text = "Mrs Kumari (PT-1043), call 98765 43210 or pooja.k@example.com. Liver 12.4 cm, frame normal.";

        var (safe, map) = PhiRedactor.Redact(text, phi);

        safe.Should().NotContain("Kumari").And.NotContain("PT-1043").And.NotContain("98765").And.NotContain("example.com");
        safe.Should().Contain("12.4 cm");
        PhiRedactor.Restore(safe, map).Should().Be(text);
    }

    [Fact]
    public void Redactor_matches_whole_words_only()
    {
        var (safe, _) = PhiRedactor.Redact("Ram: frame and Ramesh unchanged.", new[] { "Ram" });
        safe.Should().Contain("frame").And.Contain("Ramesh").And.NotContain("Ram:");
    }

    private async Task<(Appointment Visit, ReportTemplate Tpl)> VisitAsync()
    {
        var patient = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "Pooja Kumari", Age = "24", Gender = "Female", Mobile = "9876543210", PatientIdentifier = "PT-1043" };
        var visit = new Appointment { AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = "Pooja Kumari", Mobile = "9876543210", DisplayId = "APP-2610-0034", Service = "USG Abdomen", Modality = "USG" };
        var tpl = new ReportTemplate { Id = Guid.NewGuid(), HospitalId = HospitalId, Name = "USG", Modality = "USG", Content = "<p>Liver normal.</p>" };
        Context.Patients.Add(patient);
        Context.Appointments.Add(visit);
        Context.ReportTemplates.Add(tpl);
        await Context.SaveChangesAsync();
        return (visit, tpl);
    }

    [Fact]
    public async Task Voice_draft_never_sends_the_patients_name_or_phone()
    {
        var (visit, tpl) = await VisitAsync();
        _claude.Reply = "<p>Liver normal. Seen for [[PHI0]].</p>";
        var handler = new GenerateVoiceReportCommandHandler(Context, _claude, new ReportAiGate(Context));

        var res = await handler.Handle(new GenerateVoiceReportCommand
        {
            AppointmentId = visit.AppointmentId.ToString(), TemplateId = tpl.Id,
            Transcript = "Patient Pooja Kumari, mobile 9876543210. Liver normal.",
        }, CancellationToken.None);

        res.Success.Should().BeTrue();
        _claude.LastUser.Should().NotContain("Pooja").And.NotContain("Kumari").And.NotContain("9876543210");
        _claude.LastUser.Should().Contain("Age: 24").And.Contain("USG Abdomen");
        Context.ReportAiUsageLogs.Single().Feature.Should().Be("voice");
        Context.ReportAiUsageLogs.Single().MaskedCount.Should().BeGreaterThan(0);
        Context.ReportAiUsageLogs.Single().InputTokens.Should().Be(100);
    }

    [Fact]
    public async Task Assist_masks_the_context_too()
    {
        var (visit, _) = await VisitAsync();
        _claude.Reply = "<p>ok</p>";
        var handler = new AiAssistCommandHandler(_claude, Context, new ReportAiGate(Context));

        await handler.Handle(new AiAssistCommand
        {
            Action = "proofread", Text = "Liver normal.", Context = "Study for Pooja Kumari (APP-2610-0034)", AppointmentId = visit.AppointmentId,
        }, CancellationToken.None);

        _claude.LastUser.Should().NotContain("Pooja").And.NotContain("APP-2610-0034");
    }

    [Fact]
    public async Task A_centre_can_switch_report_AI_off()
    {
        Context.CentreAiPolicies.Add(new CentreAiPolicy { HospitalId = HospitalId, ReportAiEnabled = false });
        await Context.SaveChangesAsync();

        var res = await new AiAssistCommandHandler(_claude, Context, new ReportAiGate(Context))
            .Handle(new AiAssistCommand { Action = "proofread", Text = "Liver normal." }, CancellationToken.None);

        res.Success.Should().BeFalse();
        res.Error.Should().Contain("switched off");
        _claude.Calls.Should().Be(0);
    }

    [Fact]
    public async Task The_monthly_allowance_is_enforced()
    {
        Context.CentreAiPolicies.Add(new CentreAiPolicy { HospitalId = HospitalId, MonthlyCallLimit = 2 });
        await Context.SaveChangesAsync();
        var handler = new AiAssistCommandHandler(_claude, Context, new ReportAiGate(Context));
        _claude.Reply = "<p>ok</p>";

        for (var i = 0; i < 2; i++)
            (await handler.Handle(new AiAssistCommand { Action = "proofread", Text = "Liver normal." }, CancellationToken.None)).Success.Should().BeTrue();
        var third = await handler.Handle(new AiAssistCommand { Action = "proofread", Text = "Liver normal." }, CancellationToken.None);

        third.Success.Should().BeFalse();
        third.Error.Should().Contain("this month");
        _claude.Calls.Should().Be(2);
    }

    private sealed class FakeClaude : IAnthropicService
    {
        public string Reply { get; set; } = "";
        public string LastUser { get; private set; } = "";
        public int Calls { get; private set; }
        public bool IsConfigured => true;
        public string ModelName => "test-model";

        public Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        { Calls++; LastUser = userPrompt; return Task.FromResult(Reply); }

        public Task<AiJsonResult> GenerateWithUsageAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        { Calls++; LastUser = userPrompt; return Task.FromResult(new AiJsonResult(Reply, new AiUsage(100, 20, 0, 0))); }

        public Task<string> GenerateJsonAsync(string systemPrompt, string userPrompt, object? responseSchema, CancellationToken cancellationToken = default)
        { Calls++; LastUser = userPrompt; return Task.FromResult(Reply); }

        public Task<AiJsonResult> GenerateJsonWithUsageAsync(string systemPrompt, string userPrompt, object? responseSchema, CancellationToken cancellationToken = default)
        { Calls++; LastUser = userPrompt; return Task.FromResult(new AiJsonResult(Reply, new AiUsage(100, 20, 0, 0))); }
    }
}
