using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix;

public record GetFinancialMatrixQuery : IRequest<FinancialMatrixDto>
{
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
}

public class FinancialMatrixDto
{
    public List<MatrixItemDto> Daily { get; set; } = new();
    public List<MatrixItemDto> Weekly { get; set; } = new();
    public List<MatrixItemDto> Monthly { get; set; } = new();
    public List<MatrixItemDto> Yearly { get; set; } = new();
    public List<ModalityRevenueDto> ModalityBreakdown { get; set; } = new();
    public ClinicPerformanceDto Performance { get; set; } = new();
    public List<ModalityProfitabilityDto> ModalityProfitability { get; set; } = new();
    public ReferralContributionDto ReferralContribution { get; set; } = new();
    public AgingAnalysisDto AgingDues { get; set; } = new();
    public DiscountDistributionDto DiscountAllocations { get; set; } = new();
    public List<DiscountLeakageAuditorDto> LeakageAudits { get; set; } = new();
    public List<PatientAcquisitionCohortDto> PatientAcquisitionBreakdown { get; set; } = new();
    public List<PhysicianRoiDto> PhysicianRoiLedger { get; set; } = new();
    public PaymentChannelBreakdownDto CollectionChannels { get; set; } = new();
    public PatientLtvDto PatientLtv { get; set; } = new();
}

public class MatrixItemDto
{
    public string Label { get; set; } = string.Empty;
    public decimal Invoiced { get; set; }
    public decimal Collected { get; set; }
    public decimal Pending { get; set; }
    public decimal Expenses { get; set; }
    // Cash-basis per period: money collected minus expenses recorded in the period.
    // (Per-period referral commissions are not bucketed here; the headline net
    // profit in /finance/stats is the full cash-basis figure incl. commissions.)
    public decimal NetProfit => Collected - Expenses;
    public int RealizationRate { get; set; }
}

public class ModalityRevenueDto
{
    public string Modality { get; set; } = string.Empty;
    public decimal RangeRevenue { get; set; }
    public int ContributionPercentage { get; set; }
}

public class ClinicPerformanceDto
{
    public decimal GrossRevenue { get; set; }
    public decimal CashCollected { get; set; }
    public decimal ConcessionLeakage { get; set; }
    public double LeakagePercentage { get; set; }
    public decimal OutstandingAR { get; set; }
    public double ExpenseRatio { get; set; }
    public decimal AverageRevenuePerScan { get; set; }
    public int TotalScansCount { get; set; }
}

public class ModalityProfitabilityDto
{
    public string Modality { get; set; } = string.Empty;
    public int ScanCount { get; set; }
    public decimal GrossRevenue { get; set; }
    public decimal ReferralCut { get; set; }
    public decimal NetRevenue { get; set; }
    public double MarginPercentage { get; set; }
    public double CollectionEfficiency { get; set; }
    public decimal OperatingCost { get; set; }
    public decimal NetOperatingProfit { get; set; }
    public double OperatingMarginPercentage { get; set; }
    public double EquipmentRoiRatio { get; set; }
    public decimal BreakEvenScansNeeded { get; set; }
    public List<ServiceProfitabilityDto> Services { get; set; } = new();
}

public class ServiceProfitabilityDto
{
    public string ServiceName { get; set; } = string.Empty;
    public int ScanCount { get; set; }
    public decimal GrossRevenue { get; set; }
    public decimal ReferralCut { get; set; }
    public decimal NetRevenue { get; set; }
    public double MarginPercentage { get; set; }
    public double CollectionEfficiency { get; set; }
}

public class ReferralContributionDto
{
    public decimal ReferredRevenue { get; set; }
    public decimal DirectRevenue { get; set; }
    public double ReferralRatio { get; set; }
    public int ReferredScansCount { get; set; }
    public int DirectScansCount { get; set; }
}

