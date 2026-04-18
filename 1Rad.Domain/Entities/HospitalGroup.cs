namespace _1Rad.Domain.Entities;

public class HospitalGroup
{
    public Guid GroupId { get; set; } = Guid.NewGuid();
    public string GroupName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Hospital> Hospitals { get; set; } = new List<Hospital>();
}
