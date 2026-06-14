using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class InvoiceItem : BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }

    // Multi-service support (migration 57). When an appointment has many
    // services, each AppointmentService maps 1:1 to an InvoiceItem on
    // the visit's single Invoice. NULL on legacy rows that pre-date the
    // migration; backfill stamps it from the appointment's only service.
    public Guid? AppointmentServiceId { get; set; }

    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal SubTotal => Amount * Quantity;

    // Per-line free test (multi-service free billing). When true, this line is a
    // free test and is EXCLUDED from the invoice's payable total — the gross is
    // still recorded for the bill, but the patient owes nothing for this line.
    // Other lines on the same invoice can still be charged. Mirrors
    // AppointmentService.IsFree (the source of truth for the service line).
    public bool IsFree { get; set; } = false;

    // Navigation
    public Invoice Invoice { get; set; } = null!;
}
