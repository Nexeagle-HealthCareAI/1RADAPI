using MediatR;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.GenerateInvoice;

public record GenerateInvoiceCommand : IRequest<Guid>
{
    public Guid? AppointmentId { get; init; }
    public Guid PatientId { get; init; }
    public Guid? ReferrerId { get; init; }
    public decimal CentreDiscount { get; init; }
    public decimal ReferrerDiscount { get; init; }
    public decimal? CommissionAmount { get; init; }
    public List<InvoiceItemDto> Items { get; init; } = new();
}

// Multi-service rollout (batch-2 fix). AppointmentServiceId is optional —
// supplied when the frontend chose a pending billable that came from the
// fan-out in GetPendingBillablesQuery, omitted on freeform "add registry
// service" lines. Server stamps it on the resulting InvoiceItem so the
// line attaches to the right AppointmentService row.
public record InvoiceItemDto(
    string Description,
    decimal Amount,
    int Quantity,
    Guid? AppointmentServiceId = null
);

public class GenerateInvoiceCommandHandler : IRequestHandler<GenerateInvoiceCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public GenerateInvoiceCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(GenerateInvoiceCommand request, CancellationToken cancellationToken)
    {
        try
        {
            // Validate items
            if (request.Items == null || !request.Items.Any())
            {
                throw new ArgumentException("Invoice must contain at least one item.", nameof(request.Items));
            }

            // Validate item amounts
            foreach (var item in request.Items)
            {
                if (item.Amount <= 0)
                {
                    throw new ArgumentException($"Item '{item.Description}' has invalid amount. Amount must be greater than zero.");
                }
                if (item.Quantity <= 0)
                {
                    throw new ArgumentException($"Item '{item.Description}' has invalid quantity. Quantity must be greater than zero.");
                }
            }

            var patient = await _context.Patients
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.PatientId == request.PatientId, cancellationToken);
            
            if (patient == null)
            {
                throw new KeyNotFoundException($"Patient with ID '{request.PatientId}' not found in the system (Checked global registry).");
            }

            var hospitalId = _context.UserContext.HospitalId;
            
            // Fallback to patient's hospital ID if context is missing (common in some automated background tasks)
            if (hospitalId == Guid.Empty)
            {
                hospitalId = patient.HospitalId;
            }

            // Verify patient belongs to the hospital
            if (patient.HospitalId != hospitalId)
            {
                throw new UnauthorizedAccessException($"Patient does not belong to your hospital.");
            }

            // Verify appointment if provided
            // Captured here (outside the block below) so the referral commission
            // created further down can stamp ServiceDate from the actual visit
            // date instead of leaving it unset — see the comment at its creation.
            DateTime? appointmentDateTime = null;
            if (request.AppointmentId.HasValue)
            {
                var appointment = await _context.Appointments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId.Value, cancellationToken);

                if (appointment == null)
                {
                    throw new KeyNotFoundException($"Appointment with ID '{request.AppointmentId}' not found in global registry.");
                }

                appointmentDateTime = appointment.DateTime;

                if (appointment.PatientId != request.PatientId)
                {
                    throw new InvalidOperationException("Appointment does not belong to the specified patient.");
                }

                var hasLiveInvoice = await _context.Invoices
                    .AnyAsync(i => i.AppointmentId == request.AppointmentId.Value && i.DeletedAt == null, cancellationToken);
                if (hasLiveInvoice)
                {
                    throw new InvalidOperationException("This appointment already has an active invoice.");
                }

                // One invoice per appointment must cover every live service on it.
                // A second invoice for the same appointment is blocked (above), so a
                // service silently left off this one would become permanently
                // unbillable through this appointment — the only thing that would
                // ever pick it up is UpdateAppointmentCommand's edit-time
                // reconciler, which folds it into THIS invoice's total the next
                // time the appointment is edited for any reason, changing what was
                // billed without anyone deliberately choosing to bill it. Enforce
                // full coverage up front instead: remove the SERVICE from the
                // appointment first if it genuinely shouldn't be billed here.
                var liveServiceIds = await _context.AppointmentServices
                    .Where(s => s.AppointmentId == request.AppointmentId.Value && s.DeletedAt == null)
                    .Select(s => s.Id)
                    .ToListAsync(cancellationToken);
                if (liveServiceIds.Count > 0)
                {
                    var coveredIds = request.Items
                        .Where(i => i.AppointmentServiceId.HasValue)
                        .Select(i => i.AppointmentServiceId!.Value)
                        .ToHashSet();
                    var missingCount = liveServiceIds.Count(id => !coveredIds.Contains(id));
                    if (missingCount > 0)
                    {
                        throw new ArgumentException(
                            $"This invoice is missing {missingCount} service(s) from the appointment. " +
                            "An invoice must cover every live service on the visit — remove the service from " +
                            "the appointment itself (Edit) if it shouldn't be billed at all, rather than " +
                            "omitting it from this invoice.");
                    }
                }
            }

            var grossAmount = request.Items.Sum(x => x.Amount * x.Quantity);
            var totalDiscount = request.CentreDiscount + request.ReferrerDiscount;
            
            // Keep the deduction vectors and the aggregate discount auditable.
            // Silently clamping the aggregate would persist a total that no
            // longer matches the centre/referrer amounts the caller submitted.
            if (totalDiscount > grossAmount)
            {
                throw new ArgumentException("Total discount cannot exceed the invoice gross amount.");
            }

            // ServiceDate is what GetFinancialMatrixQuery (Service Performance,
            // Analytics, and every other matrix tab) filters invoices by — it was
            // never set here at all, defaulting to DateTime.MinValue. That made
            // every manually-generated invoice (auto-billing off, or a freeform
            // walk-in with no appointment) permanently invisible to the whole
            // matrix for any date range, forever, while Revenue (which never
            // reads ServiceDate — it uses the appointment date or CreatedAt)
            // correctly counted it. Falls back to "now" for a freeform invoice
            // with no appointment to anchor to, same as the referral commission
            // created below for the same invoice.
            var invoiceCreatedAt = DateTime.UtcNow;
            var invoice = new Invoice
            {
                AppointmentId = request.AppointmentId,
                PatientId = request.PatientId,
                PatientName = patient.FullName ?? "UNKNOWN PATIENT",
                HospitalId = hospitalId,
                InvoiceId = $"INV-{invoiceCreatedAt:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}",
                CentreDiscount = request.CentreDiscount,
                ReferrerDiscount = request.ReferrerDiscount,
                PaidAmount = 0,
                ReferralCutValue = request.CommissionAmount ?? 0,
                Status = "PENDING",
                CreatedAt = invoiceCreatedAt,
                ServiceDate = appointmentDateTime ?? invoiceCreatedAt,
            };

            foreach (var item in request.Items)
            {
                invoice.Items.Add(new InvoiceItem
                {
                    Description = item.Description,
                    Amount = item.Amount,
                    Quantity = item.Quantity,
                    AppointmentServiceId = item.AppointmentServiceId,
                });
            }

            // Canonical recompute (Common/InvoiceTotals.cs). The explicit throw
            // above already guarantees totalDiscount <= grossAmount, so
            // ApplyDiscountAndFinalize's clamp is a no-op on this path — kept
            // anyway so every invoice-total site goes through the one formula.
            InvoiceTotals.RecomputeGross(invoice, 0);
            InvoiceTotals.ApplyDiscountAndFinalize(invoice, totalDiscount);

            _context.Invoices.Add(invoice);

            // Set below only when a NEW commission row is actually added, so the
            // post-save AccumulatedTotal recompute runs only when needed.
            Guid? newCommissionReferrerId = null;

            // Record a Referral Commission whenever a referrer is chosen — even at
            // ₹0 — so the referral is tracked against them and the invoice's referrer
            // (Revenue Hub) matches the Referral Hub. Self / walk-in is skipped below
            // (it earns nothing).
            if (request.ReferrerId.HasValue)
            {
                var referrer = await _context.Referrers
                    .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId.Value, cancellationToken);

                // Self / walk-in earns no commission — never write a payout row
                // for it (mirrors the arrival-billing path). (#19)
                if (referrer != null && !_1Rad.Application.Common.NameNormalizer.SameName(referrer.Name, "Self"))
                {
                    // Guard against a duplicate payout: GenerateBillingOnArrivalAsync
                    // creates per-service commissions on arrival whenever a service
                    // carries a referral cut, EVEN WHEN auto-billing is off (it only
                    // gates invoice creation, not commission creation) — leaving live
                    // commissions with no invoice behind them yet. This command IS
                    // that invoice arriving after the fact for an auto-billing-off
                    // hospital; without this check it would add a second, duplicate
                    // aggregate commission on top of the arrival rows. Not caught by
                    // the DB's UX_ReferralCommissions_Live_AppointmentService unique
                    // index, since this aggregate commission carries no
                    // AppointmentServiceId. If arrival rows already exist, backfill
                    // their reference now that a real invoice exists instead of
                    // creating a second commission.
                    var existingCommissions = request.AppointmentId.HasValue
                        ? await _context.ReferralCommissions
                            .Where(c => c.AppointmentId == request.AppointmentId.Value && c.DeletedAt == null)
                            .ToListAsync(cancellationToken)
                        : new List<ReferralCommission>();

                    if (existingCommissions.Count > 0)
                    {
                        foreach (var c in existingCommissions.Where(c => string.IsNullOrEmpty(c.ReferenceNumber)))
                        {
                            c.ReferenceNumber = invoice.InvoiceId;
                        }
                    }
                    else
                    {
                        var netCommission = (request.CommissionAmount ?? 0) - request.ReferrerDiscount;
                        if (netCommission < 0) netCommission = 0;

                        var commission = new ReferralCommission
                        {
                            ReferrerId = request.ReferrerId.Value,
                            ReferrerName = referrer.Name ?? "Unknown",
                            Modality = invoice.Items.FirstOrDefault()?.Description ?? "GENERAL",
                            PatientName = patient.FullName ?? "N/A",
                            CommissionAmount = netCommission,
                            // Filled in below via ReferralLedger (the old inline sum
                            // here counted soft-deleted commissions too, inflating
                            // this figure whenever the referrer had deleted history).
                            AccumulatedTotal = 0,
                            TransactionDate = invoiceCreatedAt,
                            // The visit's actual date, when this invoice is tied to one.
                            // GetReferralCommissionsQuery falls back to TransactionDate
                            // when ServiceDate is left at its default — but
                            // GetFinancialMatrixQuery (Service Performance/Analytics)
                            // filters ServiceDate directly with no such fallback, so a
                            // left-default value made this commission permanently
                            // invisible there for any date range. Fall back to "now"
                            // (matching the invoice's own ServiceDate above) instead of
                            // defaulting, so every reader agrees on which day this
                            // freeform/manual commission belongs to.
                            ServiceDate = appointmentDateTime ?? invoiceCreatedAt,
                            Status = "UNPAID",
                            ReferenceNumber = invoice.InvoiceId,
                            AppointmentId = invoice.AppointmentId,
                            Remarks = $"Manual Invoice Generation for {patient.FullName}" + (request.ReferrerDiscount > 0 ? $" (Ref. Discount: ₹{request.ReferrerDiscount})" : ""),
                            HospitalId = hospitalId
                        };
                        _context.ReferralCommissions.Add(commission);
                        newCommissionReferrerId = request.ReferrerId.Value;
                    }
                }
            }

            await _context.SaveChangesAsync(cancellationToken);

            // ReferralLedger's own query hits the DB, so it has to run after the
            // new commission row above is actually persisted.
            if (newCommissionReferrerId.HasValue)
            {
                await ReferralLedger.RecomputeAccumulatedTotal(_context, newCommissionReferrerId.Value, hospitalId, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            return invoice.Id;
        }
        catch (ArgumentException) { throw; }
        catch (KeyNotFoundException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (DbUpdateException dex)
        {
            var innerMsg = dex.InnerException?.Message ?? dex.Message;
            throw new Exception($"Database synchronization failure: {innerMsg}", dex);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to generate invoice: {ex.Message}", ex);
        }
    }
}
