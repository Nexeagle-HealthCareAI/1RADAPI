using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class CustomRole : IHospitalContext
{
    public Guid CustomRoleId { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Hospital Hospital { get; set; } = null!;
    public ICollection<CustomRolePermission> Permissions { get; set; } = new List<CustomRolePermission>();
    public ICollection<UserHospitalMapping> UserHospitalMappings { get; set; } = new List<UserHospitalMapping>();
}
