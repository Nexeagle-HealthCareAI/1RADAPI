namespace _1Rad.Domain.Entities;

public class CustomRolePermission
{
    public Guid CustomRoleId { get; set; }
    public string RoutePath { get; set; } = string.Empty;

    // Navigation properties
    public CustomRole CustomRole { get; set; } = null!;
}
