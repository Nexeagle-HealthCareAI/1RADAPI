using _1Rad.Application.Interfaces;
using _1Rad.Infrastructure.Authentication;
using _1Rad.Infrastructure.Persistence;
using _1Rad.Infrastructure.Services;
using _1Rad.Infrastructure.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace _1Rad.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                // Azure SQL drops idle connections and throttles under load;
                // without this, a transient disconnect failed the whole request
                // (including a booking) with no retry. EnableRetryOnFailure wraps
                // each operation in an execution strategy that retries the known
                // transient SQL error codes with exponential back-off.
                sql => sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null)));

        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());
        
        services.AddHttpClient<ISmsService, WhatsAppSmsService>();
        services.AddHttpClient<IAnthropicService, AnthropicService>();
        // Gemini powers the report editor's AI co-pilot. 20s timeout so a slow
        // provider can never hang report delivery (the handler falls back to the
        // raw text and the radiologist formats manually).
        services.AddHttpClient<IReportAiService, GeminiService>(c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<IJwtProvider, JwtProvider>();
        services.AddSingleton<ITrackingTokenService, TrackingTokenService>();
        services.AddSingleton<IReferralLinkTokenService, ReferralLinkTokenService>();

        // Session management — the active-session cache is process-local;
        // when we scale to multi-instance, swap the IActiveSessionCache
        // implementation for a Redis-backed one and the rest of the system
        // keeps working as-is.
        services.AddMemoryCache();
        services.AddSingleton<IActiveSessionCache, ActiveSessionCache>();
        services.AddScoped<ISessionAlertService, SessionAlertService>();
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<IBlobService, AzureBlobService>();
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, UserContext>();

        // DICOM viewer Option C: per-slice extraction pipeline.
        services.AddSingleton<IDicomExtractionQueue, DicomExtractionQueue>();
        services.AddScoped<IDicomExtractionService, DicomExtractionService>();
        services.AddHostedService<DicomExtractionWorker>();
        services.AddHostedService<DicomExtractionBackfillJob>();

        services.AddHostedService<DailyFinancialReportJob>();
        services.AddHostedService<DailyReferralExcelReportJob>();
        services.AddHostedService<SubscriptionLifecycleJob>();

        return services;
    }
}
