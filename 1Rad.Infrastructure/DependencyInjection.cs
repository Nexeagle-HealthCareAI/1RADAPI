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
            options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));

        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());
        
        services.AddHttpClient<ISmsService, WhatsAppSmsService>();
        services.AddHttpClient<IAnthropicService, AnthropicService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<IJwtProvider, JwtProvider>();
        services.AddSingleton<ITrackingTokenService, TrackingTokenService>();
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
