using _1Rad.Application.Features.Finance.Commands.GenerateInvoice;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using FluentAssertions;

namespace _1Rad.UnitTests.Features.Finance;

/// <summary>A free bill line already billed to the same patient today is refused unless confirmed.</summary>
public class DuplicateBillLineTests : BaseHandlerTest
{
    [Fact]
    public async Task Same_test_twice_today_needs_a_yes()
    {
        var patientId = Guid.NewGuid();
        Context.Patients.Add(new Patient { PatientId = patientId, FullName = "Asha Das", HospitalId = HospitalId });
        await Context.SaveChangesAsync();
        var h = new GenerateInvoiceCommandHandler(Context);
        GenerateInvoiceCommand Cmd(bool allow, string test = "CD copy") => new()
        {
            PatientId = patientId, AllowRepeat = allow, Items = new List<InvoiceItemDto> { new(test, 200m, 1) },
        };

        await h.Handle(Cmd(false), default);
        await FluentActions.Invoking(() => h.Handle(Cmd(false, " cd COPY "), default))
            .Should().ThrowAsync<DuplicateBillLineException>().WithMessage("*Asha Das already has CD copy*");

        await h.Handle(Cmd(true), default);                 // confirmed second one
        await h.Handle(Cmd(false, "Film copy"), default);    // a different test is fine
        Context.Invoices.Count().Should().Be(3);
    }
}
