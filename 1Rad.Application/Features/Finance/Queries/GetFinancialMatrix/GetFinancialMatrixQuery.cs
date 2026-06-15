using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

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

public class GetFinancialMatrixQueryHandler : IRequestHandler<GetFinancialMatrixQuery, FinancialMatrixDto>
{
    private readonly IApplicationDbContext _context;

    public GetFinancialMatrixQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<FinancialMatrixDto> Handle(GetFinancialMatrixQuery request, CancellationToken cancellationToken)
    {
        try
        {
            // Validate hospital context
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new FinancialMatrixDto();
            }

            var hospitalId = _context.UserContext.HospitalId;

            // Base queries with hospital filter
            var invoiceQuery = _context.Invoices.AsNoTracking().Where(i => i.HospitalId == hospitalId);
            var expenseQuery = _context.Expenses.AsNoTracking().Where(e => e.HospitalId == hospitalId);
            var commissionQuery = _context.ReferralCommissions.AsNoTracking().Where(c => c.HospitalId == hospitalId);

            // Canonical date basis (agreed 2026-06-14): invoices are bucketed by
            // ServiceDate (when the scan happened), not CreatedAt. Expenses and
            // commissions keep their own TransactionDate.
            if (request.StartDate.HasValue)
            {
                invoiceQuery = invoiceQuery.Where(i => i.ServiceDate >= request.StartDate.Value);
                expenseQuery = expenseQuery.Where(e => e.TransactionDate >= request.StartDate.Value);
                commissionQuery = commissionQuery.Where(c => c.TransactionDate >= request.StartDate.Value);
            }
            if (request.EndDate.HasValue)
            {
                var end = request.EndDate.Value.Date.AddDays(1).AddTicks(-1);
                invoiceQuery = invoiceQuery.Where(i => i.ServiceDate <= end);
                expenseQuery = expenseQuery.Where(e => e.TransactionDate <= end);
                commissionQuery = commissionQuery.Where(c => c.TransactionDate <= end);
            }

            // Hydrate detailed invoice list joining Modalities and Referrer details
            var invoiceData = await invoiceQuery
                .GroupJoin(_context.Appointments.AsNoTracking(), 
                           i => i.AppointmentId, 
                           a => a.AppointmentId, 
                           (i, appointments) => new { i, appointments })
                .SelectMany(x => x.appointments.DefaultIfEmpty(),
                            (x, a) => new 
                            { 
                                x.i.Id,
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
                                HasReferrer = a != null && !string.IsNullOrEmpty(a.ReferredBy),
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
                paymentQuery = paymentQuery.Where(p => p.CreatedAt >= request.StartDate.Value);
            }
            if (request.EndDate.HasValue)
            {
                var end = request.EndDate.Value.Date.AddDays(1).AddTicks(-1);
                paymentQuery = paymentQuery.Where(p => p.CreatedAt <= end);
            }
            var paymentData = await paymentQuery
                .Select(p => new { p.Amount, p.PaymentMethod })
                .ToListAsync(cancellationToken);

            var collectionChannels = new PaymentChannelBreakdownDto
            {
                // Only true cash channels count toward collected cash. ADVANCE-tagged
                // payments (an invoice paid from a held advance) are EXCLUDED here —
                // no new money moved — and surfaced separately as AdvanceAmount.
                CashAmount = paymentData.Where(p => p.PaymentMethod != null && p.PaymentMethod.Equals("CASH", StringComparison.OrdinalIgnoreCase)).Sum(p => p.Amount),
                UpiAmount = paymentData.Where(p => p.PaymentMethod != null && p.PaymentMethod.Equals("UPI", StringComparison.OrdinalIgnoreCase)).Sum(p => p.Amount),
                CardAmount = paymentData.Where(p => p.PaymentMethod != null && p.PaymentMethod.Equals("CARD", StringComparison.OrdinalIgnoreCase)).Sum(p => p.Amount),
                AdvanceAmount = paymentData.Where(p => p.PaymentMethod != null && p.PaymentMethod.Equals("ADVANCE", StringComparison.OrdinalIgnoreCase)).Sum(p => p.Amount)
            };
            
            if (!invoiceData.Any() && !expenseData.Any() && !paymentData.Any()) return new FinancialMatrixDto();

            // Canonical: "Invoiced" = NET billed (TotalAmount, post-discount); CANCELLED excluded.
            var activeInvoices = invoiceData.Where(i => i.Status != "CANCELLED").ToList();
            var totalLifeTimeInvoiced = activeInvoices.Sum(i => i.TotalAmount);

            // 1. Temporal Aggregations (Daily) — bucketed by ServiceDate.
            var daily = activeInvoices
                .GroupBy(i => i.ServiceDate.Date)
                .Select(g => new
                {
                    Date = g.Key,
                    Invoiced = g.Sum(i => i.TotalAmount),
                    Collected = g.Sum(i => i.PaidAmount)
                })
                .Concat(expenseData.GroupBy(e => e.TransactionDate.Date).Select(g => new { Date = g.Key, Invoiced = 0m, Collected = 0m }))
                .GroupBy(x => x.Date)
                .OrderByDescending(g => g.Key)
                .Select(g => new MatrixItemDto
                {
                    Label = g.Key.ToString("dd-MMM-yyyy"),
                    Invoiced = g.Sum(x => x.Invoiced),
                    Collected = g.Sum(x => x.Collected),
                    Expenses = expenseData.Where(e => e.TransactionDate.Date == g.Key).Sum(e => e.Amount + e.TaxAmount),
                    Pending = g.Sum(x => x.Invoiced - x.Collected),
                    RealizationRate = g.Sum(x => x.Invoiced) > 0
                        ? Math.Min(100, (int)(g.Sum(x => x.Collected) / g.Sum(x => x.Invoiced) * 100))
                        : 0
                }).Take(30).ToList();
            
            // Weekly
            var weekly = activeInvoices
                .GroupBy(i => System.Globalization.ISOWeek.GetWeekOfYear(i.ServiceDate))
                .OrderByDescending(g => g.Key)
                .Select(g => new MatrixItemDto
                {
                    Label = $"Week {g.Key}",
                    Invoiced = g.Sum(i => i.TotalAmount),
                    Collected = g.Sum(i => i.PaidAmount),
                    Expenses = expenseData.Where(e => System.Globalization.ISOWeek.GetWeekOfYear(e.TransactionDate) == g.Key).Sum(e => e.Amount + e.TaxAmount),
                    Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                    RealizationRate = g.Sum(i => i.TotalAmount) > 0
                        ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
                        : 0
                }).Take(8).ToList();

            // Monthly
            var monthly = activeInvoices
                .GroupBy(i => new { i.ServiceDate.Year, i.ServiceDate.Month })
                .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
                .Select(g => new MatrixItemDto
                {
                    Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM yyyy"),
                    Invoiced = g.Sum(i => i.TotalAmount),
                    Collected = g.Sum(i => i.PaidAmount),
                    Expenses = expenseData.Where(e => e.TransactionDate.Year == g.Key.Year && e.TransactionDate.Month == g.Key.Month).Sum(e => e.Amount + e.TaxAmount),
                    Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                    RealizationRate = g.Sum(i => i.TotalAmount) > 0
                        ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
                        : 0
                }).Take(12).ToList();

            // Yearly
            var yearly = activeInvoices
                .GroupBy(i => i.ServiceDate.Year)
                .OrderByDescending(g => g.Key)
                .Select(g => new MatrixItemDto
                {
                    Label = g.Key.ToString(),
                    Invoiced = g.Sum(i => i.TotalAmount),
                    Collected = g.Sum(i => i.PaidAmount),
                    Expenses = expenseData.Where(e => e.TransactionDate.Year == g.Key).Sum(e => e.Amount + e.TaxAmount),
                    Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                    RealizationRate = g.Sum(i => i.TotalAmount) > 0
                        ? Math.Min(100, (int)(g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount) * 100))
                        : 0
                }).ToList();

            // Modalities breakdown (net revenue)
            var modalityBreakdown = activeInvoices
                .GroupBy(i => i.Modality)
                .Select(g => new ModalityRevenueDto
                {
                    Modality = (g.Key ?? "GENERAL").ToUpper(),
                    RangeRevenue = g.Sum(i => i.TotalAmount),
                    ContributionPercentage = totalLifeTimeInvoiced > 0
                        ? (int)(g.Sum(i => i.TotalAmount) / totalLifeTimeInvoiced * 100)
                        : 0
                })
                .OrderByDescending(x => x.RangeRevenue)
                .ToList();

            // 2. Outstanding AR Aging Buckets — aged from ServiceDate.
            var referenceDate = DateTime.UtcNow;
            var outstandingInvoices = activeInvoices
                .Where(i => i.PaidAmount < i.TotalAmount)
                .Select(i => new
                {
                    Outstanding = i.TotalAmount - i.PaidAmount,
                    AgeInDays = (referenceDate - i.ServiceDate).Days
                })
                .ToList();

            var agingDues = new AgingAnalysisDto
            {
                Bucket0To30 = outstandingInvoices.Where(x => x.AgeInDays <= 30).Sum(x => x.Outstanding),
                Bucket31To60 = outstandingInvoices.Where(x => x.AgeInDays > 30 && x.AgeInDays <= 60).Sum(x => x.Outstanding),
                Bucket61To90 = outstandingInvoices.Where(x => x.AgeInDays > 60 && x.AgeInDays <= 90).Sum(x => x.Outstanding),
                Bucket91Plus = outstandingInvoices.Where(x => x.AgeInDays > 90).Sum(x => x.Outstanding)
            };

            // 3. Discount allocations — sum the real deduction vectors recorded on
            //    each invoice. No more inferring "senior" from the patient's name or
            //    "promotional" from a 15%-of-gross threshold.
            var centreDisc = activeInvoices.Sum(i => i.CentreDiscount);
            var referrerDisc = activeInvoices.Sum(i => i.ReferrerDiscount);
            var institutionalDisc = activeInvoices.Sum(i => i.InstitutionalDeduction);
            var totalDisc = activeInvoices.Sum(i => i.DiscountAmount);
            var otherDisc = Math.Max(0m, totalDisc - (centreDisc + referrerDisc + institutionalDisc));

            var discountAllocations = new DiscountDistributionDto
            {
                Centre = centreDisc,
                Referrer = referrerDisc,
                Institutional = institutionalDisc,
                Other = otherDisc
            };

            // 4. Concession leakages
            var leakageAudits = activeInvoices
                .Where(i => i.HasReferrer && !string.IsNullOrEmpty(i.ReferredBy))
                .GroupBy(i => i.ReferredBy!)
                .Select(g => new DiscountLeakageAuditorDto
                {
                    DoctorName = g.Key,
                    TotalDiscountApproved = g.Sum(x => x.DiscountAmount),
                    TotalBilledRevenue = g.Sum(x => x.GrossAmount)
                })
                .OrderByDescending(x => x.TotalDiscountApproved)
                .ToList();

            // Calculate total scan counts for proportional distribution of general Radiology expenses
            var totalScans = activeInvoices.Count();
            
            // Map expenses to modalities
            var modalityExpenses = new Dictionary<string, decimal>();
            var generalRadiologyExpenses = 0m;

            foreach (var exp in expenseData)
            {
                var desc = exp.Description ?? "";
                var cc = exp.CostCenter ?? "";
                var cat = exp.Category ?? "";

                // 1. Direct allocation by Description/CostCenter keywords
                if (desc.Contains("MRI", StringComparison.OrdinalIgnoreCase) || cc.Equals("MRI", StringComparison.OrdinalIgnoreCase))
                {
                    modalityExpenses["MRI"] = modalityExpenses.GetValueOrDefault("MRI") + (exp.Amount + exp.TaxAmount);
                }
                else if (desc.Contains("CT", StringComparison.OrdinalIgnoreCase) || cc.Equals("CT", StringComparison.OrdinalIgnoreCase))
                {
                    modalityExpenses["CT"] = modalityExpenses.GetValueOrDefault("CT") + (exp.Amount + exp.TaxAmount);
                }
                else if (desc.Contains("X-RAY", StringComparison.OrdinalIgnoreCase) || desc.Contains("XRAY", StringComparison.OrdinalIgnoreCase) || cc.Equals("X-RAY", StringComparison.OrdinalIgnoreCase) || cc.Equals("XRAY", StringComparison.OrdinalIgnoreCase))
                {
                    modalityExpenses["X-RAY"] = modalityExpenses.GetValueOrDefault("X-RAY") + (exp.Amount + exp.TaxAmount);
                }
                else if (desc.Contains("USG", StringComparison.OrdinalIgnoreCase) || desc.Contains("ULTRASOUND", StringComparison.OrdinalIgnoreCase) || cc.Equals("USG", StringComparison.OrdinalIgnoreCase))
                {
                    modalityExpenses["USG"] = modalityExpenses.GetValueOrDefault("USG") + (exp.Amount + exp.TaxAmount);
                }
                // 2. Department-level allocation (Radiology general overhead)
                else if (cc.Equals("Radiology", StringComparison.OrdinalIgnoreCase) || cat.Equals("Maintenance", StringComparison.OrdinalIgnoreCase))
                {
                    generalRadiologyExpenses += (exp.Amount + exp.TaxAmount);
                }
            }

            // 5. Service Profitability Matrix with Collection Efficiency
            var modalityProfitability = activeInvoices
                .GroupBy(i => i.Modality)
                .Select(g =>
                {
                    var mod = (g.Key ?? "GENERAL").ToUpper();
                    var count = g.Count();
                    var gross = g.Sum(x => x.GrossAmount);
                    var cut = g.Sum(x => x.ReferralCutValue);
                    var net = g.Sum(x => x.GrossAmount) - cut;
                    var paid = g.Sum(x => x.PaidAmount);

                    // Allocate operating costs
                    var directCost = modalityExpenses.GetValueOrDefault(mod, 0m);
                    var proportionalShare = totalScans > 0 ? (decimal)count / totalScans * generalRadiologyExpenses : 0m;
                    var operatingCost = directCost + proportionalShare;

                    // Net operating profit
                    var netOpProfit = net - operatingCost;
                    var opMarginPct = net > 0 ? (double)Math.Round((netOpProfit / net) * 100, 1) : 0;
                    var roi = operatingCost > 0 ? (double)Math.Round(gross / operatingCost, 1) : 0;

                    // Break-even scans needed: operating cost divided by average net yield per scan
                    var avgNetYield = count > 0 ? net / count : 0m;
                    var breakEven = avgNetYield > 0 ? Math.Round(operatingCost / avgNetYield, 1) : 0m;

                    return new ModalityProfitabilityDto
                    {
                        Modality = mod,
                        ScanCount = count,
                        GrossRevenue = gross,
                        ReferralCut = cut,
                        NetRevenue = net,
                        MarginPercentage = gross > 0 ? (double)Math.Round((net / gross) * 100, 1) : 0,
                        CollectionEfficiency = gross > 0 ? (double)Math.Round((paid / gross) * 100, 1) : 0,
                        OperatingCost = operatingCost,
                        NetOperatingProfit = netOpProfit,
                        OperatingMarginPercentage = opMarginPct,
                        EquipmentRoiRatio = roi,
                        BreakEvenScansNeeded = breakEven
                    };
                })
                .OrderByDescending(m => m.GrossRevenue)
                .ToList();

            // 6. Monthly patient acquisition cohorts (New vs Returning), by ServiceDate.
            // A patient is "new" in the month of their first-ever service and
            // "returning" in any later month (distinct patients per month). No
            // synthetic fallback — an empty practice returns an empty list.
            var firstServiceMonth = activeInvoices
                .GroupBy(i => i.PatientId)
                .ToDictionary(
                    g => g.Key,
                    g => { var f = g.Min(x => x.ServiceDate); return (f.Year, f.Month); });

            var monthlyCohorts = activeInvoices
                .GroupBy(i => new { i.ServiceDate.Year, i.ServiceDate.Month })
                .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
                .Take(6)
                .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
                .ToList();

            var patientAcquisitionBreakdown = new List<PatientAcquisitionCohortDto>();
            foreach (var monthGroup in monthlyCohorts)
            {
                var label = new DateTime(monthGroup.Key.Year, monthGroup.Key.Month, 1).ToString("MMM");
                int newCount = 0;
                int retCount = 0;
                foreach (var patientId in monthGroup.Select(i => i.PatientId).Distinct())
                {
                    var fm = firstServiceMonth[patientId];
                    if (fm.Year == monthGroup.Key.Year && fm.Month == monthGroup.Key.Month) newCount++;
                    else retCount++;
                }
                patientAcquisitionBreakdown.Add(new PatientAcquisitionCohortDto
                {
                    MonthLabel = label,
                    NewPatientsCount = newCount,
                    ReturningPatientsCount = retCount
                });
            }

            // 7. Physician ROI ledger (net revenue per referrer)
            var doctorRevenue = activeInvoices
                .Where(i => i.HasReferrer && !string.IsNullOrEmpty(i.ReferredBy))
                .GroupBy(i => i.ReferredBy!)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.TotalAmount));

            var doctorCommissions = commissionData
                .Where(c => !string.IsNullOrEmpty(c.ReferrerName))
                .GroupBy(c => c.ReferrerName)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.CommissionAmount));

            var physicianRoiLedger = new List<PhysicianRoiDto>();
            foreach (var doc in doctorRevenue)
            {
                doctorCommissions.TryGetValue(doc.Key, out var comm);
                physicianRoiLedger.Add(new PhysicianRoiDto
                {
                    DoctorName = doc.Key,
                    BilledRevenue = doc.Value,
                    CommissionPaid = comm
                });
            }
            physicianRoiLedger = physicianRoiLedger.OrderByDescending(r => r.BilledRevenue).ToList();

            // 8. Base Clinical Performance Stats
            var totalGross = activeInvoices.Sum(i => i.GrossAmount);   // list value (pre-discount)
            var totalNet = activeInvoices.Sum(i => i.TotalAmount);     // net revenue (post-discount)
            var totalPaid = activeInvoices.Sum(i => i.PaidAmount);
            var totalDiscount = activeInvoices.Sum(i => i.DiscountAmount);
            var totalExpenses = expenseData.Sum(e => e.Amount + e.TaxAmount);

            var performance = new ClinicPerformanceDto
            {
                GrossRevenue = totalGross,
                CashCollected = totalPaid,
                ConcessionLeakage = totalDiscount,
                LeakagePercentage = totalGross > 0 ? (double)Math.Round((totalDiscount / totalGross) * 100, 1) : 0,
                OutstandingAR = activeInvoices.Sum(i => i.TotalAmount - i.PaidAmount),
                ExpenseRatio = totalPaid > 0 ? (double)Math.Round((totalExpenses / totalPaid) * 100, 1) : 0,
                AverageRevenuePerScan = activeInvoices.Any() ? Math.Round(totalNet / activeInvoices.Count(), 2) : 0,
                TotalScansCount = activeInvoices.Count()
            };

            // Referral split summary (net revenue)
            var referredInvoices = activeInvoices.Where(i => i.HasReferrer).ToList();
            var directInvoices = activeInvoices.Where(i => !i.HasReferrer).ToList();

            var referralContribution = new ReferralContributionDto
            {
                ReferredRevenue = referredInvoices.Sum(i => i.TotalAmount),
                DirectRevenue = directInvoices.Sum(i => i.TotalAmount),
                ReferralRatio = totalNet > 0 ? (double)Math.Round((referredInvoices.Sum(i => i.TotalAmount) / totalNet) * 100, 1) : 0,
                ReferredScansCount = referredInvoices.Count(),
                DirectScansCount = directInvoices.Count()
            };

            // 6. Patient Lifetime Value (LTV) & Cohort Retention Calculations
            var patientInvoicesGrouped = activeInvoices
                .GroupBy(i => i.PatientId)
                .Select(g => new
                {
                    PatientId = g.Key,
                    PatientName = g.First().PatientName,
                    FirstVisit = g.Min(x => x.ServiceDate),
                    Visits = g.Select(x => x.ServiceDate).ToList(),
                    TotalRevenue = g.Sum(x => x.TotalAmount)
                })
                .ToList();

            var totalInvoicesCount = activeInvoices.Count;
            var totalGrossRevenue = activeInvoices.Sum(i => i.TotalAmount);
            var uniquePatientCount = patientInvoicesGrouped.Count;

            var aov = totalInvoicesCount > 0 ? totalGrossRevenue / totalInvoicesCount : 0m;
            var pf = uniquePatientCount > 0 ? (double)totalInvoicesCount / uniquePatientCount : 0;
            var pv = aov * (decimal)pf;
            var estimatedLtv = pv * 3.0m; // 3-year projected lifespan

            var highValueCount = 0;
            var highValueRev = 0m;
            var midValueCount = 0;
            var midValueRev = 0m;
            var lowValueCount = 0;
            var lowValueRev = 0m;

            foreach (var p in patientInvoicesGrouped)
            {
                if (p.TotalRevenue >= 15000m)
                {
                    highValueCount++;
                    highValueRev += p.TotalRevenue;
                }
                else if (p.TotalRevenue >= 5000m)
                {
                    midValueCount++;
                    midValueRev += p.TotalRevenue;
                }
                else
                {
                    lowValueCount++;
                    lowValueRev += p.TotalRevenue;
                }
            }

            var ltvSegments = new List<LtvSegmentDto>
            {
                new LtvSegmentDto { Tier = "High Value", PatientCount = highValueCount, TotalRevenue = highValueRev, Percentage = uniquePatientCount > 0 ? Math.Round((double)highValueCount / uniquePatientCount * 100, 1) : 0 },
                new LtvSegmentDto { Tier = "Mid Value", PatientCount = midValueCount, TotalRevenue = midValueRev, Percentage = uniquePatientCount > 0 ? Math.Round((double)midValueCount / uniquePatientCount * 100, 1) : 0 },
                new LtvSegmentDto { Tier = "Low Value", PatientCount = lowValueCount, TotalRevenue = lowValueRev, Percentage = uniquePatientCount > 0 ? Math.Round((double)lowValueCount / uniquePatientCount * 100, 1) : 0 }
            };

            var cohortHeatmap = new List<RetentionCohortDto>();
            var cohortGroups = patientInvoicesGrouped
                .GroupBy(p => p.FirstVisit.ToString("yyyy-MM"))
                .OrderBy(g => g.Key)
                .Take(6)
                .ToList();

            foreach (var cg in cohortGroups)
            {
                var cohortMonth = cg.Key;
                var cohortPatients = cg.ToList();
                var size = cohortPatients.Count;

                var rates = new List<double> { 100.0 };

                // Safely parse cohort year and month
                var parts = cohortMonth.Split('-');
                var year = parts.Length > 0 && int.TryParse(parts[0], out var y) ? y : DateTime.UtcNow.Year;
                var month = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : DateTime.UtcNow.Month;
                var cohortStartDateTime = new DateTime(year, month, 1);

                for (int offset = 1; offset <= 5; offset++)
                {
                    var targetMonthStart = cohortStartDateTime.AddMonths(offset);
                    var targetMonthEnd = targetMonthStart.AddMonths(1).AddTicks(-1);

                    var activeCount = cohortPatients
                        .Count(p => p.Visits.Any(v => v >= targetMonthStart && v <= targetMonthEnd));

                    var rate = size > 0 ? Math.Round((double)activeCount / size * 100, 1) : 0;
                    rates.Add(rate);
                }

                cohortHeatmap.Add(new RetentionCohortDto
                {
                    CohortMonth = cohortMonth,
                    Size = size,
                    RetentionRates = rates
                });
            }

            var churnAlerts = new List<PatientChurnAlertDto>();
            var localNow = DateTime.UtcNow;

            foreach (var group in activeInvoices.GroupBy(i => i.PatientId))
            {
                var invoices = group.OrderByDescending(i => i.ServiceDate).ToList();
                var lastInvoice = invoices.First();
                var daysSince = (localNow - lastInvoice.ServiceDate).Days;

                if (daysSince > 45 && daysSince <= 180)
                {
                    var name = lastInvoice.PatientName ?? "Anonymous Patient";
                    churnAlerts.Add(new PatientChurnAlertDto
                    {
                        PatientName = name,
                        LastModality = lastInvoice.Modality,
                        LastScanDate = lastInvoice.ServiceDate,
                        DaysSinceLastScan = daysSince,
                        RiskLevel = daysSince > 90 ? "CRITICAL" : "ELEVATED"
                    });
                }
            }

            churnAlerts = churnAlerts.OrderByDescending(c => c.DaysSinceLastScan).Take(3).ToList();

            var patientLtv = new PatientLtvDto
            {
                AverageOrderValue = Math.Round(aov, 2),
                PurchaseFrequency = Math.Round(pf, 2),
                PatientValue = Math.Round(pv, 2),
                EstimatedLifetimeValue = Math.Round(estimatedLtv, 2),
                Segments = ltvSegments,
                RetentionHeatmap = cohortHeatmap,
                ChurnAlerts = churnAlerts
            };

            return new FinancialMatrixDto
            {
                Daily = daily,
                Weekly = weekly,
                Monthly = monthly,
                Yearly = yearly,
                ModalityBreakdown = modalityBreakdown,
                Performance = performance,
                ModalityProfitability = modalityProfitability,
                ReferralContribution = referralContribution,
                AgingDues = agingDues,
                DiscountAllocations = discountAllocations,
                LeakageAudits = leakageAudits,
                PatientAcquisitionBreakdown = patientAcquisitionBreakdown,
                PhysicianRoiLedger = physicianRoiLedger,
                CollectionChannels = collectionChannels,
                PatientLtv = patientLtv
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve financial matrix: {ex.Message}", ex);
        }
    }
}