public class AgingAnalysisDto
{
    public decimal Bucket0To30 { get; set; }
    public decimal Bucket31To60 { get; set; }
    public decimal Bucket61To90 { get; set; }
    public decimal Bucket91Plus { get; set; }
    public decimal TotalOutstanding => Bucket0To30 + Bucket31To60 + Bucket61To90 + Bucket91Plus;
}

public class PaymentChannelBreakdownDto
{
    public decimal CashAmount { get; set; }
    public decimal UpiAmount { get; set; }
    public decimal CardAmount { get; set; }
    // Invoice settled from a patient's existing advance/credit. This is NOT fresh
    // cash (it came in earlier when the advance was taken), so it is deliberately
    // EXCLUDED from TotalCollected — surfaced only for transparency.
    public decimal AdvanceAmount { get; set; }
    public decimal TotalCollected => CashAmount + UpiAmount + CardAmount;
}

public class DiscountDistributionDto
{
    // Real deduction vectors recorded on the invoice (replaces the old guessed
    // Senior/Corporate/Promotional buckets).
    public decimal Centre { get; set; }         // Invoice.CentreDiscount
    public decimal Referrer { get; set; }       // Invoice.ReferrerDiscount
    public decimal Institutional { get; set; }  // Invoice.InstitutionalDeduction
    public decimal Other { get; set; }          // residual DiscountAmount not in the vectors above
}

public class DiscountLeakageAuditorDto
{
    public string DoctorName { get; set; } = string.Empty;
    public decimal TotalDiscountApproved { get; set; }
    public decimal TotalBilledRevenue { get; set; }
    public double AverageDiscountPercentage => TotalBilledRevenue > 0 ? (double)Math.Round((TotalDiscountApproved / TotalBilledRevenue) * 100, 1) : 0;
    public string RiskLevel => AverageDiscountPercentage > 20 ? "HIGH RISK" : AverageDiscountPercentage > 10 ? "REVIEW" : "NORMAL";
}

public class PatientAcquisitionCohortDto
{
    public string MonthLabel { get; set; } = string.Empty;
    public int NewPatientsCount { get; set; }
    public int ReturningPatientsCount { get; set; }
}

public class PhysicianRoiDto
{
    public string DoctorName { get; set; } = string.Empty;
    public decimal BilledRevenue { get; set; }
    public decimal CommissionPaid { get; set; }
    public double RoiMultiplier => CommissionPaid > 0 ? (double)Math.Round(BilledRevenue / CommissionPaid, 1) : 0;
}

public class PatientLtvDto
{
    public decimal AverageOrderValue { get; set; }
    public double PurchaseFrequency { get; set; }
    public decimal PatientValue { get; set; }
    public decimal EstimatedLifetimeValue { get; set; }
    public List<LtvSegmentDto> Segments { get; set; } = new();
    public List<RetentionCohortDto> RetentionHeatmap { get; set; } = new();
    public List<PatientChurnAlertDto> ChurnAlerts { get; set; } = new();
}

public class LtvSegmentDto
{
    public string Tier { get; set; } = string.Empty; // High Value, Mid Value, Low Value
    public int PatientCount { get; set; }
    public decimal TotalRevenue { get; set; }
    public double Percentage { get; set; }
}

public class RetentionCohortDto
{
    public string CohortMonth { get; set; } = string.Empty;
    public int Size { get; set; }
    public List<double> RetentionRates { get; set; } = new();
}

public class PatientChurnAlertDto
{
    public string PatientName { get; set; } = string.Empty;
    public string LastModality { get; set; } = string.Empty;
    public DateTime LastScanDate { get; set; }
    public int DaysSinceLastScan { get; set; }
    public string RiskLevel { get; set; } = string.Empty;
}

