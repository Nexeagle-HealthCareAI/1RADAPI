namespace _1Rad.Domain.Constants;

public static class RoleConstants
{
    /// <summary>
    /// Chief Medical Officer / Clinical Administrator (RoleId = 1)
    /// Full clinical and administrative access.
    /// </summary>
    public const string AdminDoctor = "AdminDoctor";

    /// <summary>
    /// Administrative Operator (RoleId = 2)
    /// Manages hospital operations, staff, and configurations.
    /// </summary>
    public const string AdminOperator = "Admin";

    /// <summary>
    /// Doctor (RoleId = 3)
    /// Standard clinical doctor. Can manage own prescription protocol.
    /// </summary>
    public const string Doctor = "Doctor";

    /// <summary>
    /// Technician (RoleId = 4)
    /// Can view prescription protocols for printing/execution. Read-only access.
    /// </summary>
    public const string Technician = "Technician";

    /// <summary>
    /// Receptionist (RoleId = 5)
    /// Front-desk access. Can view but not modify prescription protocols.
    /// </summary>
    public const string Receptionist = "Receptionist";

    /// <summary>
    /// Accountant (RoleId = 6)
    /// Finance access only. No prescription write access.
    /// </summary>
    public const string Accountant = "Accountant";
}
