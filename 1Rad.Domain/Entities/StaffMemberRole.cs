namespace _1Rad.Domain.Entities;

public class StaffMemberRole
{
    public int Id { get; set; }
    public Guid StaffId { get; set; }
    public string RoleName { get; set; } = string.Empty;

    // Navigation
    public StaffMember StaffMember { get; set; } = null!;
}