// The handler's ONLY job now is I/O orchestration: fetch rows from the
// DbContext, hydrate them into the plain calculator-facing row shapes
// (Calculators/FinancialMatrixModels.cs), then hand off to one
// single-purpose, independently unit-testable calculator per report card.
// It composes; it doesn't compute. Every calculation this used to inline
// (temporal rollups, aging, discount allocation, leakage audits, modality
// profitability, patient acquisition, physician ROI, clinic performance,
// referral contribution, patient LTV/retention/churn, payment channels) now
// lives in its own class under Calculators/, injected via its interface —
// swapping an implementation, or adding a new report card, never requires
// touching this method.
public class GetFinancialMatrixQueryHandler : IRequestHandler<GetFinancialMatrixQuery, FinancialMatrixDto>
{
    private readonly IApplicationDbContext _context;
    private readonly ITemporalAggregationCalculator _temporalCalculator;
    private readonly IModalityRevenueCalculator _modalityRevenueCalculator;
    private readonly IAgingAnalysisCalculator _agingCalculator;
    private readonly IDiscountAllocationCalculator _discountCalculator;
    private readonly ILeakageAuditCalculator _leakageCalculator;
    private readonly IModalityProfitabilityCalculator _profitabilityCalculator;
    private readonly IPatientAcquisitionCalculator _patientAcquisitionCalculator;
    private readonly IPhysicianRoiCalculator _physicianRoiCalculator;
    private readonly IClinicPerformanceCalculator _clinicPerformanceCalculator;
    private readonly IReferralContributionCalculator _referralContributionCalculator;
    private readonly IPatientLtvCalculator _patientLtvCalculator;
    private readonly IPaymentChannelCalculator _paymentChannelCalculator;

    public GetFinancialMatrixQueryHandler(
        IApplicationDbContext context,
        ITemporalAggregationCalculator temporalCalculator,
        IModalityRevenueCalculator modalityRevenueCalculator,
        IAgingAnalysisCalculator agingCalculator,
        IDiscountAllocationCalculator discountCalculator,
        ILeakageAuditCalculator leakageCalculator,
        IModalityProfitabilityCalculator profitabilityCalculator,
        IPatientAcquisitionCalculator patientAcquisitionCalculator,
        IPhysicianRoiCalculator physicianRoiCalculator,
        IClinicPerformanceCalculator clinicPerformanceCalculator,
        IReferralContributionCalculator referralContributionCalculator,
        IPatientLtvCalculator patientLtvCalculator,
        IPaymentChannelCalculator paymentChannelCalculator)
    {
        _context = context;
        _temporalCalculator = temporalCalculator;
        _modalityRevenueCalculator = modalityRevenueCalculator;
        _agingCalculator = agingCalculator;
        _discountCalculator = discountCalculator;
        _leakageCalculator = leakageCalculator;
        _profitabilityCalculator = profitabilityCalculator;
        _patientAcquisitionCalculator = patientAcquisitionCalculator;
        _physicianRoiCalculator = physicianRoiCalculator;
        _clinicPerformanceCalculator = clinicPerformanceCalculator;
        _referralContributionCalculator = referralContributionCalculator;
        _patientLtvCalculator = patientLtvCalculator;
        _paymentChannelCalculator = paymentChannelCalculator;
    }

    public async Task<FinancialMatrixDto> Handle(GetFinancialMatrixQuery request, CancellationToken cancellationToken)
    {
        try
        {
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new FinancialMatrixDto();
            }

            var hospitalId = _context.UserContext.HospitalId;

            var invoiceQuery = _context.Invoices.AsNoTracking().Where(i => i.HospitalId == hospitalId);
            var expenseQuery = _context.Expenses.AsNoTracking().Where(e => e.HospitalId == hospitalId);
            // DeletedAt/Status filter matches the convention used everywhere else this
            // table is read (commissionByServiceId/commissionByAppointmentId below,
            // GetReferralCommissionsQuery, InvoiceEnrichmentService, GetDoctorPortalQuery).
            // Without it, a commission line soft-deleted by RecordReferralCommissionsCommand
            // (a payout line removed/replaced, e.g. a corrected modality) keeps its nonzero
            // CommissionAmount and DeletedAt-only tombstone — that stale amount then
            // permanently double-counts into the Physician ROI Ledger below even though
            // it's already gone from the Referral Hub and every other commission view.
            var commissionQuery = _context.ReferralCommissions.AsNoTracking()
                .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null && c.Status != "Cancelled");

