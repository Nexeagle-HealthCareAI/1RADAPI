using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.GrantBoardAccess;

/// <summary>
/// Grants a board login account to an existing HR staff record.
/// Creates a User + UserHospitalMapping so the staff member can sign in.
/// </summary>
public record GrantBoardAccessCommand(
    Guid StaffId,
    Guid HospitalId,
    string Password,
    List<string> RoleNames
) : IRequest<(bool Success, string? Error)>;
