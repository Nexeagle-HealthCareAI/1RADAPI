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
        // Grammar engine — keeps PHI on-network. Modes (no third party, no LLM):
        //   LanguageTool:Mode = "dotnet"   → pure-.NET in-process checker (no deps) [default]
        //   LanguageTool:Mode = "embedded" → in-process IKVM LanguageTool engine
        //   otherwise                      → HTTP proxy to a self-hosted LanguageTool
        // If none is reachable the controller falls back per LanguageTool:GrammarLlmFallback.
        var ltMode = configuration["LanguageTool:Mode"];
        if (string.Equals(ltMode, "embedded", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ILanguageToolService, EmbeddedLanguageToolService>();
        }
        else if (string.Equals(ltMode, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ILanguageToolService, RadiologyGrammarService>();
        }
        else
        {
            services.AddHttpClient<ILanguageToolService, LanguageToolService>(c => c.Timeout = TimeSpan.FromSeconds(15));
        }
        // RadAI report formatter knowledge pack (templates + lexicon + examples),
        // loaded once from Resources/Radiology. Singleton — pure in-memory data.
        services.AddSingleton<IRadiologyPack, RadiologyPack>();
        // RadLex + curated radiology term corpus (whitelist + wrong→correct map),
        // loaded once from Resources/Radiology. Powers the Layer-1 spell pass,
        // autocomplete, and spell-check. Singleton — pure in-memory data.
        services.AddSingleton<IRadiologyCorpus, RadiologyCorpus>();
        // RadAI help-desk knowledge pack (system prompt + app_knowledge.json),
        // loaded once from Resources/RadAI. Singleton — pure in-memory data.
        services.AddSingleton<IRadAiKnowledge, RadAiKnowledge>();
        // RadAI response cache — repeated questions skip the model entirely
        // (token-optimization layer). Backed by the AddMemoryCache below.
        services.AddSingleton<IRadAiResponseCache, RadAiResponseCache>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<IJwtProvider, JwtProvider>();
        services.AddSingleton<ITrackingTokenService, TrackingTokenService>();
        services.AddSingleton<IStudyShareTokenService, StudyShareTokenService>();
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
        // Storage backend is pluggable: "Minio" (self-hosted on the VM) or "Azure"
        // (legacy Azure Blob). Both implement IBlobService identically, so nothing
        // downstream changes — the proxy-asset reads, AssetUrlSigner, extraction and
        // metering all work against either. Defaults to Azure for back-compat.
        var storageProvider = (configuration["Storage:Provider"] ?? "Azure").Trim().ToLowerInvariant();
        if (storageProvider == "minio")
            services.AddScoped<IBlobService, MinioBlobService>();
        else
            services.AddScoped<IBlobService, AzureBlobService>();
        // Signs short-lived capability URLs for proxy-asset reads of the private
        // PHI container (stateless — singleton).
        services.AddSingleton<IAssetUrlSigner, AssetUrlSigner>();
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, UserContext>();

        // Product-module entitlements (RIS / PACS SKUs). Scoped because it
        // reads through the scoped DbContext; the cache behind it is shared.
        services.AddScoped<IModuleEntitlementService, ModuleEntitlementService>();

        // PACS storage metering (Phase 3) — usage sums + quota checks.
        services.AddScoped<IStorageMeteringService, StorageMeteringService>();

        // PACS-only server-side study↔patient/appointment matching.
        services.AddScoped<IStudyMatchingService, StudyMatchingService>();

        // Tier seat/site caps (Starter/Growth/Clinic enforcement).
        services.AddScoped<ISubscriptionLimitsService, SubscriptionLimitsService>();

        // DICOM viewer Option C: per-slice extraction pipeline.
        services.AddSingleton<IDicomExtractionQueue, DicomExtractionQueue>();
        services.AddScoped<IDicomExtractionService, DicomExtractionService>();
        services.AddHostedService<DicomExtractionWorker>();
        services.AddHostedService<DicomExtractionBackfillJob>();

        services.AddHostedService<DailyFinancialReportJob>();
        services.AddHostedService<DailyReferralExcelReportJob>();
        services.AddHostedService<SubscriptionLifecycleJob>();
        services.AddHostedService<BlobOrphanSweepJob>();

        return services;
    }
}