            // Canonical date basis (agreed 2026-06-14, revised — commissions
            // moved off TransactionDate): invoices AND commissions are bucketed
            // by ServiceDate (when the scan happened); expenses keep their own
            // TransactionDate since a standalone expense has no analogous visit
            // date. Commissions used to filter by TransactionDate (when the
            // commission row was recorded, essentially "now" at creation) —
            // that let the same visit's invoice and referral commission land in
            // different day/range buckets on this exact query whenever billing
            // happened on a different calendar day than the visit itself (late
            // billing, backdated entry), the same class of bug already fixed for
            // the Referral Hub's own date filtering (useBillingData.js).
            // StartDate/EndDate arrive as a bare "YYYY-MM-DD" (a date picker, or
            // the frontend's own getIstDateStr()) — model-bound to a
            // DateTimeKind.Unspecified midnight with no timezone info at all.
            // ServiceDate/TransactionDate are real UTC instants. Comparing the
            // two directly silently treats "Sept 16" as midnight UTC (5:30am
            // IST) instead of midnight IST, shifting every day boundary by 5.5
            // hours — see IstDateRange's doc comment for the full picture.
            if (request.StartDate.HasValue)
            {
                var start = IstDateRange.ToUtcStart(request.StartDate.Value);
                invoiceQuery = invoiceQuery.Where(i => i.ServiceDate >= start);
                expenseQuery = expenseQuery.Where(e => e.TransactionDate >= start);
                commissionQuery = commissionQuery.Where(c => c.ServiceDate >= start);
            }
            if (request.EndDate.HasValue)
            {
                var end = IstDateRange.ToUtcEndInclusive(request.EndDate.Value);
                invoiceQuery = invoiceQuery.Where(i => i.ServiceDate <= end);
                expenseQuery = expenseQuery.Where(e => e.TransactionDate <= end);
                commissionQuery = commissionQuery.Where(c => c.ServiceDate <= end);
            }

            var invoiceData = await invoiceQuery
                .GroupJoin(_context.Appointments.AsNoTracking(),
                           i => i.AppointmentId,
                           a => a.AppointmentId,
                           (i, appointments) => new { i, appointments })
                .SelectMany(x => x.appointments.DefaultIfEmpty(),
                            (x, a) => new
                            {
                                x.i.Id,
                                x.i.AppointmentId,
                                x.i.InvoiceId,
                                x.i.PatientId,
                                x.i.PatientName,
                                x.i.GrossAmount,
                                x.i.DiscountAmount,
                                x.i.TotalAmount,
                                x.i.PaidAmount,
                                x.i.CreatedAt,
                                x.i.ServiceDate,
                                x.i.ReferralCutValue,
                                x.i.CentreDiscount,
                                x.i.ReferrerDiscount,
                                x.i.InstitutionalDeduction,
                                x.i.Status,
                                Modality = a != null ? a.Modality : "GENERAL",
                                Service = a != null ? a.Service : "OTHER",
                                // "Self" is the app-wide sentinel for a walk-in/self-referred
                                // visit (see UpdateAppointmentCommand/UpdateAppointmentStatusCommand/
                                // ReferrerReassign's isSelf checks) — it is stored in ReferredBy
                                // like any other name, but pays no commission and is not a
                                // referral partner. Without excluding it here, every walk-in
                                // visit was counted as "referred" business: inflating
                                // ReferralContributionCalculator's ratio/revenue, creating a
                                // phantom "SELF" row in PhysicianRoiCalculator's ledger, and
                                // attributing centre discounts to "SELF" in LeakageAuditCalculator.
                                HasReferrer = a != null && !string.IsNullOrEmpty(a.ReferredBy)
                                              && a.ReferredBy.Trim().ToUpper() != "SELF",
                                ReferredBy = a != null ? a.ReferredBy : null
                            })
                .ToListAsync(cancellationToken);

