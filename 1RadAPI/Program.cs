using System.Text;
using System.Threading.RateLimiting;
using _1Rad.Application;
using _1Rad.Infrastructure;
using _1Rad.Infrastructure.Middleware;
using _1RadAPI.Middleware;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        // Always emit fields (including null/default) so API responses are shape-stable for clients.
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
    });
builder.Services.AddFluentValidationAutoValidation()
                .AddFluentValidationClientsideAdapters();

// Tactical: Increase payload capacity for large DICOM/ZIP clinical assets (500MB)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 524288000; // 500MB
    options.ValueLengthLimit = 524288000;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 524288000; // 500MB
});

builder.Services.AddEndpointsApiExplorer();

// Enhanced Swagger with Security Definitions
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "1Rad Clinical Hub API", Version = "v1" });
    
    // Include XML Comments
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (System.IO.File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

// Configure Rate Limiting (OTP Protection)
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter(policyName: "OtpRateLimit", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(10);
        opt.QueueLimit = 0;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Configure CORS — pulls the allow-list from Cors:AllowedOrigins in
// appsettings.{Environment}.json. Combining a wildcard origin with
// AllowCredentials() is a CSRF risk, so we restrict to the explicit list.
var corsOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowedOrigins", policy =>
    {
        if (corsOrigins.Length == 0)
        {
            // Defensive: in misconfigured environments we keep the API
            // reachable from same-origin only. Logged to startup so it's
            // obvious during smoke tests if the list wasn't loaded.
            Console.WriteLine("[CORS] WARNING — Cors:AllowedOrigins is empty. No cross-origin requests will be accepted.");
        }
        else
        {
            Console.WriteLine($"[CORS] Allowed origins: {string.Join(", ", corsOrigins)}");
        }

        policy.WithOrigins(corsOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials()
              .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
    });
});

// JWT Authentication Configuration
// Reads Jwt:Secret from appsettings.{Environment}.json or environment
// variables (App Service Configuration in Prod). Fails fast if missing —
// no hardcoded fallback secret in the binary, otherwise anyone with read
// access to the repo could forge tokens for environments that forgot to
// configure the secret.
var jwtSettings = builder.Configuration.GetSection("Jwt");
var secretKey = jwtSettings["Secret"];
// `??` alone only catches a MISSING key. An env var set to an empty string —
// e.g. `Jwt__Secret=` rendered from a blank JWT_SECRET CI secret — reads as ""
// (not null), slips past a null check, and reaches
// `new SymmetricSecurityKey([])`, which throws the cryptic
// "IDX10703: key length is zero" at startup. Validate presence AND length here
// so the failure names the real problem instead of crash-looping the container.
if (string.IsNullOrWhiteSpace(secretKey))
    throw new InvalidOperationException(
        "Jwt:Secret is required but was empty or not configured. " +
        "Set it via App Service Configuration (Prod), appsettings.Development.json (local dev), " +
        "or the Jwt__Secret environment variable (check the JWT_SECRET CI secret isn't blank).");
// HMAC-SHA256 needs a key of at least 256 bits (32 bytes); a shorter one throws
// "IDX10653" the first time a token is signed/validated. Fail fast at startup.
if (Encoding.UTF8.GetByteCount(secretKey) < 32)
    throw new InvalidOperationException(
        $"Jwt:Secret is too short ({Encoding.UTF8.GetByteCount(secretKey)} bytes). " +
        "HMAC-SHA256 requires at least 32 bytes (256 bits) — use a longer random secret.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings["Issuer"] ?? "1RadAPI",
        ValidAudience = jwtSettings["Audience"] ?? "1RadClient",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey))
    };
});

// Authorization Policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("InitiationOnly", policy => 
        policy.RequireClaim("type", "initiation"));
    
    options.AddPolicy("AccessOnly", policy => 
        policy.RequireClaim("type", "access"));
});

// Clean Architecture Registrations
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Response compression (Brotli + Gzip — added by default when no providers are
// specified). The big win is the DICOM manifest JSON: a ~400-slice study ships
// ~300 KB of metadata that compresses ~5-10x, so the viewer OPENS faster. Safe
// over HTTPS here — the manifest has no attacker-controlled secret to leak
// (BREACH N/A), and it's Bearer-authed, not cookie-reflected.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true; // App Service serves over HTTPS
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
        .Concat(new[] { "application/json" });
});

var app = builder.Build();

// Ops guard: without CdnBaseUrl the viewer's slice URLs point straight at
// blob storage — no Front Door edge caching AND browsers cap blob origins at
// ~6 parallel HTTP/1.1 connections, which collapses DICOM scroll throughput.
// This is the single biggest viewer-latency lever; make the resolved value
// LOUD at startup so a ":" vs "__" App-Setting keying mistake is obvious in the
// App Service log stream (on Linux a literal ":" key does NOT bind).
var resolvedCdn = _1RadAPI.Controllers.StudyController.ResolveCdnBaseUrl(builder.Configuration);
if (string.IsNullOrWhiteSpace(resolvedCdn))
{
    app.Logger.LogWarning(
        "[CDN] AzureBlobStorage:CdnBaseUrl did NOT resolve — DICOM slice URLs will bypass Front Door " +
        "(no edge caching, HTTP/1.1 only, ~6 parallel requests). On LINUX App Service the setting key " +
        "MUST use double underscore: AzureBlobStorage__CdnBaseUrl (not a colon). Set it to your Front Door " +
        "endpoint, e.g. https://<your-frontdoor>.azurefd.net.");
}
else
{
    app.Logger.LogInformation("[CDN] CdnBaseUrl resolved to {Cdn} — DICOM slices will be served via Front Door.", resolvedCdn);
}

// Configure the HTTP request pipeline.
// CorrelationIdMiddleware MUST run before ExceptionHandlingMiddleware so the
// exception handler can include the correlation ID in error responses.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>(); // Custom Global Exception Handler

// Compress responses early so it wraps every downstream response (esp. the
// DICOM manifest JSON). Must precede the controllers that produce the bodies.
app.UseResponseCompression();

// Enable Swagger in all environments for testing on Azure
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "1Rad API V1");
    c.RoutePrefix = "swagger"; // Standard /swagger path
});

// Redirect root URL (/) to Swagger UI
app.MapGet("/", context =>
{
    context.Response.Redirect("/swagger");
    return Task.CompletedTask;
});

app.UseHttpsRedirection();

app.UseCors("AllowedOrigins"); // Enable CORS

app.UseRateLimiter(); // Apply Rate Limiting

app.UseAuthentication(); // Must be before UseAuthorization
app.UseAuthorization();
// SessionValidationMiddleware runs AFTER UseAuthorization so the principal
// is already populated and signature-validated by the time we check the
// session id. Order matters: this must fire BEFORE any controller that
// touches user state, so we sit it just after the auth pipeline.
app.UseMiddleware<SessionValidationMiddleware>();
app.UseMiddleware<ContextualSentinelMiddleware>();
app.UseMiddleware<SubscriptionValidationMiddleware>();
// Phase B2 Track 2 — server-side dedupe for mutations carrying an
// Idempotency-Key header (the offline outbox sends one on every queued
// push). Runs AFTER session + subscription gates so a replay can't be
// used to slip past those checks; runs BEFORE MapControllers so a hit
// short-circuits the action handler entirely.
app.UseMiddleware<IdempotencyMiddleware>();

app.MapControllers();

app.Run();
