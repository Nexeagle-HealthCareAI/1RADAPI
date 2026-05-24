namespace _1Rad.Application.Features.Staff.Queries.GetStaffSalary;

public record SalaryRevisionDto(
    Guid RevisionId,
    string EffectiveFrom,
    decimal BasicPay,
    decimal Hra,
    decimal Travel,
    decimal OtherAllowances,
    decimal PfDeduction,
    decimal Tds,
    decimal OtherDeductions,
    string? Note,
    DateTime CreatedAt
);

public record SalaryDisbursementDto(
    Guid DisbursementId,
    Guid? RevisionId,
    string Month,
    string Status,
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
    decimal EncashmentDays,
    decimal EncashmentBonus,
    decimal ExtraPay,
    string? ExtraPayReason,
    string PaymentMode,
    string? Reference,
    string PaidOnDate,
    string? Notes,
    DateTime CreatedAt
);

public record StaffSalaryDto(
    Guid StaffId,
    List<SalaryRevisionDto> Revisions,
    List<SalaryDisbursementDto> Disbursements
);