            var expenseData = await expenseQuery
                .Select(e => new { e.Amount, e.TaxAmount, e.TransactionDate, e.Category, e.CostCenter, e.Description })
                .ToListAsync(cancellationToken);

            var commissionData = await commissionQuery
                .Select(c => new { c.ReferrerId, c.ReferrerName, c.CommissionAmount, c.TransactionDate, c.Status })
                .ToListAsync(cancellationToken);

            var paymentQuery = _context.Payments.AsNoTracking().Where(p => p.HospitalId == hospitalId);
            if (request.StartDate.HasValue)
            {
                paymentQuery = paymentQuery.Where(p => p.CreatedAt >= IstDateRange.ToUtcStart(request.StartDate.Value));
            }
            if (request.EndDate.HasValue)
            {
                paymentQuery = paymentQuery.Where(p => p.CreatedAt <= IstDateRange.ToUtcEndInclusive(request.EndDate.Value));
            }
            var paymentData = await paymentQuery
                .Select(p => new { p.Amount, p.PaymentMethod })
                .ToListAsync(cancellationToken);

            if (!invoiceData.Any() && !expenseData.Any() && !paymentData.Any()) return new FinancialMatrixDto();

            // Canonical: "Invoiced" = NET billed (TotalAmount, post-discount); CANCELLED excluded.
            var activeInvoicesRaw = invoiceData.Where(i => i.Status != "CANCELLED").ToList();
            var totalLifeTimeInvoiced = activeInvoicesRaw.Sum(i => i.TotalAmount);

            // Per-service-line data for modality/service breakdowns (Service Performance
            // tab). Invoice.Modality/Service above are denormalised from the appointment's
            // FIRST/primary service only — grouping by those fields silently folds every
            // ADDITIONAL service on a multi-service visit into the primary service's
            // bucket. Group by the real per-line AppointmentService instead so every
            // booked service is counted under its own name. PaidAmount/TotalAmount are
            // only stored at the invoice level, so they're allocated to each line in
            // proportion to that line's share of the invoice's GrossAmount.
            var activeInvoiceIds = activeInvoicesRaw.Select(i => i.Id).ToHashSet();
            var invoiceItemsRaw = await _context.Invoices.AsNoTracking()
                .Where(inv => activeInvoiceIds.Contains(inv.Id))
                .SelectMany(inv => inv.Items,
                            (inv, it) => new { it.InvoiceId, it.AppointmentServiceId, it.Amount, it.Quantity, it.Description })
                .ToListAsync(cancellationToken);

