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

    // Navigation
    public Invoice Invoice { get; set; } = null!;
}
