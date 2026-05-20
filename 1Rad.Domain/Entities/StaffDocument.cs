using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class StaffDocument : BaseEntity
{
    public Guid DocumentId { get; set; } = Guid.NewGuid();
    public Guid StaffId { get; set; }
    public Guid HospitalId { get; set; }

    /// <summary>ID Proof | Medical License | Degree / Certificate | Employment Contract | Background Check | Other</summary>
    public string Category { get; set; } = "Other";

    public string FileName { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public int? FileSizeBytes { get; set; }

    /// <summary>Absolute URL of the file in Azure Blob Storage</summary>
    public string? BlobUrl { get; set; }

    /// <summary>Pending | Verified | Rejected</summary>
    public string VerificationStatus { get; set; } = "Pending";
    public string? Notes { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public Guid? UploadedByUserId { get; set; }

    // Navigation
    public StaffMember StaffMember { get; set; } = null!;
}
