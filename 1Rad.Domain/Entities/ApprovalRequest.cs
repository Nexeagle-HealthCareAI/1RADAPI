using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// A sensitive change that needs admin sign-off before it takes effect —
/// post-payment edits, cancelling a PAID appointment, or re-pointing the
/// referrer on a PAID invoice. The biller submits a request with a reason; an
/// admin / admin-doctor reviews it on the Finance → Approvals page and either
/// APPROVES it (the change is applied) or REJECTS it. Nothing is applied while
/// the request is PENDING.
/// </summary>
public class ApprovalRequest : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }

    // EDIT_PAYMENT | CANCEL_APPOINTMENT | CHANGE_REFERRER
    public string Type { get; set; } = string.Empty;

    // Human label shown in the queue, e.g. "INV-2024-001 · Ravi Kumar".
    public string Title { get; set; } = string.Empty;

    // What the request targets — kept for display and for applying on approval.
    public Guid? InvoiceId { get; set; }
    public Guid? AppointmentId { get; set; }

    // JSON describing the requested change; applied verbatim when approved.
    public string Payload { get; set; } = "{}";

    public string Reason { get; set; } = string.Empty;

    // PENDING | APPROVED | REJECTED
    public string Status { get; set; } = "PENDING";

    public Guid RequestedBy { get; set; }
    public Guid? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }
    public DateTime? ReviewedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
}
