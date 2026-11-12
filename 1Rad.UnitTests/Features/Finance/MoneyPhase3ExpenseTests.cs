using _1Rad.Application.Common;
using _1Rad.Application.Features.Finance.Commands.DeleteExpense;
using _1Rad.Application.Features.Finance.Commands.PayExpense;
using _1Rad.Application.Features.Finance.DayBook;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

/// <summary>Money ▸ Expenses: to pay → paid, counted in the cash book only once paid.</summary>
public class MoneyPhase3ExpenseTests : BaseHandlerTest
{
    private readonly DateTime _today = IstDateRange.ToIst(DateTime.UtcNow).Date;

    private Expense Add(string status, Guid? payroll = null)
    {
        var e = new Expense { HospitalId = HospitalId, Description = "Electricity bill, October", Category = "Utilities", Amount = 900,
            Status = status, TransactionDate = DateTime.UtcNow, LinkedDisbursementId = payroll };
        Context.Expenses.Add(e);
        return e;
    }

    [Fact]
    public async Task ToPay_IsNotInTheCashBook_UntilPaid()
    {
        var e = Add("Pending"); await SaveChangesAsync();
        var book = await new GetDayBookQueryHandler(Context).Handle(new GetDayBookQuery(_today, _today), default);
        book.Lines.Should().BeEmpty();

        await new PayExpenseCommandHandler(Context).Handle(new PayExpenseCommand(e.Id, "BANK", "UTR 1234"), default);
        e.Status.Should().Be("Paid");
        e.PaymentMode.Should().Be("Bank Transfer");
        e.PaidAt.Should().NotBeNull();

        book = await new GetDayBookQueryHandler(Context).Handle(new GetDayBookQuery(_today, _today), default);
        book.Lines.Should().ContainSingle(l => l.Kind == "EXPENSE" && l.Mode == "BANK" && l.Amount == -900);
    }

    [Fact]
    public async Task PayingTwice_IsRefused()
    {
        var e = Add("Paid"); await SaveChangesAsync();
        var act = () => new PayExpenseCommandHandler(Context).Handle(new PayExpenseCommand(e.Id, "CASH", null), default);
        await act.Should().ThrowAsync<BusinessRuleViolationException>();
    }

    [Fact]
    public async Task PayrollExpense_CannotBeDeletedHere()
    {
        var e = Add("Paid", Guid.NewGuid()); await SaveChangesAsync();
        var act = () => new DeleteExpenseCommandHandler(Context).Handle(new DeleteExpenseCommand(e.Id), default);
        await act.Should().ThrowAsync<BusinessRuleViolationException>();
    }

    [Fact]
    public async Task Delete_KeepsTheRow()
    {
        var e = Add("Pending"); await SaveChangesAsync();
        await new DeleteExpenseCommandHandler(Context).Handle(new DeleteExpenseCommand(e.Id), default);
        (await Context.Expenses.IgnoreQueryFilters().SingleAsync(x => x.Id == e.Id)).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public void UnknownState_IsRefused() =>
        Assert.Throws<ValidationException>(() => ExpenseStates.Normalise("Settled-ish"));
}
