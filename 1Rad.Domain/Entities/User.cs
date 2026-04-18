using _1Rad.Domain.Common;
using _1Rad.Domain.Enums;

namespace _1Rad.Domain.Entities;

public class User : BaseEntity
{
    public Guid UserId { get; set; } = Guid.NewGuid();
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsVerified { get; set; } = false;
    public UserStatus Status { get; set; } = UserStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<UserHospitalMapping> HospitalMappings { get; set; } = new List<UserHospitalMapping>();
}