            var lineServiceIds = invoiceItemsRaw
                .Where(it => it.AppointmentServiceId.HasValue)
                .Select(it => it.AppointmentServiceId!.Value)
                .Distinct()
                .ToList();
            var svcLookup = await _context.AppointmentServices.AsNoTracking()
                .Where(s => lineServiceIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Modality, s.ServiceName, s.ReferralCutValue })
                .ToDictionaryAsync(s => s.Id, cancellationToken);

            // Actual earned commission per service — NOT AppointmentService.ReferralCutValue,
            // which is just the configured/nominal rate and stays populated even on a
            // self-referred visit with no referrer to pay. That mismatch was silently
            // inflating the "cut" subtracted in ModalityProfitabilityCalculator for every
            // walk-in/self-referred service, understating net yield well below the real
            // figure (Revenue's Clinic Income, which already sums actual commission rows).
            // "Cancelled" commissions (a voided clawback/reversal) don't count as a real cost.
            var commissionByServiceId = await _context.ReferralCommissions.AsNoTracking()
                .Where(c => c.HospitalId == hospitalId
                         && c.AppointmentServiceId.HasValue
                         && lineServiceIds.Contains(c.AppointmentServiceId.Value)
                         && c.DeletedAt == null
                         && c.Status != "Cancelled")
                .GroupBy(c => c.AppointmentServiceId!.Value)
                .Select(g => new { ServiceId = g.Key, Amount = g.Sum(c => c.CommissionAmount) })
                .ToDictionaryAsync(x => x.ServiceId, x => x.Amount, cancellationToken);

            // Fallback for commissions with no AppointmentServiceId — GenerateInvoiceCommand
            // (manual invoicing / auto-billing-off hospitals) books ONE aggregate commission
            // for the whole invoice rather than one per service line, unlike arrival billing.
            // Without this, every manually-invoiced referred visit's services would show
            // zero commission here despite a real one being owed and paid, overstating net
            // yield for exactly those invoices. Allocated per line the same way Paid/Total
            // already are — proportional to each line's share of the invoice's GrossAmount.
            var activeAppointmentIds = activeInvoicesRaw
                .Where(i => i.AppointmentId.HasValue)
                .Select(i => i.AppointmentId!.Value)
                .ToHashSet();
            var commissionByAppointmentId = await _context.ReferralCommissions.AsNoTracking()
                .Where(c => c.HospitalId == hospitalId
                         && c.AppointmentServiceId == null
                         && c.AppointmentId.HasValue
                         && activeAppointmentIds.Contains(c.AppointmentId.Value)
                         && c.DeletedAt == null
                         && c.Status != "Cancelled")
                .GroupBy(c => c.AppointmentId!.Value)
                .Select(g => new { AppointmentId = g.Key, Amount = g.Sum(c => c.CommissionAmount) })
                .ToDictionaryAsync(x => x.AppointmentId, x => x.Amount, cancellationToken);

            var invoiceById = activeInvoicesRaw.ToDictionary(i => i.Id);

            var serviceLines = invoiceItemsRaw.Select(it =>
            {
                var inv = invoiceById[it.InvoiceId];
                string modality;
                string serviceName;
                bool hasDirectServiceCut;
                decimal referralCut;

                if (it.AppointmentServiceId.HasValue && svcLookup.TryGetValue(it.AppointmentServiceId.Value, out var svc))
                {
                    modality = string.IsNullOrWhiteSpace(svc.Modality) ? "GENERAL" : svc.Modality;
                    serviceName = string.IsNullOrWhiteSpace(svc.ServiceName) ? (string.IsNullOrWhiteSpace(it.Description) ? "OTHER" : it.Description) : svc.ServiceName;
                    hasDirectServiceCut = commissionByServiceId.TryGetValue(it.AppointmentServiceId.Value, out referralCut);
                }
                else
                {
                    // Legacy/manual line with no linked AppointmentService (pre-migration
                    // invoice, or a manually added line) — fall back to the invoice's own
                    // denormalised modality, same as the old behaviour for this case.
                    modality = string.IsNullOrWhiteSpace(inv.Modality) ? "GENERAL" : inv.Modality;
                    serviceName = string.IsNullOrWhiteSpace(it.Description) ? (string.IsNullOrWhiteSpace(inv.Service) ? "OTHER" : inv.Service) : it.Description;
                    hasDirectServiceCut = false;
                    referralCut = 0m;
                }

                var lineGross = it.Amount * it.Quantity;
                var share = inv.GrossAmount > 0 ? lineGross / inv.GrossAmount : 0m;

                // No per-service commission row (either no link, or this specific
                // service has none) — check for an invoice-level aggregate
                // commission (GenerateInvoiceCommand's manual-invoicing path) and
                // allocate it proportionally, same as Paid/Total below.
                if (!hasDirectServiceCut && inv.AppointmentId.HasValue
                    && commissionByAppointmentId.TryGetValue(inv.AppointmentId.Value, out var invoiceCommission))
                {
                    referralCut = invoiceCommission * share;
                }

                return new ServiceLineRow
                {
                    Modality = modality.ToUpper(),
                    ServiceName = serviceName.ToUpper(),
                    Gross = lineGross,
                    Total = inv.GrossAmount > 0 ? lineGross * (inv.TotalAmount / inv.GrossAmount) : lineGross,
                    Paid = inv.GrossAmount > 0 ? inv.PaidAmount * share : 0m,
                    ReferralCut = referralCut
                };
            }).ToList();

            // Hydrate the plain calculator-facing rows once, here — every
            // calculator below takes only these, never the DbContext.
            var activeInvoices = activeInvoicesRaw.Select(i => new InvoiceMatrixRow
            {
                Id = i.Id,
                InvoiceId = i.InvoiceId,
                PatientId = i.PatientId,
                PatientName = i.PatientName,
                GrossAmount = i.GrossAmount,
                DiscountAmount = i.DiscountAmount,
                TotalAmount = i.TotalAmount,
                PaidAmount = i.PaidAmount,
                CreatedAt = i.CreatedAt,
                ServiceDate = i.ServiceDate,
                ReferralCutValue = i.ReferralCutValue,
                CentreDiscount = i.CentreDiscount,
                ReferrerDiscount = i.ReferrerDiscount,
                InstitutionalDeduction = i.InstitutionalDeduction,
                Status = i.Status,
                Modality = i.Modality ?? "GENERAL",
                Service = i.Service ?? "OTHER",
                HasReferrer = i.HasReferrer,
                ReferredBy = i.ReferredBy
            }).ToList();

            var expenseRows = expenseData.Select(e => new ExpenseMatrixRow
            {
                Amount = e.Amount,
                TaxAmount = e.TaxAmount,
                TransactionDate = e.TransactionDate,
                Category = e.Category,
                CostCenter = e.CostCenter,
                Description = e.Description
            }).ToList();

            var commissionRows = commissionData.Select(c => new CommissionMatrixRow
            {
                ReferrerId = c.ReferrerId,
                ReferrerName = c.ReferrerName,
                CommissionAmount = c.CommissionAmount,
                TransactionDate = c.TransactionDate,
                Status = c.Status
            }).ToList();

            var paymentRows = paymentData.Select(p => new PaymentMatrixRow
            {
                Amount = p.Amount,
                PaymentMethod = p.PaymentMethod
            }).ToList();

            var referenceDate = DateTime.UtcNow;

            var temporal = _temporalCalculator.Calculate(activeInvoices, expenseRows);

            return new FinancialMatrixDto
            {
                Daily = temporal.Daily,
                Weekly = temporal.Weekly,
                Monthly = temporal.Monthly,
                Yearly = temporal.Yearly,
                ModalityBreakdown = _modalityRevenueCalculator.Calculate(serviceLines, totalLifeTimeInvoiced),
                Performance = _clinicPerformanceCalculator.Calculate(activeInvoices, expenseRows),
                ModalityProfitability = _profitabilityCalculator.Calculate(serviceLines, expenseRows),
                ReferralContribution = _referralContributionCalculator.Calculate(activeInvoices),
                AgingDues = _agingCalculator.Calculate(activeInvoices, referenceDate),
                DiscountAllocations = _discountCalculator.Calculate(activeInvoices),
                LeakageAudits = _leakageCalculator.Calculate(activeInvoices),
                PatientAcquisitionBreakdown = _patientAcquisitionCalculator.Calculate(activeInvoices),
                PhysicianRoiLedger = _physicianRoiCalculator.Calculate(activeInvoices, commissionRows),
                CollectionChannels = _paymentChannelCalculator.Calculate(paymentRows),
                PatientLtv = _patientLtvCalculator.Calculate(activeInvoices, referenceDate)
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve financial matrix: {ex.Message}", ex);
        }
    }
}
