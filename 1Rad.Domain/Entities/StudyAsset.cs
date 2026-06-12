using _1Rad.Domain.Common;
using System;

namespace _1Rad.Domain.Entities
{
    public class StudyAsset : BaseEntity, IHospitalContext
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        // Phase 1 of the RIS/PACS SKU split: the appointment link is now
        // OPTIONAL. RIS+PACS flows always set it (behaviour unchanged);
        // PACS-only ingestion (Phase 2) creates assets with only the
        // ImagingStudy link below.
        public Guid? AppointmentId { get; set; }

        // The imaging aggregate this asset belongs to. Set for every
        // DICOM-bearing asset (zip / instances / dcm) going forward and
        // backfilled for legacy rows; null for plain document attachments
        // (pdf / jpg / png), which are visit paperwork, not imaging.
        public Guid? ImagingStudyId { get; set; }

        // Multi-service support (migration 57). A multi-modality visit
        // (X-ray + CT) routes each acquisition's assets to the correct
        // service line so the right report opens against the right
        // images. NULL on legacy rows that pre-date the migration.
        public Guid? AppointmentServiceId { get; set; }

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
        /// <summary>
        /// Bytes this asset persists in blob storage — the basis for PACS
        /// storage metering (Phase 3 of the RIS/PACS split). Set at upload
        /// (source file size) and recomputed by extraction to the durable
        /// total: original blob (ZIPs are kept) + transcoded slices. 0 on
        /// legacy rows uploaded before metering existed.
        /// </summary>
        public long StorageBytes { get; set; }

        public string? ExtractionStatus { get; set; }
        public DateTime? ExtractionStartedAt { get; set; }
        public DateTime? ExtractionCompletedAt { get; set; }
        public string? ExtractionError { get; set; }
        public int ExtractionSliceCount { get; set; }

        // ── Durable, multi-instance extraction queue (migration 60) ──────────
        // The work queue is the StudyAssets table itself: a row in 'Queued' is a
        // job, claimed atomically with a LEASE so many API instances can pull
        // work safely (READPAST skip), and a crashed instance's lease expires so
        // the job is reclaimed — no in-memory queue, no double-processing, no
        // lost work on restart. Retry + live progress are columns too, so ANY
        // instance answers the status poll and survives restarts.

        /// <summary>Instance id currently holding the processing lease (null when idle).</summary>
        public string? ExtractionLeaseOwner { get; set; }

        /// <summary>Lease expiry. A 'Running' row past this is treated as abandoned and reclaimable.</summary>
        public DateTime? ExtractionLeaseUntil { get; set; }

        /// <summary>How many times extraction has been attempted (durable retry counter).</summary>
        public int ExtractionAttempts { get; set; }

        /// <summary>Backoff gate — a 'Queued' row is not claimed before this time.</summary>
        public DateTime? ExtractionNextAttemptAt { get; set; }

        /// <summary>Live progress phase shown in the viewer (Downloading / Processing / Finalizing).</summary>
        public string? ExtractionPhase { get; set; }

        /// <summary>Live progress numerator — slices handled so far.</summary>
        public int ExtractionProcessedSlices { get; set; }

        /// <summary>Live progress denominator — total slices discovered (0 until known).</summary>
        public int ExtractionTotalSlices { get; set; }

        // Navigation
        public Appointment? Appointment { get; set; }
        public ImagingStudy? ImagingStudy { get; set; }
        public ICollection<StudySliceIndex> Slices { get; set; } = new List<StudySliceIndex>();
    }
}
