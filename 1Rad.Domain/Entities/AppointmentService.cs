using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// One line item of work attached to an Appointment.
///
/// A single patient visit can require multiple services (X-ray + CT + USG
/// in one walk-in). The Appointment row stays as the "visit container"
/// (one row per visit on the worklist), and each scan/study is modelled
/// as a child AppointmentService with its own status, TAT milestones,
/// pricing, and downstream report/study/commission rows.
///
/// Created in migration 57 alongside nullable AppointmentServiceId FKs on:
///   DiagnosticReports, StudyAssets, ReferralCommissions, InvoiceItems.
///
/// Until the multi-service rollout reaches every layer, the parent
/// Appointment's denormalised <c>Service</c>/<c>Modality</c> scalars stay
/// populated (as the "primary service") for backward compatibility with
/// older PWA installs that still read only the scalar fields.
/// </summary>
public class AppointmentService : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AppointmentId { get; set; }

    /// <summary>
    /// Optional pointer to the ServiceCharges catalogue row the receptionist
    /// picked at booking. Null when the price was edited manually or when
    /// the source catalogue row has since been deleted.
    /// </summary>
    public Guid? ServiceChargeId { get; set; }

    public string ServiceName { get; set; } = string.Empty;

    /// <summary>X-RAY, CT, MRI, USG, etc.</summary>
    public string Modality { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>Per-service referral cut. Drives a per-line ReferralCommission row.</summary>
    public decimal ReferralCutValue { get; set; }

    /// <summary>
    /// State machine, identical in spirit to Appointment.Status but scoped
    /// to this one service line:
    ///   NOT_STARTED  — booked, not scanned yet
    ///   SCANNED      — technician completed acquisition
    ///   REPORTED     — diagnostic report finalised
    ///   DELIVERED    — report handed to patient / referrer
    ///   CANCELLED    — service withdrawn from this visit
    /// </summary>
    public string Status { get; set; } = "NOT_STARTED";

    // ── Per-service TAT milestones ────────────────────────────────────────
    // The parent Appointment keeps rollup timestamps (earliest scan start,
    // latest delivery) for the overdue bell. These per-service timestamps
    // are the source of truth and drive per-line clocks.
    public DateTime? ScanStartedAt { get; set; }
    public DateTime? ScanCompletedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }

    /// <summary>Per-service technician (different scans in a visit may be done by different techs).</summary>
    public Guid? TechnicianId { get; set; }

    /// <summary>Free-text technician notes for this specific service.</summary>
    public string? TechnicianComments { get; set; }

    // ── Sync foundations ──────────────────────────────────────────────────
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    /// <summary>OCC token, server-maintained ROWVERSION.</summary>
    public byte[]? RowVersion { get; set; }

    public Guid HospitalId { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────
    public Appointment Appointment { get; set; } = null!;
    public Hospital Hospital { get; set; } = null!;
    public ServiceCharge? ServiceCharge { get; set; }
}
