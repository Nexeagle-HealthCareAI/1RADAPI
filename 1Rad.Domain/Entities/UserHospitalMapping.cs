namespace _1Rad.Domain.Entities;

public class UserHospitalMapping
{
    public Guid MappingId { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid HospitalId { get; set; }
    public bool IsDefault { get; set; } = false;
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public User User { get; set; } = null!;
    public Hospital Hospital { get; set; } = null!;
    public ICollection<Role> Roles { get; set; } = new List<Role>();
    public ICollection<CustomRole> CustomRoles { get; set; } = new List<CustomRole>();
}
