namespace _1Rad.Domain.Constants;

/// <summary>
/// Who may do what (review A4, B15). Used in [Authorize(Roles = …)].
/// The least-privileged role is listed FIRST: when a custom role is let through by
/// CustomRoleRouteMap, ContextualSentinelMiddleware stands it in for one listed role,
/// and it must never be the admin one.
/// </summary>
public static class RoleSets
{
    private const string R = RoleConstants.Receptionist, T = RoleConstants.Technician, D = RoleConstants.Doctor,
        A = RoleConstants.Accountant, AD = RoleConstants.AdminDoctor, AO = RoleConstants.AdminOperator;

    /// Booking, editing a visit, changing the referrer.
    public const string FrontDesk = $"{R},{AD},{AO}";
    /// Moving a visit or a scan through the workflow.
    public const string Workflow = $"{T},{R},{D},{AD},{AO}";
    /// Bulk import of appointments.
    public const string Admins = $"{AD},{AO}";
    /// Bills, payments, a patient's advance — what the front desk needs to bill.
    public const string Billing = $"{R},{A},{AD},{AO}";
    /// Revenue, profit, the financial matrix and exports.
    public const string FinanceReports = $"{A},{AD},{AO}";

    /// Roles that may cancel a visit through the status endpoint.
    public static readonly IReadOnlySet<string> CanCancel =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { R, AD, AO };
}
