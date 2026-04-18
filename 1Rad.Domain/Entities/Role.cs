namespace _1Rad.Domain.Entities;

public class Role
{
    public int RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;

    // Navigation properties
    public ICollection<UserHospitalMapping> HospitalMappings { get; set; } = new List<UserHospitalMapping>();
}
