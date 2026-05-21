using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.SetDisbursementStatus;

/// <summary>
/// Transitions a salary disbursement between Draft and Paid.
/// Also synchronises the linked Expense row so Finance stays in sync.
/// Callable from either Staff &amp; Payroll (HR) or Billing > Expenses (Accountant).
/// </summary>
public record SetDisbursementStatusCommand(
    Guid DisbursementId,
    Guid HospitalId,
    Guid? UpdatedByUserId,
    string Status,            // "Draft" | "Paid"
    string? PaymentMode,      // optional override when transitioning Draft -> Paid
    string? Reference,        // optional UTR / cheque number etc.
    string? PaidOnDate        // optional "YYYY-MM-DD" — defaults to today
) : IRequest<(bool Success, string? Error)>;
