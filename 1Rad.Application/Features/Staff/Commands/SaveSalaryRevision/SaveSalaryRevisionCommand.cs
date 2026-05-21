using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.SaveSalaryRevision;

/// <summary>
/// Upserts a salary revision for a staff member. If a revision already exists
/// with the same StaffId + EffectiveFrom, it is replaced. Otherwise a new one
/// is appended to the history.
/// </summary>
public record SaveSalaryRevisionCommand(
    Guid StaffId,
    Guid HospitalId,
    Guid? CreatedByUserId,
    string EffectiveFrom, // "YYYY-MM-DD"
    decimal BasicPay,
    decimal Hra,
    decimal Travel,
    decimal OtherAllowances,
    decimal PfDeduction,
    decimal Tds,
    decimal OtherDeductions,
    string? Note
) : IRequest<(Guid RevisionId, string? Error)>;
