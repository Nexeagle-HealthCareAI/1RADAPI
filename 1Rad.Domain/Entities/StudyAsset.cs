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

        /// <summary>
        /// State machine for the per-slice extraction pipeline (Option C):
        ///   Pending    — newly uploaded, not yet enqueued for extraction.
        ///   Queued     — sitting in the background queue.
        ///   Running    — extraction in progress (set when worker picks it up).
        ///   Extracted  — slice index populated; manifest endpoint will use it.
        ///   Failed     — extraction crashed; manifest endpoint falls back to ZIP.
        ///   NotApplicable — non-ZIP assets (single .dcm, .jpg, .png) skip extraction.
        /// Older rows have NULL/empty and are treated as <c>Pending</c>.
        /// </summary>
        public string? ExtractionStatus { get; set; }
        public DateTime? ExtractionStartedAt { get; set; }
        public DateTime? ExtractionCompletedAt { get; set; }
        public string? ExtractionError { get; set; }
        public int ExtractionSliceCount { get; set; }

        // Navigation
        public Appointment Appointment { get; set; } = null!;
        public ICollection<StudySliceIndex> Slices { get; set; } = new List<StudySliceIndex>();
    }
}
