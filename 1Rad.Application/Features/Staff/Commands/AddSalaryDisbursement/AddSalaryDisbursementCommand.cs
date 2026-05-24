using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.AddSalaryDisbursement;

public record AddSalaryDisbursementCommand(
    Guid StaffId,
    Guid HospitalId,
    Guid? CreatedByUserId,
    Guid? RevisionId,
    string Month,            // "YYYY-MM"
    decimal GrossPay,
    decimal NetPay,
    decimal StructureGross,
    decimal StructureNet,
    decimal LwpDays,
    decimal LwpDeduction,
    decimal PerDayRate,
    int PaidLeaveInMonth,
    int LwpLeaveInMonth,
    string? AttendanceJson,
    string PaymentMode,      // bank | cash | upi | cheque
    string? Reference,
    string PaidOnDate,       // "YYYY-MM-DD"
    string? Notes,
    decimal EncashmentDays = 0,
    decimal EncashmentBonus = 0,
    decimal ExtraPay = 0,
    string? ExtraPayReason = null,
    string Status = "Paid"   // Draft | Paid — defaults to Paid for backwards compatibility
) : IRequest<(Guid DisbursementId, string? Error)>;
