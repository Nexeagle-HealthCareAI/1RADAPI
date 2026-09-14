using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

namespace _1Rad.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));
        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
        services.AddAutoMapper(Assembly.GetExecutingAssembly());
        services.AddScoped<_1Rad.Application.Features.Finance.Queries.GetInvoices.IInvoiceEnrichmentService, _1Rad.Application.Features.Finance.Queries.GetInvoices.InvoiceEnrichmentService>();

        // Financial matrix report-card calculators (Phase 1 SOLID split — see
        // GetFinancialMatrixQueryHandler). Each is a stateless, pure calculator
        // over already-hydrated in-memory rows, independently unit-testable
        // without a DbContext. Stateless => safe as scoped or singleton; scoped
        // to match the rest of this registration's lifetime style.
        services.AddScoped<ITemporalAggregationCalculator, TemporalAggregationCalculator>();
        services.AddScoped<IModalityRevenueCalculator, ModalityRevenueCalculator>();
        services.AddScoped<IAgingAnalysisCalculator, AgingAnalysisCalculator>();
        services.AddScoped<IDiscountAllocationCalculator, DiscountAllocationCalculator>();
        services.AddScoped<ILeakageAuditCalculator, LeakageAuditCalculator>();
        services.AddScoped<IModalityExpenseAllocator, ModalityExpenseAllocator>();
        services.AddScoped<IModalityProfitabilityCalculator, ModalityProfitabilityCalculator>();
        services.AddScoped<IPatientAcquisitionCalculator, PatientAcquisitionCalculator>();
        services.AddScoped<IPhysicianRoiCalculator, PhysicianRoiCalculator>();
        services.AddScoped<IClinicPerformanceCalculator, ClinicPerformanceCalculator>();
        services.AddScoped<IReferralContributionCalculator, ReferralContributionCalculator>();
        services.AddScoped<IPatientLtvCalculator, PatientLtvCalculator>();
        services.AddScoped<IPaymentChannelCalculator, PaymentChannelCalculator>();

        return services;
    }
}
