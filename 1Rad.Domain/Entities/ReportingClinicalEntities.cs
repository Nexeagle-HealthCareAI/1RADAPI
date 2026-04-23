using _1Rad.Domain.Common;
using System;
using System.Collections.Generic;

namespace _1Rad.Domain.Entities
{
    public class ReportTemplate : BaseEntity, IHospitalContext
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string Modality { get; set; } = string.Empty; // X-RAY, MRI, etc.
        public bool IsStructured { get; set; } = false;
        
        // Stores JSON structure for structured reports or HTML/Text for narrative
        public string Content { get; set; } = string.Empty; 
        
        public Guid? DoctorId { get; set; } // Null if global/hospital template
        public Guid HospitalId { get; set; }
        
        // Navigation
        public User? Doctor { get; set; }
        public Hospital Hospital { get; set; } = null!;
    }

    public class ReportingKeyword : BaseEntity, IHospitalContext
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Trigger { get; set; } = string.Empty; // e.g. .norm
        public string ReplacementText { get; set; } = string.Empty; // Full findings text
        
        public Guid DoctorId { get; set; }
        public Guid HospitalId { get; set; }
        
        // Navigation
        public User Doctor { get; set; } = null!;
        public Hospital Hospital { get; set; } = null!;
    }

    public class DiagnosticReport : BaseEntity, IHospitalContext
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid AppointmentId { get; set; }
        public Guid DoctorId { get; set; }
        public Guid? TemplateId { get; set; }
        
        public string Findings { get; set; } = string.Empty; // Can be JSON for structured
        public string Impression { get; set; } = string.Empty;
        public string Advice { get; set; } = string.Empty;
        
        public bool IsFinalized { get; set; } = false;
        public DateTime? FinalizedAt { get; set; }
        public string ReportPdfUrl { get; set; } = string.Empty;
        
        public Guid HospitalId { get; set; }
        
        // Navigation
        public Appointment Appointment { get; set; } = null!;
        public User Doctor { get; set; } = null!;
        public ReportTemplate? Template { get; set; }
    }
}
