using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Finance.Queries.GetPendingBillables;

public record GetPendingBillablesQuery : IRequest<List<PendingBillableDto>>
{
    public Guid PatientId { get; init; }
}

public class PendingBillableDto
{
    public Guid AppointmentId { get; set; }
    // Multi-service rollout (batch-2 fix). One row per AppointmentService
    // line so multi-modality visits (X-ray + CT + USG) expose every
    // billable service to the receptionist — not just the primary one.
    // NULL only on visits that somehow have zero AppointmentService rows
    // (legacy pre-migration-57 row whose backfill failed); those still
    // surface as a single fallback DTO using the scalar fields.
    public Guid? AppointmentServiceId { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public DateTime DateTime { get; set; }
    public decimal? Amount { get; set; }
    // Per-service referral cut — frontend forwards it onto the invoice
    // line so the commission row gets created with the right value.
    public decimal? ReferralCutValue { get; set; }
}

public class GetPendingBillablesQueryHandler : IRequestHandler<GetPendingBillablesQuery, List<PendingBillableDto>>
{
    private readonly IApplicationDbContext _context;

    public GetPendingBillablesQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<PendingBillableDto>> Handle(GetPendingBillablesQuery request, CancellationToken cancellationToken)
    {
        try
        {
            // Validate hospital context
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new List<PendingBillableDto>();
            }

            // Validate patient ID
            if (request.PatientId == Guid.Empty)
            {
                throw new ArgumentException("Patient ID is required.", nameof(request.PatientId));
            }

            // Verify patient exists and belongs to the hospital
            var patient = await _context.Patients
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PatientId == request.PatientId && p.HospitalId == _context.UserContext.HospitalId, cancellationToken);

            if (patient == null)
            {
                throw new KeyNotFoundException($"Patient with ID '{request.PatientId}' not found or does not belong to your hospital.");
            }

            // Optimization: Execute the 'Already Invoiced' check as a SQL subquery rather than fetching IDs into memory.
            // This prevents the 'Parameter Limit' crash in SQL Server as the invoice count grows.
            var unbilledAppointments = await _context.Appointments
                .AsNoTracking()
                .Where(a => a.PatientId == request.PatientId &&
                            a.Status != "CANCELLED" &&
                            a.HospitalId == _context.UserContext.HospitalId)
                .Where(a => !_context.Invoices.Any(i => i.AppointmentId == a.AppointmentId && i.Status != "CANCELLED"))
                .ToListAsync(cancellationToken);

            if (unbilledAppointments.Count == 0) return new List<PendingBillableDto>();

            // Batched lookup of every live service line for those visits in
            // one round-trip — same shape as GetAppointmentsQuery's services
            // hydration. We INNER join here at the C# level (skip lines
            // with cancelled status / deleted tombstones) so a partially-
            // cancelled visit doesn't surface its cancelled lines as
            // pending bills.
            var appointmentIds = unbilledAppointments.Select(a => a.AppointmentId).ToList();
            var lines = await _context.AppointmentServices
                .AsNoTracking()
                .Where(s => appointmentIds.Contains(s.AppointmentId)
                         && s.DeletedAt == null
                         && s.Status != "CANCELLED")
                .OrderBy(s => s.UpdatedAt)
                .ToListAsync(cancellationToken);

            // Extract unique modalities and service names required for pricing
            var requiredModalities = lines.Select(l => l.Modality).Distinct().ToList();
            var requiredServices = lines.Select(l => l.ServiceName).Distinct().ToList();
            
            // Add fallback parameters
            foreach (var a in unbilledAppointments)
            {
                if (!string.IsNullOrEmpty(a.Modality) && !requiredModalities.Contains(a.Modality)) requiredModalities.Add(a.Modality);
                if (!string.IsNullOrEmpty(a.Service) && !requiredServices.Contains(a.Service)) requiredServices.Add(a.Service);
            }

            // Map and try to find prices from Service Registry, scoped to tenant and required services
            var registry = await _context.ServiceCharges
                .AsNoTracking()
                .Where(r => r.HospitalId == _context.UserContext.HospitalId && 
                            requiredModalities.Contains(r.Modality) && 
                            requiredServices.Contains(r.ServiceName))
                .ToListAsync(cancellationToken);

            decimal? ResolvePrice(string modality, string serviceName, decimal? fallback)
            {
                if (fallback.HasValue && fallback.Value > 0) return fallback.Value;
                var price = registry.FirstOrDefault(r =>
                    string.Equals(r.Modality, modality, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
                return price?.Amount;
            }

            var result = new List<PendingBillableDto>();

            // Fan out by service line. One DTO per AppointmentService row
            // so the billing drawer surfaces every line the receptionist
            // needs to charge for. Each carries its AppointmentServiceId
            // so the eventual InvoiceItem gets stamped against the right
            // line via the create-invoice command.
            var linesByAppointment = lines
                .GroupBy(s => s.AppointmentId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var a in unbilledAppointments)
            {
                if (linesByAppointment.TryGetValue(a.AppointmentId, out var visitLines) && visitLines.Count > 0)
                {
                    foreach (var line in visitLines)
                    {
                        result.Add(new PendingBillableDto
                        {
                            AppointmentId        = a.AppointmentId,
                            AppointmentServiceId = line.Id,
                            Service              = line.ServiceName,
                            Modality             = line.Modality,
                            DateTime             = a.DateTime,
                            Amount               = ResolvePrice(line.Modality, line.ServiceName, line.Amount),
                            ReferralCutValue     = line.ReferralCutValue > 0 ? (decimal?)line.ReferralCutValue : null,
                        });
                    }
                    continue;
                }

                // Fallback for visits with no AppointmentService rows (a
                // legacy row whose migration-57 backfill failed, or a row
                // booked through a v1-only path that briefly bypassed the
                // service insert). Surface it as a single line keyed on
                // the scalar fields so it still gets billed.
                result.Add(new PendingBillableDto
                {
                    AppointmentId        = a.AppointmentId,
                    AppointmentServiceId = null,
                    Service              = a.Service ?? string.Empty,
                    Modality             = a.Modality ?? string.Empty,
                    DateTime             = a.DateTime,
                    Amount               = ResolvePrice(a.Modality, a.Service, null),
                    ReferralCutValue     = null,
                });
            }

            return result;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve pending billables: {ex.Message}", ex);
        }
    }
}
