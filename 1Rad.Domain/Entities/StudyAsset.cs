using _1Rad.Domain.Common;
using System;

namespace _1Rad.Domain.Entities
{
    public class StudyAsset : BaseEntity, IHospitalContext
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid AppointmentId { get; set; }
        public string BlobUrl { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty; // zip, dcm, jpg, png
        public string TechnicianComments { get; set; } = string.Empty;
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
        
        public Guid HospitalId { get; set; }
        
        // Navigation
        public Appointment Appointment { get; set; } = null!;
    }
}
