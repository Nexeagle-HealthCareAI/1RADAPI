using MediatR;
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
            if (request.AppointmentId.HasValue)
            {
                var appointment = await _context.Appointments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId.Value, cancellationToken);
                
                if (appointment == null)
                {
                    throw new KeyNotFoundException($"Appointment with ID '{request.AppointmentId}' not found in global registry.");
                }

                if (appointment.PatientId != request.PatientId)
                {
                    throw new InvalidOperationException("Appointment does not belong to the specified patient.");
                }
            }

            var grossAmount = request.Items.Sum(x => x.Amount * x.Quantity);
            var totalDiscount = request.CentreDiscount + request.ReferrerDiscount;
            
            // Security/Business Rule: Discount cannot exceed Gross Amount
            if (totalDiscount > grossAmount)
            {
                totalDiscount = grossAmount;
            }

            var invoice = new Invoice
            {
                AppointmentId = request.AppointmentId,
                PatientId = request.PatientId,
                PatientName = patient.FullName ?? "UNKNOWN PATIENT",
                HospitalId = hospitalId,
                InvoiceId = $"INV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}",
                GrossAmount = grossAmount,
                DiscountAmount = totalDiscount,
                CentreDiscount = request.CentreDiscount,
                ReferrerDiscount = request.ReferrerDiscount,
                TotalAmount = grossAmount - totalDiscount,
                PaidAmount = 0,
                ReferralCutValue = request.CommissionAmount ?? 0,
                Status = "PENDING",
                CreatedAt = DateTime.UtcNow
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

            _context.Invoices.Add(invoice);

            // Record Referral Commission if Referrer is provided
            if (request.ReferrerId.HasValue && (request.CommissionAmount ?? 0) > 0)
            {
                var referrer = await _context.Referrers
                    .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId.Value, cancellationToken);

                if (referrer != null)
                {
                    var currentTotal = await _context.ReferralCommissions
                        .Where(c => c.ReferrerId == request.ReferrerId.Value && c.HospitalId == hospitalId)
                        .SumAsync(c => (decimal?)c.CommissionAmount, cancellationToken) ?? 0;

                    var netCommission = (request.CommissionAmount ?? 0) - request.ReferrerDiscount;
                    if (netCommission < 0) netCommission = 0;

                    var commission = new ReferralCommission
                    {
                        ReferrerId = request.ReferrerId.Value,
                        ReferrerName = referrer.Name ?? "Unknown",
                        Modality = invoice.Items.FirstOrDefault()?.Description ?? "GENERAL",
                        PatientName = patient.FullName ?? "N/A",
                        CommissionAmount = netCommission,
                        AccumulatedTotal = currentTotal + netCommission,
                        TransactionDate = DateTime.UtcNow,
                        Status = "UNPAID",
                        ReferenceNumber = invoice.InvoiceId,
                        AppointmentId = invoice.AppointmentId,
                        Remarks = $"Manual Invoice Generation for {patient.FullName}" + (request.ReferrerDiscount > 0 ? $" (Ref. Discount: ₹{request.ReferrerDiscount})" : ""),
                        HospitalId = hospitalId
                    };
                    _context.ReferralCommissions.Add(commission);
                }
            }

            await _context.SaveChangesAsync(cancellationToken);

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
